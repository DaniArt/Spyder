using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Spy.Core.Vcl;

public sealed class VclHookManager : IDisposable
{
    const int WH_GETMESSAGE = 3;
    const uint WM_QUIT = 0x0012;

    IntPtr _hHook = IntPtr.Zero;
    IntPtr _hMod = IntPtr.Zero;
    Thread? _msgThread;
    uint _msgThreadId;
    uint _targetThreadId;
    int _targetPid;
    string? _lastError;

    public bool IsRunning => _hHook != IntPtr.Zero && _msgThread != null && _msgThread.IsAlive;
    public string? LastError => _lastError;

    public bool Start(int pid)
    {
        Stop();
        _lastError = null;
        _targetPid = pid;

        if (!FindGuiThread(pid, out _targetThreadId))
        {
            _lastError = "GUI thread not found";
            return false;
        }

        bool target64 = IsTarget64Bit(pid);
        bool self64 = Environment.Is64BitProcess;
        if (self64 != target64)
        {
            _lastError = target64
                ? "Target is x64. Please run dist\\x64\\Spy.UI.exe"
                : "Target is x86. Please run dist\\x86\\Spy.UI.exe";
            Spy.Core.Logging.Logger.Error(_lastError);
            return false;
        }
        string baseDir = AppContext.BaseDirectory;
        string dllPath = System.IO.Path.Combine(baseDir, self64 ? "VclHook64.dll" : "VclHook32.dll");
        Spy.Core.Logging.Logger.Info($"VCL: Will load hook dll: {dllPath}");
        _hMod = LoadLibrary(dllPath);
        if (_hMod == IntPtr.Zero)
        {
            _lastError = $"DLL not found or LoadLibrary failed (err={Marshal.GetLastWin32Error()}): {dllPath}";
            Spy.Core.Logging.Logger.Error(_lastError);
            return false;
        }

        IntPtr pHook = GetProcAddress(_hMod, "GetMsgHookProc");
        if (pHook == IntPtr.Zero)
        {
            _lastError = "GetMsgHookProc not found";
            Spy.Core.Logging.Logger.Error(_lastError);
            return false;
        }

        _msgThread = new Thread(() => MessageLoop(pHook))
        {
            IsBackground = true,
            Name = "VCL Hook Loop"
        };
        _msgThread.SetApartmentState(ApartmentState.STA);
        _msgThread.Start();

        int wait = 0;
        while (_msgThreadId == 0 && wait < 1000)
        {
            Thread.Sleep(10);
            wait += 10;
        }

        return IsRunning;
    }

    public void Stop()
    {
        try
        {
            if (_msgThread != null && _msgThreadId != 0)
            {
                PostThreadMessage(_msgThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }

        try
        {
            if (_msgThread != null)
            {
                if (!_msgThread.Join(500))
                    _msgThread.Interrupt();
            }
        }
        catch { }
        _msgThread = null;
        _msgThreadId = 0;
        _hHook = IntPtr.Zero;
        _hMod = IntPtr.Zero;
    }

    void MessageLoop(IntPtr pHookProc)
    {
        _msgThreadId = GetCurrentThreadId();
        _hHook = SetWindowsHookEx(WH_GETMESSAGE, pHookProc, _hMod, _targetThreadId);
        if (_hHook == IntPtr.Zero)
        {
            _lastError = $"SetWindowsHookEx failed (err={Marshal.GetLastWin32Error()})";
            Spy.Core.Logging.Logger.Error(_lastError);
            return;
        }
        Spy.Core.Logging.Logger.Info($"Hook installed on TID={_targetThreadId}");

        // Wake up target thread to process hook immediately
        PostThreadMessage(_targetThreadId, 0x0000 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (_hHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hHook);
                _hHook = IntPtr.Zero;
            }
        }
    }

    static bool FindGuiThread(int pid, out uint threadId)
    {
        uint tid = 0;
        EnumWindows((h, l) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if ((int)p == pid)
            {
                tid = GetWindowThreadProcessId(h, out _);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        threadId = tid;
        return threadId != 0;
    }

    // Correct architecture detection
    public bool IsTarget64Bit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            
            // Log attempt
            Spy.Core.Logging.Logger.Info($"Checking architecture for PID={pid} ({p.ProcessName})");

            // Method 1: IsWow64Process2 (Win10+ 1511)
            ushort processMachine = 0;
            ushort nativeMachine = 0;
            
            // Check if IsWow64Process2 is available (dynamically)
            IntPtr kernel32 = GetModuleHandle("kernel32.dll");
            IntPtr pIsWow64Process2 = GetProcAddress(kernel32, "IsWow64Process2");
            
            if (pIsWow64Process2 != IntPtr.Zero)
            {
                var isWow64Process2 = Marshal.GetDelegateForFunctionPointer<IsWow64Process2Delegate>(pIsWow64Process2);
                if (isWow64Process2(p.Handle, out processMachine, out nativeMachine))
                {
                    Spy.Core.Logging.Logger.Info($"IsWow64Process2: processMachine={processMachine:X}, nativeMachine={nativeMachine:X}");
                    
                    if (processMachine != IMAGE_FILE_MACHINE_UNKNOWN)
                    {
                        // WOW64 process (e.g. x86 on x64) -> 32-bit
                        Spy.Core.Logging.Logger.Info("Target is WOW64 (32-bit)");
                        return false;
                    }
                    else
                    {
                        // Native process. Check host architecture.
                        if (nativeMachine == IMAGE_FILE_MACHINE_AMD64 || nativeMachine == IMAGE_FILE_MACHINE_ARM64)
                        {
                            Spy.Core.Logging.Logger.Info("Target is Native 64-bit");
                            return true;
                        }
                        else
                        {
                            Spy.Core.Logging.Logger.Info("Target is Native 32-bit");
                            return false;
                        }
                    }
                }
            }

            // Method 2: IsWow64Process (Legacy)
            if (IsWow64Process(p.Handle, out var isWow64))
            {
                Spy.Core.Logging.Logger.Info($"IsWow64Process: {isWow64}");
                if (isWow64)
                {
                    // Running under WOW64 -> 32-bit on 64-bit OS
                    return false; 
                }
                else
                {
                    // Either 64-bit native on 64-bit OS, or 32-bit native on 32-bit OS
                    bool os64 = Environment.Is64BitOperatingSystem;
                    Spy.Core.Logging.Logger.Info($"Target is Native (OS 64-bit: {os64}) -> {(os64 ? "64-bit" : "32-bit")}");
                    return os64;
                }
            }
        }
        catch (Exception ex)
        {
            Spy.Core.Logging.Logger.Error($"Arch check failed: {ex.Message}");
        }
        
        // Fallback: assume same as current process? Or 32-bit?
        // Safest is to return Environment.Is64BitOperatingSystem if we can't determine, 
        // but let's default to OS bitness.
        return Environment.Is64BitOperatingSystem; 
    }

    // P/Invoke
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string lpFileName);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, IntPtr lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsWow64Process(IntPtr hProcess, out bool lpSystemInfo);
    
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    delegate bool IsWow64Process2Delegate(IntPtr hProcess, out ushort pProcessMachine, out ushort pNativeMachine);

    const ushort IMAGE_FILE_MACHINE_UNKNOWN = 0;
    const ushort IMAGE_FILE_MACHINE_I386 = 0x014c;
    const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;
    const ushort IMAGE_FILE_MACHINE_ARM64 = 0xAA64;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    // diagnostics: check if hook dll loaded into target process
    public bool IsHookDllLoaded()
    {
        try
        {
            using var p = Process.GetProcessById(_targetPid);
            var is64 = IsTarget64Bit(_targetPid);
            var name = is64 ? "VclHook64.dll" : "VclHook32.dll";
            foreach (ProcessModule m in p.Modules)
            {
                if (m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Spy.Core.Logging.Logger.Error($"IsHookDllLoaded check failed: {ex.Message}");
        }
        return false;
    }

    public void Dispose() => Stop();
}
