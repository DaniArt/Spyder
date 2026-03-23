using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Spy.Core.Vcl;

namespace Spy.UI;

sealed class VclBridgeIntrospector : IVclIntrospector
{
    readonly VclBridgeClient _client;
    public int ProcessId { get; }

    public VclBridgeIntrospector(VclBridgeClient client, int processId)
    {
        _client = client;
        ProcessId = processId;
    }

    public bool TryFindForm(string formId, out VclControlInfo form)
    {
        form = default!;
        if (string.IsNullOrWhiteSpace(formId)) return false;

        var token = formId.Trim();

        foreach (var hwnd in EnumerateTopLevelWindows(ProcessId))
        {
            var r = _client.ResolveByHwnd(hwnd);
            if (r?.ok != true || r.is_vcl != true) continue;
            if (string.IsNullOrWhiteSpace(r.vcl_self) || string.Equals(r.vcl_self, "0x00000000", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(r.vcl_class)) continue;

            var vclName = r.vcl_name ?? "";
            if (string.Equals(vclName, token, StringComparison.OrdinalIgnoreCase))
            {
                form = new VclControlInfo(ParseSelf(r.vcl_self!), r.vcl_class!, string.IsNullOrWhiteSpace(r.vcl_name) ? null : r.vcl_name);
                return true;
            }
        }

        foreach (var hwnd in EnumerateTopLevelWindows(ProcessId))
        {
            var r = _client.ResolveByHwnd(hwnd);
            if (r?.ok != true || r.is_vcl != true) continue;
            if (string.IsNullOrWhiteSpace(r.vcl_self) || string.Equals(r.vcl_self, "0x00000000", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(r.vcl_class)) continue;

            if (string.Equals(r.vcl_class, token, StringComparison.OrdinalIgnoreCase))
            {
                form = new VclControlInfo(ParseSelf(r.vcl_self!), r.vcl_class!, string.IsNullOrWhiteSpace(r.vcl_name) ? null : r.vcl_name);
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<VclControlInfo> GetChildren(nuint parentSelf)
    {
        var hex = $"0x{parentSelf:x}";
        var children = _client.GetChildrenForEngine(hex);
        if (children.Count == 0) return Array.Empty<VclControlInfo>();

        var list = new List<VclControlInfo>(children.Count);
        foreach (var ch in children)
        {
            if (string.IsNullOrWhiteSpace(ch.self) || string.IsNullOrWhiteSpace(ch.@class)) continue;
            var self = ParseSelf(ch.self);
            list.Add(new VclControlInfo(self, ch.@class!, string.IsNullOrWhiteSpace(ch.name) ? null : ch.name));
        }
        return list;
    }

    static nuint ParseSelf(string selfHex)
    {
        var s = selfHex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (nuint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v;
        return 0;
    }

    static IEnumerable<IntPtr> EnumerateTopLevelWindows(int pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (!IsWindow(h)) return true;
            if (GetParent(h) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(h, out var winPid);
            if (winPid == pid)
                list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr hwnd);
}
