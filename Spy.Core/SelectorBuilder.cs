using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Spy.Core;

public static class SelectorBuilder
{
    public static string Build(SpyElement element)
    {
        var parts = new List<string>
        {
            $"App(\"{Escape(element.ProcessName)}\")"
        };

        // Priority 1: VCL Chain (if available)
        if (element.VclChain != null && element.VclChain.Count > 0)
        {
            foreach (var node in element.VclChain)
            {
                if (string.Equals(node.ClassName, "TApplication", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(node.ComponentName))
                {
                    parts.Add($".VCL(\"{Escape(node.ComponentName)}\")");
                }
                else
                {
                    parts.Add($".VCL(\"{Escape(node.ClassName)}\")");
                }
            }

            return string.Concat(parts);
        }

        // Priority 2: UIA Chain
        // Clean up UIA tree: remove intermediate containers if they don't add value?
        // For now, keep as is.
        if ((element.UiaParentChain?.Count ?? 0) > 0 || !string.IsNullOrEmpty(element.UiaAutomationId) || !string.IsNullOrEmpty(element.UiaName))
        {
             // Top-level form/window for UIA
             var caption = string.IsNullOrWhiteSpace(element.Win32Text) ? element.UiaName : element.Win32Text;
             if (!string.IsNullOrWhiteSpace(caption))
             {
                 parts.Add($".Form(\"{Escape(caption!)}\")");
             }
             BuildUiaChain(element, parts, element.UiaName ?? element.UiaAutomationId ?? element.UiaClassName);
        }
        else
        {
             // Priority 3: Win32/MSAA fallback
             // Top-level form/window for Win32
             var caption = string.IsNullOrWhiteSpace(element.Win32Text) ? element.UiaName : element.Win32Text;
             if (!string.IsNullOrWhiteSpace(caption))
             {
                 parts.Add($".Form(\"{Escape(caption!)}\")");
             }
             
             bool preferVclFallback = PreferVclFallback(element);
             BuildWin32MsaaFallback(element, parts, preferVclFallback);
        }

        return string.Concat(parts);
    }

    static void BuildUiaChain(SpyElement element, List<string> parts, string? windowCaption)
    {
        var nodes = new List<SpyUiaNode>();
        if (element.UiaParentChain != null && element.UiaParentChain.Count > 0)
            nodes.AddRange(element.UiaParentChain);

        // leaf как последний UIA-узел
        nodes.Add(new SpyUiaNode(
            element.UiaName,
            element.UiaAutomationId,
            element.UiaControlType,
            element.UiaClassName,
            element.UiaFrameworkId));

        bool isChromium = IsChromium(element);

        string? lastMethod = null;
        string? lastArg = null;

        foreach (var node in nodes)
        {
            var ctRaw = node.ControlType;
            var ct = NormalizeControlType(ctRaw);
            var method = MapControlTypeToMethod(ct);

            // Window-узлы внутри формы не дублируем как Form()
            if (ct.Equals("Window", StringComparison.OrdinalIgnoreCase))
                continue;

            // Контейнеры без хорошего идентификатора зачастую не нужны
            bool isContainer = method is "Grouping" or "ToolBar" or "UIAObject";

            var arg = ChooseBestArgument(node, isChromium);

            if (arg == null)
            {
                if (isContainer)
                    continue; // пропускаем мусорный контейнер

                // fallback: индекс (0) среди siblings данного типа
                parts.Add($".{method}(0)");
                lastMethod = method;
                lastArg = "0";
            }
            else
            {
                // Если Document повторяет caption формы — можем выкинуть
                if (method == "Document" &&
                    !string.IsNullOrWhiteSpace(windowCaption) &&
                    string.Equals(arg, windowCaption, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Убираем подряд идущие одинаковые контейнеры с тем же аргументом
                if (lastMethod == method && string.Equals(lastArg, arg, StringComparison.Ordinal))
                    continue;

                parts.Add($".{method}(\"{Escape(arg)}\")");
                lastMethod = method;
                lastArg = arg;
            }
        }
    }

    static void BuildWin32MsaaFallback(SpyElement element, List<string> parts, bool preferVclFallback)
    {
        string? leafName =
            element.MsaaName ??
            element.UiaName ??
            element.Win32Text ??
            element.Win32Class;

        if (string.IsNullOrWhiteSpace(leafName))
            return;

        var name = leafName!.Trim();

        if (preferVclFallback)
        {
            parts.Add($".VCL(\"{Escape(name)}\")");
        }
        else
        {
            parts.Add($".Client(\"{Escape(name)}\")");
        }
    }

    static bool PreferVclFallback(SpyElement element)
    {
        if ((element.VclChain?.Count ?? 0) > 0)
            return true;

        static bool LooksLikeVclToken(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return false;

            var t = s.Trim();
            if (t.StartsWith("T", StringComparison.OrdinalIgnoreCase))
                return true;

            return t.Contains("vcl", StringComparison.OrdinalIgnoreCase);
        }

        return
            LooksLikeVclToken(element.Win32Class) ||
            LooksLikeVclToken(element.UiaClassName) ||
            LooksLikeVclToken(element.UiaFrameworkId);
    }

    static int GetWindowClassIndex(IntPtr hwnd, string className)
    {
        if (hwnd == IntPtr.Zero) return 1;
        if (string.IsNullOrWhiteSpace(className)) return 1;

        var parent = GetAncestor(hwnd, GA_PARENT);
        if (parent == IntPtr.Zero) return 1;

        int seen = 0;
        int? result = null;

        EnumChildWindows(parent, (child, _) =>
        {
            var cls = GetClassNameStr(child);
            if (string.Equals(cls, className, StringComparison.OrdinalIgnoreCase))
            {
                seen++;
                if (child == hwnd)
                {
                    result = seen;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        return result ?? 1;
    }

    static string GetClassNameStr(IntPtr hwnd)
    {
        var buf = new char[256];
        var len = GetClassName(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    const uint GA_PARENT = 1;

    static string? ChooseBestArgument(SpyUiaNode node, bool isChromium)
    {
        // 1) AutomationId
        if (!string.IsNullOrWhiteSpace(node.AutomationId))
        {
            var id = node.AutomationId!.Trim();
            if (!isChromium) // для хрома AutomationId часто нестабилен
                return id;
        }

        // 2) Name (если не "мусор")
        if (!string.IsNullOrWhiteSpace(node.Name))
        {
            var name = node.Name!.Trim();

            // фильтруем "мусорные" имена
            if (name.Length > 80 || IsMostlyWhitespace(name) || LooksLikeCssLike(name))
            {
                // считаем Name непригодным, идём дальше к индексу
            }
            else
            {
                return name;
            }
        }

        // 3) иначе аргумента нет -> индекс
        return null;
    }

    static string NormalizeControlType(string? programmatic)
    {
        if (string.IsNullOrWhiteSpace(programmatic))
            return string.Empty;

        var s = programmatic!;
        // обычно вида "ControlType.Button"
        var lastDot = s.LastIndexOf('.');
        return lastDot >= 0 && lastDot < s.Length - 1
            ? s[(lastDot + 1)..]
            : s;
    }

    static string MapControlTypeToMethod(string controlType)
    {
        return controlType switch
        {
            "Button" => "Button",
            "Edit" => "Edit",
            "ToolBar" => "ToolBar",
            "Document" => "Document",
            "Window" => "Form",
            "Group" or "Grouping" => "Grouping",
            "Pane" or "Custom" => "Grouping",
            _ => "UIAObject"
        };
    }

    static bool IsChromium(SpyElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.UiaFrameworkId) &&
            element.UiaFrameworkId!.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (!string.IsNullOrWhiteSpace(element.ProcessName) &&
            (element.ProcessName.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
             element.ProcessName.Contains("cursor", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!string.IsNullOrWhiteSpace(element.Win32Class) &&
            element.Win32Class.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    static bool IsMostlyWhitespace(string s)
    {
        int nonWs = s.Count(c => !char.IsWhiteSpace(c));
        return nonWs == 0;
    }

    static bool LooksLikeCssLike(string s)
    {
        // Простая эвристика: длинная строка без пробелов, много '-' или '_'
        if (s.Length <= 20) return false;
        if (s.Contains(' ')) return false;
        int dashes = s.Count(c => c == '-' || c == '_');
        return dashes >= 2;
    }

    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

// ------- Structured Locator (V2) -------
    public record SlStep(
        string Backend,
        string? AutomationId,
        string? Name,
        string? ClassName,
        string? ControlType,
        string? Role,
        string? Text,
        int? Index
    );

    public record LocatorWindow(string? Title, string? ClassName, string? TitleContains = null);

    public record LocatorUia(string? ControlType, string? AutomationId, string? ClassName, string? Name);
    public record LocatorWin32(string? ClassName, string? Text);
    public record LocatorLeaf(string Kind, string? ClassName, string? Text, int? Index);

    public record StructuredLocator(
        string[] BackendPriority,
        string Process,
        LocatorWindow? Window,
        IReadOnlyList<SlStep>? Chain,
        IReadOnlyList<string>? VclChain = null,
        LocatorUia? UIA = null,
        LocatorWin32? Win32 = null,
        LocatorLeaf? Leaf = null
    );

    public static class StructuredLocatorBuilder
    {
        public static StructuredLocator Build(SpyElement element)
        {
            var process = element.ProcessName;
            var rawTitle = string.IsNullOrWhiteSpace(element.Win32Text) ? (element.UiaName ?? "") : element.Win32Text!;
            var (title, titleContains) = StabilizeWindowTitle(rawTitle);
            var cls = element.Win32Class ?? "";

            // VCL-first logic
            if (element.VclChain != null && element.VclChain.Count > 0)
            {
                var vclChain = new List<string>();
                foreach (var n in element.VclChain)
                {
                     // Use Name or Class
                     vclChain.Add(!string.IsNullOrEmpty(n.ComponentName) ? n.ComponentName : n.ClassName);
                }
                
                LocatorLeaf? leaf = null;
                var leafWin32Class = element.Win32Class;
                if (!string.IsNullOrWhiteSpace(leafWin32Class))
                {
                    var last = element.VclChain[^1];
                    if (string.IsNullOrWhiteSpace(last.ComponentName) ||
                        !string.Equals(last.ClassName, leafWin32Class, StringComparison.OrdinalIgnoreCase))
                    {
                        leaf = new LocatorLeaf(
                            Kind: "Window",
                            ClassName: leafWin32Class,
                            Text: "",
                            Index: GetWindowClassIndex(element.Hwnd, leafWin32Class)
                        );
                    }
                }

                return new StructuredLocator(
                    BackendPriority: new[] { "vcl", "uia", "win32", "msaa" },
                    Process: process,
                    Window: null,
                    Chain: null,
                    VclChain: vclChain,
                    UIA: new LocatorUia(
                        element.UiaControlType,
                        element.UiaAutomationId,
                        element.UiaClassName,
                        element.UiaName
                    ),
                    Win32: new LocatorWin32(
                        element.Win32Class,
                        element.Win32Text
                    ),
                    Leaf: leaf
                );
            }

            var chain = BuildMinimalChain(element);
            chain = MinimizeChain(element, chain);

            return new StructuredLocator(
                BackendPriority: new[] { "uia", "win32", "msaa" },
                Process: process,
                Window: new LocatorWindow(title, cls, titleContains),
                Chain: chain
            );
        }

        public static string BuildHumanSelector(SpyElement element, StructuredLocator locator)
        {
            var parts = new List<string>();
            parts.Add($"Процесс({Safe(locator.Process)})");

            if (locator.VclChain is { Count: > 0 })
            {
                foreach (var n in locator.VclChain)
                {
                    parts.Add($"VCL({Safe(n)})");
                }
                if (locator.Leaf != null)
                {
                    parts.Add($"Window({Safe(locator.Leaf.ClassName ?? "")})");
                }
                return string.Join(" -> ", parts);
            }
            
            if (locator.Window != null)
            {
                var winName = !string.IsNullOrWhiteSpace(locator.Window.Title)
                    ? locator.Window.Title
                    : locator.Window.ClassName;
                parts.Add($"Окно({Safe(winName)})");
            }

            if (locator.Chain != null)
            {
                foreach (var step in locator.Chain)
                {
                    var kind = StepKindRu(step);
                    var val = StepValue(step);
                    parts.Add($"{kind}({Safe(val)})");
                }
            }

            return string.Join(" -> ", parts);
        }

        static int GetWindowClassIndex(IntPtr hwnd, string className)
        {
            if (hwnd == IntPtr.Zero) return 1;
            if (string.IsNullOrWhiteSpace(className)) return 1;

            var parent = GetAncestor(hwnd, GA_PARENT);
            if (parent == IntPtr.Zero) return 1;

            int seen = 0;
            int? result = null;

            EnumChildWindows(parent, (child, _) =>
            {
                var cls = GetClassNameStr(child);
                if (string.Equals(cls, className, StringComparison.OrdinalIgnoreCase))
                {
                    seen++;
                    if (child == hwnd)
                    {
                        result = seen;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            return result ?? 1;
        }

        static string GetClassNameStr(IntPtr hwnd)
        {
            var buf = new char[256];
            var len = GetClassName(hwnd, buf, buf.Length);
            return len > 0 ? new string(buf, 0, len) : "";
        }

        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        const uint GA_PARENT = 1;

        static IReadOnlyList<SlStep> BuildMinimalChain(SpyElement e)
        {
            // Если есть UIA у leaf:
            if (!string.IsNullOrWhiteSpace(e.UiaAutomationId) ||
                !string.IsNullOrWhiteSpace(e.UiaName) ||
                !string.IsNullOrWhiteSpace(e.UiaClassName) ||
                !string.IsNullOrWhiteSpace(e.UiaControlType))
            {
                var leaf = MakeUiaStep(e, e.UiaAutomationId, e.UiaName, e.UiaClassName, e.UiaControlType, index: null);

                // 1) Shortest: только leaf с AutomationId
                if (!string.IsNullOrWhiteSpace(leaf.AutomationId))
                    return new[] { leaf };

                // 2) leaf с валидным Name
                if (IsGoodName(leaf.Name))
                    return new[] { leaf };

                // 3) leaf с class_name + control_type — возможно недостаточно: добавим ближайшего стабильного родителя
                var parentStable = FindStableParent(e);
                if (parentStable != null)
                    return new[] { parentStable, leaf };

                // 4) fallback: индекс на leaf
                leaf = leaf with { Index = 0 };
                return new[] { leaf };
            }

            // UIA слабый — пробуем Win32
            if (!string.IsNullOrWhiteSpace(e.Win32Class) || !string.IsNullOrWhiteSpace(e.Win32Text))
            {
                var win32Name = ChooseStableWin32Name(e);
                var win32Step = new SlStep(
                    Backend: "win32",
                    AutomationId: null,
                    Name: win32Name,
                    ClassName: string.IsNullOrWhiteSpace(e.Win32Class) ? null : e.Win32Class,
                    ControlType: null,
                    Role: null,
                    Text: null,
                    Index: null
                );
                return new[] { win32Step };
            }

            // Последний шанс — MSAA
            if (!string.IsNullOrWhiteSpace(e.MsaaRole) || !string.IsNullOrWhiteSpace(e.MsaaName))
            {
                var msaa = new SlStep(
                    Backend: "msaa",
                    AutomationId: null,
                    Name: string.IsNullOrWhiteSpace(e.MsaaName) ? null : e.MsaaName,
                    ClassName: null,
                    ControlType: null,
                    Role: string.IsNullOrWhiteSpace(e.MsaaRole) ? null : e.MsaaRole,
                    Text: null,
                    Index: null
                );
                return new[] { msaa };
            }

            // Совсем ничего — пустая цепочка
            return Array.Empty<SlStep>();
        }

        static SlStep? FindStableParent(SpyElement e)
        {
            if (e.UiaParentChain is not { Count: > 0 })
                return null;

            foreach (var p in e.UiaParentChain)
            {
                var step = MakeUiaStep(e, p.AutomationId, p.Name, p.ClassName, p.ControlType, index: null);
                if (!string.IsNullOrWhiteSpace(step.AutomationId))
                    return step;
                if (IsGoodName(step.Name))
                    return step;
                if (!string.IsNullOrWhiteSpace(step.ClassName) && !string.IsNullOrWhiteSpace(step.ControlType))
                    return step;
            }
            return null;
        }

        static SlStep MakeUiaStep(SpyElement e, string? automationId, string? name, string? className, string? controlType, int? index)
        {
            var ct = NormalizeControlType(controlType);
            if (!string.IsNullOrWhiteSpace(automationId) && !IsChromium(e))
            {
                return new SlStep("uia", automationId.Trim(), null, null, string.IsNullOrWhiteSpace(ct) ? null : ct, null, null, null);
            }

            if (IsGoodName(name) || IsGoodName(e.VclNameCandidate))
            {
                var val = IsGoodName(name) ? name!.Trim() : e.VclNameCandidate!.Trim();
                return new SlStep("uia", null, val, null, string.IsNullOrWhiteSpace(ct) ? null : ct, null, null, null);
            }

            var cls = GoodClassName(className);
            if (!string.IsNullOrWhiteSpace(cls) || !string.IsNullOrWhiteSpace(controlType))
            {
                return new SlStep("uia", null, null, cls, string.IsNullOrWhiteSpace(ct) ? null : ct, null, null, null);
            }

            return new SlStep("uia", null, null, null, null, null, null, index);
        }

        static bool IsGoodName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();
            if (n.Length > 80) return false;
            if (LooksLikeCssLike(n)) return false;
            if (IsMostlyWhitespace(n)) return false;
            return true;
        }

        static string StepKindRu(SlStep step)
        {
            if (step.Backend == "uia")
            {
                var ct = step.ControlType ?? "";
                return ct switch
                {
                    "Button" => "Кнопка",
                    "Edit" => "Поле",
                    "Document" => "Документ",
                    "ToolBar" => "Панель",
                    "Group" or "Grouping" => "Группа",
                    "Pane" or "Custom" => "Панель",
                    "CheckBox" => "Флажок",
                    "RadioButton" => "Переключатель",
                    "ComboBox" => "Комбобокс",
                    "List" or "ListItem" => "Список",
                    "Tab" or "TabItem" => "Вкладка",
                    "Menu" or "MenuItem" => "Меню",
                    "Tree" or "TreeItem" => "Дерево",
                    _ => "Элемент"
                };
            }
            if (step.Backend == "win32")
                return "Элемент";
            if (step.Backend == "msaa")
            {
                var role = (step.Role ?? "").ToLowerInvariant();
                if (role.Contains("button")) return "Кнопка";
                if (role.Contains("menu")) return "Меню";
                if (role.Contains("check")) return "Флажок";
                return "MSAA";
            }
            return "Элемент";
        }

        static string StepValue(SlStep step)
        {
            // Для human_selector: сначала Name, затем AutomationId, затем короткий ClassName/ControlType, затем Role/Text, затем Index.
            if (!string.IsNullOrWhiteSpace(step.Name)) return step.Name!;
            if (!string.IsNullOrWhiteSpace(step.AutomationId)) return step.AutomationId!;
            if (!string.IsNullOrWhiteSpace(step.ClassName) || !string.IsNullOrWhiteSpace(step.ControlType))
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(step.ClassName)) parts.Add(step.ClassName!);
                if (!string.IsNullOrWhiteSpace(step.ControlType)) parts.Add(step.ControlType!);
                return string.Join("/", parts);
            }
            if (!string.IsNullOrWhiteSpace(step.Text)) return step.Text!;
            if (!string.IsNullOrWhiteSpace(step.Role)) return step.Role!;
            if (step.Index != null) return $"index={step.Index}";
            return "?";
        }

        static string Safe(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s!;

        static string NormalizeControlType(string? programmatic)
        {
            if (string.IsNullOrWhiteSpace(programmatic))
                return string.Empty;
            var s = programmatic!;
            var lastDot = s.LastIndexOf('.');
            return lastDot >= 0 && lastDot < s.Length - 1 ? s[(lastDot + 1)..] : s;
        }

        static (string title, string? titleContains) StabilizeWindowTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return ("", null);
            var t = title!;
            bool looksPath = t.Contains(@":\") || t.Contains('/');
            if (!looksPath) return (t, null);
            var idx = t.IndexOf(" - ");
            if (idx >= 0)
            {
                var suffix = t[idx..]; // включая " - "
                return ("", suffix);
            }
            return ("", null);
        }

        static string? GoodClassName(string? className)
        {
            if (string.IsNullOrWhiteSpace(className)) return null;
            var s = className.Trim();
            if (s.Length > 50) return null;
            if (s.Contains(' ')) return null; // списки CSS или составные
            return s;
        }

        static IReadOnlyList<SlStep> MinimizeChain(SpyElement e, IReadOnlyList<SlStep> chain)
        {
            if (chain.Count < 2) return chain;
            var first = chain[0];
            var second = chain[1];
            bool firstIsWindow = string.Equals(first.ControlType, "Window", StringComparison.OrdinalIgnoreCase);
            bool dupWinClass = !string.IsNullOrWhiteSpace(first.ClassName) &&
                               !string.IsNullOrWhiteSpace(e.Win32Class) &&
                               string.Equals(first.ClassName, e.Win32Class, StringComparison.OrdinalIgnoreCase);
            if (firstIsWindow || dupWinClass)
            {
                // Отбрасываем верхний шаг
                return chain.Skip(1).ToArray();
            }
            return chain;
        }

        static bool IsDynamicText(string? caption)
        {
            if (string.IsNullOrWhiteSpace(caption)) return false;
            var s = caption.Trim();
            // простая эвристика: слишком длинный/похож на дату/идентификатор
            if (s.Length > 80) return true;
            if (s.Any(char.IsDigit) && s.Count(char.IsDigit) > s.Length / 2) return true;
            return false;
        }

        static bool IsMostlyWhitespace(string s)
        {
            int nonWs = s.Count(c => !char.IsWhiteSpace(c));
            return nonWs == 0;
        }

        static bool LooksLikeCssLike(string s)
        {
            if (s.Length <= 20) return false;
            if (s.Contains(' ')) return false;
            int dashes = s.Count(c => c == '-' || c == '_');
            return dashes >= 2;
        }

        static bool IsChromium(SpyElement element)
        {
            if (!string.IsNullOrWhiteSpace(element.UiaFrameworkId) &&
                element.UiaFrameworkId!.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (!string.IsNullOrWhiteSpace(element.ProcessName) &&
                (element.ProcessName.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
                 element.ProcessName.Contains("cursor", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!string.IsNullOrWhiteSpace(element.Win32Class) &&
                element.Win32Class.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        static string? ChooseStableWin32Name(SpyElement e)
        {
            if (!string.IsNullOrWhiteSpace(e.VclNameCandidate)) return e.VclNameCandidate!.Trim();
            if (!string.IsNullOrWhiteSpace(e.MsaaName)) return e.MsaaName!.Trim();
            if (!string.IsNullOrWhiteSpace(e.UiaName)) return e.UiaName!.Trim();
            if (!IsDynamicText(e.Win32Text) && !string.IsNullOrWhiteSpace(e.Win32Text)) return e.Win32Text!.Trim();
            return null;
        }
    }
