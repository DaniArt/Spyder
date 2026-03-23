using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using Accessibility;

namespace Spy.Core;

public record SpyElement(
    IntPtr Hwnd,
    int ProcessId,
    string ProcessName,
    string? ProcessPath,
    string Win32Class,
    string Win32Text,
    System.Windows.Rect Rect,
    int? Win32HitTestCode,
    string? Win32HitTestArea,
    string? MsaaName,
    string? MsaaRole,
    int? MsaaChildId,
    string? MsaaState,
    string? MsaaValue,
    string? MsaaDescription,
    string? UiaName,
    string? UiaAutomationId,
    string? UiaControlType,
    string? UiaClassName,
    string? UiaFrameworkId,
    string? UiaHelpText,
    string? UiaLegacyName,
    string? UiaLegacyDescription,
    System.Windows.Rect? UiaBoundingRect,
    IReadOnlyList<SpyUiaNode> UiaParentChain,
    string? VclNameCandidate,
    IReadOnlyList<SpyVclNode>? VclChain = null
);

public record SpyUiaNode(
    string? Name,
    string? AutomationId,
    string? ControlType,
    string? ClassName,
    string? FrameworkId
);

public record SpyVclNode(
    string ClassName,
    string? ComponentName
);

// Win32 / UIA / MSAA internal snapshots to keep layers separated inside SpyCapture
record Win32Snapshot(
    IntPtr Hwnd,
    int ProcessId,
    string ProcessName,
    string? ProcessPath,
    Rectangle Rect,
    string Win32Class,
    string Win32Text,
    int? HitTestCode,
    string? HitTestArea
);

record UiaSnapshot(
    string? Name,
    string? AutomationId,
    string? ControlType,
    string? ClassName,
    string? FrameworkId,
    string? HelpText,
    string? LegacyName,
    string? LegacyDescription,
    System.Windows.Rect? BoundingRect,
    IReadOnlyList<SpyUiaNode> ParentChain
);

record MsaaSnapshot(
    string? Name,
    string? Role,
    int? ChildId,
    string? State,
    string? Value,
    string? Description
    // расширяем по мере реализации MSAA
);

public static class SpyCapture
{
    /// <summary>
    /// Лёгкий захват только Win32-части для hover-режима (без UIA/MSAA).
    /// </summary>
    public static SpyElement CaptureWin32UnderCursor()
    {
        GetCursorPos(out var pt);

        var win32 = CaptureWin32FromPoint(pt);

        // Лёгкая UIA-часть только для bounding rect (для точной подсветки),
        // без построения цепочек и глубоких свойств.
        System.Windows.Rect? br = null;
        try
        {
            AutomationElement? ae = null;
            try { ae = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y)); } catch { }
            if (ae == null)
            {
                try { ae = AutomationElement.FromHandle(win32.Hwnd); } catch { }
            }

            if (ae != null)
            {
                try { br = ae.Current.BoundingRectangle; } catch { }
            }
        }
        catch
        {
            br = null;
        }

        return new SpyElement(
            win32.Hwnd,
            win32.ProcessId,
            win32.ProcessName,
            win32.ProcessPath,
            win32.Win32Class,
            win32.Win32Text,
            ToRect(win32.Rect),
            win32.HitTestCode,
            win32.HitTestArea,
            MsaaName: null,
            MsaaRole: null,
            MsaaChildId: null,
            MsaaState: null,
            MsaaValue: null,
            MsaaDescription: null,
            UiaName: null,
            UiaAutomationId: null,
            UiaControlType: null,
            UiaClassName: null,
            UiaFrameworkId: null,
            UiaHelpText: null,
            UiaLegacyName: null,
            UiaLegacyDescription: null,
            UiaBoundingRect: br,
            UiaParentChain: Array.Empty<SpyUiaNode>(),
            VclNameCandidate: null,
            VclChain: null
        );
    }

    public interface IVclBackend
    {
        bool IsConnected { get; }
        IReadOnlyList<SpyVclNode>? ResolveVclChain(IntPtr hwnd, int screenX, int screenY, string? vclNameCandidate);
        Dictionary<string, string>? GetProperties(IntPtr hwnd); // Simplified for capture
    }

    public static SpyElement CaptureUnderCursor(IVclBackend? vclBackend = null)
    {
        GetCursorPos(out var pt);

        // Win32 layer (обязательная часть)
        var win32 = CaptureWin32FromPoint(pt);

        // UIA layer (including parent traversal) — выполняем в фоне с таймаутом
        UiaSnapshot? uia = null;
        try
        {
            var uiaTask = Task.Run(() => CaptureUiaForPointAndHwnd(pt, win32.Hwnd));
            if (uiaTask.Wait(millisecondsTimeout: 800))
                uia = uiaTask.Result;
        }
        catch
        {
            // UIA может зависнуть/упасть — игнорируем, остаёмся на Win32/MSAA
        }

        // MSAA / IAccessible layer
        MsaaSnapshot? msaa = null;
        try
        {
            var msaaTask = Task.Run(() => CaptureMsaaFromPoint(pt));
            if (msaaTask.Wait(millisecondsTimeout: 800))
                msaa = msaaTask.Result;
        }
        catch
        {
            // MSAA тоже не должен уронить захват
        }

        var vclNameCandidate = FindVclNameCandidate(win32, uia, msaa);

        IReadOnlyList<SpyVclNode>? vclChain = null;
        if (vclBackend?.IsConnected == true)
        {
            try
            {
                Trace.WriteLine($"[SpyCapture] Requesting VCL chain for HWND 0x{win32.Hwnd.ToInt64():X} (candidate={vclNameCandidate ?? "null"})");
                vclChain = vclBackend.ResolveVclChain(win32.Hwnd, pt.X, pt.Y, vclNameCandidate);
                Trace.WriteLine($"[SpyCapture] VCL chain result: {(vclChain == null ? "null" : $"{vclChain.Count} nodes")}");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SpyCapture] VCL capture failed: {ex.Message}");
            }
        }

        return new SpyElement(
            win32.Hwnd,
            win32.ProcessId,
            win32.ProcessName,
            win32.ProcessPath,
            win32.Win32Class,
            win32.Win32Text,
            ToRect(win32.Rect),
            win32.HitTestCode,
            win32.HitTestArea,
            msaa?.Name,
            msaa?.Role,
            msaa?.ChildId,
            msaa?.State,
            msaa?.Value,
            msaa?.Description,
            uia?.Name,
            uia?.AutomationId,
            uia?.ControlType,
            uia?.ClassName,
            uia?.FrameworkId,
            uia?.HelpText,
            uia?.LegacyName,
            uia?.LegacyDescription,
            uia?.BoundingRect,
            uia?.ParentChain ?? Array.Empty<SpyUiaNode>(),
            vclNameCandidate,
            VclChain: vclChain
        );
    }

    private static System.Windows.Rect ToRect(Rectangle r)
    {
        return new System.Windows.Rect(r.X, r.Y, r.Width, r.Height);
    }

    // --- Win32 layer ---


    static Win32Snapshot CaptureWin32FromPoint(POINT pt)
    {
        var hwnd = GetDeepestHwndFromPoint(pt);
        if (hwnd == IntPtr.Zero) throw new Exception("HWND not found from cursor point.");

        GetWindowThreadProcessId(hwnd, out var pid);
        var proc = Process.GetProcessById((int)pid);

        string? path = null;
        try { path = proc.MainModule?.FileName; } catch { }

        var cls = GetClassNameStr(hwnd);
        var txt = GetWindowTextStr(hwnd);
        var rect = GetRect(hwnd);

        int? hitCode = null;
        string? hitArea = null;
        try
        {
            (hitCode, hitArea) = HitTestNonClient(hwnd, pt);
        }
        catch
        {
            // ignore hit-test errors
        }

        return new Win32Snapshot(
            hwnd,
            (int)pid,
            proc.ProcessName,
            path,
            rect,
            cls,
            txt,
            hitCode,
            hitArea
        );
    }

    // --- UIA layer ---

    static UiaSnapshot? CaptureUiaForPointAndHwnd(POINT pt, IntPtr hwnd)
    {
        AutomationElement? ae = null;
        try { ae = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y)); } catch { }
        if (ae == null)
        {
            try { ae = AutomationElement.FromHandle(hwnd); } catch { }
        }

        if (ae == null)
            return null;

        System.Windows.Rect? br = null;
        try { br = ae.Current.BoundingRectangle; } catch { }

        string? name = null, aid = null, ctype = null, uiaCls = null, fw = null, help = null, legacyName = null, legacyDesc = null;
        try { name = ae.Current.Name; } catch { }
        try { aid = ae.Current.AutomationId; } catch { }
        try { ctype = ae.Current.ControlType?.ProgrammaticName; } catch { }
        try { uiaCls = ae.Current.ClassName; } catch { }
        try { fw = ae.Current.FrameworkId; } catch { }
        try
        {
            var val = ae.GetCurrentPropertyValue(AutomationElement.HelpTextProperty);
            help = val as string;
        }
        catch { }
        legacyName = null;
        legacyDesc = null;

        var parentChain = BuildParentChain(ae);

        return new UiaSnapshot(
            name,
            aid,
            ctype,
            uiaCls,
            fw,
            help,
            legacyName,
            legacyDesc,
            br,
            parentChain
        );
    }

    static IReadOnlyList<SpyUiaNode> BuildParentChain(AutomationElement? element)
    {
        if (element == null)
            return Array.Empty<SpyUiaNode>();

        var result = new List<SpyUiaNode>();

        try
        {
            var walker = TreeWalker.ControlViewWalker;

            AutomationElement? parent = null;
            try { parent = walker.GetParent(element); } catch { parent = null; }

            var depthGuard = 0;
            while (parent != null && depthGuard < 64)
            {
                depthGuard++;

                string? name = null, aid = null, ctype = null, cls = null, fw = null;
                try { name = parent.Current.Name; } catch { }
                try { aid = parent.Current.AutomationId; } catch { }
                try { ctype = parent.Current.ControlType?.ProgrammaticName; } catch { }
                try { cls = parent.Current.ClassName; } catch { }
                try { fw = parent.Current.FrameworkId; } catch { }

                result.Add(new SpyUiaNode(name, aid, ctype, cls, fw));

                AutomationElement? next = null;
                try { next = walker.GetParent(parent); } catch { next = null; }
                parent = next;
            }
        }
        catch
        {
            // ignore and return what we have
        }

        return result;
    }

    // --- MSAA / IAccessible layer (via UIA LegacyIAccessiblePattern) ---

    static MsaaSnapshot? CaptureMsaaFromPoint(POINT pt)
    {
        try
        {
            var hr = AccessibleObjectFromPoint(pt, out var acc, out var varChild);
            if (hr < 0 || acc == null)
                return null;

            object child = varChild;
            int? childId = null;
            if (varChild is int ci && ci != 0)
                childId = ci;

            string? name = null;
            string? role = null;
            string? state = null;
            string? value = null;
            string? desc = null;

            try { name = acc.get_accName(child); } catch { }
            try
            {
                var roleObj = acc.get_accRole(child);
                role = roleObj?.ToString();
            }
            catch { }

            try
            {
                var stateObj = acc.get_accState(child);
                state = stateObj?.ToString();
            }
            catch { }

            try { value = acc.get_accValue(child); } catch { }
            try { desc = acc.get_accDescription(child); } catch { }

            if (string.IsNullOrEmpty(name) &&
                string.IsNullOrEmpty(role) &&
                string.IsNullOrEmpty(state) &&
                string.IsNullOrEmpty(value) &&
                string.IsNullOrEmpty(desc) &&
                childId == null)
                return null;

            return new MsaaSnapshot(name, role, childId, state, value, desc);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT Point);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromPoint(POINT pt, [MarshalAs(UnmanagedType.Interface)] out IAccessible acc, [MarshalAs(UnmanagedType.Struct)] out object varChild);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr hwndParent, POINT pt, uint uFlags);

    const uint WM_NCHITTEST = 0x0084;
    const int HTNOWHERE = 0;
    const int HTCLIENT = 1;
    const int HTCAPTION = 2;
    const int HTSYSMENU = 3;
    const int HTGROWBOX = 4;
    const int HTSIZE = 4;
    const int HTMINBUTTON = 8;
    const int HTMAXBUTTON = 9;
    const int HTCLOSE = 20;

    const uint CWP_SKIPINVISIBLE = 0x0001;
    const uint CWP_SKIPDISABLED = 0x0002;
    const uint CWP_SKIPTRANSPARENT = 0x0004;

    static IntPtr GetDeepestHwndFromPoint(POINT screenPt)
    {
        var hwnd = WindowFromPoint(screenPt);
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr current = hwnd;

        while (true)
        {
            var clientPt = screenPt;
            if (!ScreenToClient(current, ref clientPt))
                break;

            var child = ChildWindowFromPointEx(
                current,
                clientPt,
                CWP_SKIPDISABLED | CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT
            );

            if (child == IntPtr.Zero || child == current)
                break;

            current = child;
        }

        // можно дополнительно игнорировать собственный процесс ObjectSpy
        try
        {
            GetWindowThreadProcessId(current, out var pid);
            var thisPid = Process.GetCurrentProcess().Id;
            if ((int)pid == thisPid)
            {
                // если попали в собственное окно — вернём исходный hwnd
                return hwnd;
            }
        }
        catch
        {
            // игнорируем ошибки определения процесса
        }

        return current;
    }

    static (int? code, string? area) HitTestNonClient(IntPtr hwnd, POINT ptScreen)
    {
        // lParam для WM_NCHITTEST ожидает координаты курсора в экране: LOWORD = X, HIWORD = Y
        int x = ptScreen.X;
        int y = ptScreen.Y;
        var lParam = new IntPtr((y << 16) | (x & 0xFFFF));

        var result = SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, lParam);
        int code = unchecked((int)result.ToInt64());

        string area = code switch
        {
            HTCLIENT => "Client",
            HTCAPTION => "Caption",
            HTSYSMENU => "SysMenu",
            HTMINBUTTON => "MinButton",
            HTMAXBUTTON => "MaxButton",
            HTCLOSE => "CloseButton",
            HTGROWBOX or HTSIZE => "SizeGrip",
            HTNOWHERE => "Nowhere",
            _ => $"Unknown({code})"
        };

        return (code, area);
    }

    static string GetClassNameStr(IntPtr hwnd)
    {
        var buf = new char[512];
        var len = GetClassName(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }

    static string GetWindowTextStr(IntPtr hwnd)
    {
        var buf = new char[2048];
        var len = GetWindowText(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }

    static Rectangle GetRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    static string? FindVclNameCandidate(Win32Snapshot win32, UiaSnapshot? uia, MsaaSnapshot? msaa)
    {
        if (IsVclName(uia?.AutomationId)) return uia!.AutomationId!.Trim();
        if (IsVclName(uia?.Name)) return uia!.Name!.Trim();
        if (IsVclName(uia?.LegacyName)) return uia!.LegacyName!.Trim();
        if (IsVclName(uia?.LegacyDescription)) return uia!.LegacyDescription!.Trim();
        if (IsVclName(uia?.HelpText)) return uia!.HelpText!.Trim();
        if (IsVclName(msaa?.Name)) return msaa!.Name!.Trim();
        if (IsVclName(msaa?.Role)) return msaa!.Role!.Trim();
        if (IsVclName(msaa?.Description)) return msaa!.Description!.Trim();
        if (IsVclName(msaa?.Value)) return msaa!.Value!.Trim();
        if (IsVclName(win32.Win32Text)) return win32.Win32Text.Trim();
        if (IsVclName(win32.Win32Class)) return win32.Win32Class.Trim();
        return null;
    }

    static bool IsVclName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length < 3 || t.Length > 64) return false;
        if (!IsLetterOrUnderscore(t[0])) return false;
        for (int i = 1; i < t.Length; i++)
        {
            char c = t[i];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    static bool IsLetterOrUnderscore(char c)
    {
        return char.IsLetter(c) || c == '_';
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
