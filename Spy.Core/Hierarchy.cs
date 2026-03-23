using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Accessibility;

namespace Spy.Core;

public enum HierarchySource
{
    Uia,
    Win32,
    Msaa,
    Vcl
}

public record HierarchyNodeModel(
    string Label,
    HierarchySource Source,
    IntPtr Hwnd
);

public static class HierarchyBuilder
{
    public static IReadOnlyList<HierarchyNodeModel> Build(SpyElement element)
    {
        var nodes = new List<HierarchyNodeModel>();

        // 1) Процесс
        nodes.Add(new HierarchyNodeModel(
            $"{element.ProcessName} (PID {element.ProcessId})",
            HierarchySource.Win32,
            IntPtr.Zero));

        // 2) VCL Hierarchy (Priority 1)
        if (element.VclChain is { Count: > 0 })
        {
            foreach (var node in element.VclChain)
            {
                var label = $"[VCL] {node.ClassName} \"{node.ComponentName ?? "<no name>"}\"";
                nodes.Add(new HierarchyNodeModel(label, HierarchySource.Vcl, element.Hwnd));
            }
            return nodes;
        }

        // 3) Win32 top-level window
        nodes.Add(new HierarchyNodeModel(
            $"{element.Win32Class} \"{(string.IsNullOrWhiteSpace(element.Win32Text) ? "<no caption>" : element.Win32Text)}\"",
            HierarchySource.Win32,
            element.Hwnd));

        // 4) UIA chain, если есть
        if (element.UiaParentChain is { Count: > 0 })
        {
            int level = 0;
            foreach (var p in element.UiaParentChain)
            {
                var label = FormatUiaLabel(level, p.Name, p.ClassName, p.AutomationId);
                nodes.Add(new HierarchyNodeModel(label, HierarchySource.Uia, element.Hwnd));
                level++;
            }
        }
        else
        {
            // Fallback: Win32 родительская цепочка
            foreach (var p in EnumerateWin32Parents(element.Hwnd))
            {
                nodes.Add(new HierarchyNodeModel(p, HierarchySource.Win32, element.Hwnd));
            }
        }

        // 4) MSAA parent chain (верхние уровни, если доступны)
        foreach (var p in EnumerateMsaaParents(element.Hwnd))
        {
            nodes.Add(new HierarchyNodeModel(p, HierarchySource.Msaa, element.Hwnd));
        }

        // 5) Leaf element (UIA/MSAA)
        var leafName =
            element.MsaaName ??
            element.UiaName ??
            element.Win32Text ??
            element.Win32Class;

        var leafLabel = $"[Leaf] \"{leafName ?? "<no name>"}\"";
        nodes.Add(new HierarchyNodeModel(leafLabel, HierarchySource.Msaa, element.Hwnd));

        return nodes;
    }

    static string FormatUiaLabel(int level, string? name, string? cls, string? automationId)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "<no name>" : name;
        var safeClass = string.IsNullOrWhiteSpace(cls) ? "<no class>" : cls;
        var aidPart = string.IsNullOrWhiteSpace(automationId) ? "" : $"  [{automationId}]";
        return $"[UIA] {level}. {safeClass} \"{safeName}\"{aidPart}";
    }

    static IEnumerable<string> EnumerateWin32Parents(IntPtr hwnd)
    {
        var current = hwnd;
        while (true)
        {
            var parent = GetAncestor(current, GA_PARENT);
            if (parent == IntPtr.Zero || parent == current)
                yield break;

            string cls = GetClassNameStr(parent);
            string txt = GetWindowTextStr(parent);
            yield return $"[Win32] {cls} \"{(string.IsNullOrWhiteSpace(txt) ? "<no caption>" : txt)}\"";

            current = parent;
        }
    }

    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    const uint GA_PARENT = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    static string GetClassNameStr(IntPtr hwnd)
    {
        var buf = new char[256];
        var len = GetClassName(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }

    static string GetWindowTextStr(IntPtr hwnd)
    {
        var buf = new char[512];
        var len = GetWindowText(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }

    // --- MSAA hierarchy ---

    static IEnumerable<string> EnumerateMsaaParents(IntPtr hwnd)
    {
        const uint OBJID_CLIENT = 0xFFFFFFFC;
        Guid iid = new("618736E0-3C3D-11CF-810C-00AA00389B71"); // IID_IAccessible

        var result = new List<string>();

        int hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out var acc);
        if (hr < 0 || acc == null)
            return result;

        var current = acc;
        int depth = 0;

        while (current != null && depth < 10)
        {
            depth++;
            string? name = null;
            string? role = null;

            try { name = current.get_accName(0); } catch { }
            try
            {
                var roleObj = current.get_accRole(0);
                role = roleObj?.ToString();
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(role))
                result.Add($"[MSAA] {role ?? ""} \"{(string.IsNullOrWhiteSpace(name) ? "<no name>" : name)}\"");

            object parentObj;
            try { parentObj = current.accParent; }
            catch { break; }

            if (parentObj is IAccessible parentAcc)
                current = parentAcc;
            else
                break;
        }

        return result;
    }

    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IAccessible acc);
}

