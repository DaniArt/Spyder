using System;
using System.Collections.Generic;
using System.Linq;

namespace Spy.Core;

public enum LocatorStepType
{
    Process,
    VCLObject,
    Window,
    Form,
    Client
}

public record LocatorStep(LocatorStepType Type, string Name, int? Index);

public record Locator(string Process, IReadOnlyList<LocatorStep> Chain);

public static class LocatorGenerator
{
    // Небольшой список типичных VCL-классов; дальше можно расширять
    static readonly string[] VclPrefixes = { "T", "TCI", "TEx" };

    public static Locator Build(SpyElement element)
    {
        var steps = new List<LocatorStep>();

        // root: Sys.Process("ProcessName")
        var processName = element.ProcessName;

        bool isColvir = element.ProcessName.Contains("colvir", StringComparison.OrdinalIgnoreCase) ||
                        (element.ProcessPath?.IndexOf("colvir", StringComparison.OrdinalIgnoreCase) >= 0);

        // Определяем, VCL это или нет, по классу
        bool IsVclClass(string? cls) =>
            !string.IsNullOrWhiteSpace(cls) &&
            VclPrefixes.Any(p => cls.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        // 1) top-level window / form
        var winCaption = string.IsNullOrWhiteSpace(element.Win32Text) ? element.UiaName : element.Win32Text;
        if (!string.IsNullOrWhiteSpace(winCaption))
        {
            steps.Add(new LocatorStep(LocatorStepType.Window, winCaption!, null));
        }

        // 2) UIA parent chain -> VCLObject / Client
        var chain = element.UiaParentChain ?? Array.Empty<SpyUiaNode>();
        foreach (var node in chain)
        {
            var cls = node.ClassName ?? node.FrameworkId;
            var isVclNode = IsVclClass(cls) || isColvir;

            // Property priority:
            // 1) Name
            // 2) ClassName
            // 3) ControlType
            // 4) AutomationId (только не для VCL)
            string name = node.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                name = cls ?? "";
            if (string.IsNullOrWhiteSpace(name))
                name = node.ControlType ?? "";
            if (!isVclNode && string.IsNullOrWhiteSpace(name))
                name = node.AutomationId ?? "";
            if (string.IsNullOrWhiteSpace(name))
                name = "<no name>";

            var stepType = isVclNode
                ? LocatorStepType.VCLObject
                : LocatorStepType.Client;

            steps.Add(new LocatorStep(stepType, name, null));
        }

        // 3) сам элемент
        var isVclLeaf = IsVclClass(element.UiaClassName ?? element.Win32Class) || isColvir;

        // Property priority для leaf:
        // 1) Name / Caption (MsaaName / UiaName / Win32Text)
        // 2) ClassName
        // 3) ControlType
        // 4) AutomationId (кроме VCL)
        string? leafName = element.MsaaName;
        if (string.IsNullOrWhiteSpace(leafName))
            leafName = element.UiaName;
        if (string.IsNullOrWhiteSpace(leafName))
            leafName = element.Win32Text;
        if (string.IsNullOrWhiteSpace(leafName))
            leafName = element.UiaClassName ?? element.Win32Class;
        if (string.IsNullOrWhiteSpace(leafName))
            leafName = element.UiaControlType;
        if (!isVclLeaf && string.IsNullOrWhiteSpace(leafName))
            leafName = element.UiaAutomationId;

        if (!string.IsNullOrWhiteSpace(leafName))
        {
            var leafClass = element.UiaClassName ?? element.Win32Class;
            var leafType = IsVclClass(leafClass) || isColvir
                ? LocatorStepType.VCLObject
                : LocatorStepType.Client;

            steps.Add(new LocatorStep(leafType, leafName!, null));
        }

        return new Locator(processName, steps);
    }

    public static string ToCodeString(Locator locator)
    {
        var parts = new List<string>
        {
            $"Sys.Process(\"{Escape(locator.Process)}\")"
        };

        foreach (var step in locator.Chain)
        {
            var typeName = step.Type switch
            {
                LocatorStepType.Process => "Process",
                LocatorStepType.VCLObject => "VCLObject",
                LocatorStepType.Window => "Window",
                LocatorStepType.Form => "Form",
                LocatorStepType.Client => "Client",
                _ => "Client"
            };

            var namePart = $"\"{Escape(step.Name)}\"";

            parts.Add($".{typeName}({namePart})");
        }

        return string.Concat(parts);
    }

    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

