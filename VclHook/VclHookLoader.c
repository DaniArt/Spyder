#include <windows.h>
#include <stdio.h>

extern IMAGE_DOS_HEADER __ImageBase;

static BOOL BuildHookDllPath(DWORD targetPid, WCHAR* outPath, DWORD cch)
{
    if (!outPath || cch == 0) return FALSE;
    outPath[0] = 0;

    WCHAR selfPath[MAX_PATH];
    if (!GetModuleFileNameW((HMODULE)&__ImageBase, selfPath, MAX_PATH)) return FALSE;
    WCHAR* slash = wcsrchr(selfPath, L'\\');
    if (!slash) return FALSE;
    slash[1] = 0;

    HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, targetPid);
    if (!hProc) return FALSE;
    BOOL wowTarget = FALSE, wowSelf = FALSE;
    IsWow64Process(hProc, &wowTarget);
    IsWow64Process(GetCurrentProcess(), &wowSelf);
    CloseHandle(hProc);

    const WCHAR* dllName = L"VclHook32.dll";
    if (!wowTarget && !wowSelf) dllName = L"VclHook64.dll";
    wsprintfW(outPath, L"%s%s", selfPath, dllName);
    if (GetFileAttributesW(outPath) == INVALID_FILE_ATTRIBUTES)
    {
        wsprintfW(outPath, L"%sVclHook32.dll", selfPath);
    }
    return GetFileAttributesW(outPath) != INVALID_FILE_ATTRIBUTES;
}

static BOOL WaitHookLoadedMessage(DWORD targetPid, DWORD timeoutMs)
{
    char msgName[128];
    wsprintfA(msgName, "Spyder.HookLoaded.%lu", (unsigned long)targetPid);
    UINT msgId = RegisterWindowMessageA(msgName);
    if (!msgId) return FALSE;

    MSG msg;
    PeekMessage(&msg, NULL, WM_USER, WM_USER, PM_NOREMOVE);
    DWORD start = GetTickCount();
    while (GetTickCount() - start < timeoutMs)
    {
        while (PeekMessage(&msg, NULL, 0, 0, PM_REMOVE))
        {
            if (msg.message == msgId && (DWORD)msg.wParam == targetPid)
                return TRUE;
        }
        Sleep(20);
    }
    return FALSE;
}

__declspec(dllexport) BOOL InstallHooks(DWORD targetPid)
{
    if (!targetPid) return FALSE;
    WCHAR dllPath[MAX_PATH];
    if (!BuildHookDllPath(targetPid, dllPath, MAX_PATH)) return FALSE;

    HANDLE hProc = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, targetPid);
    if (!hProc) return FALSE;

    SIZE_T cb = (wcslen(dllPath) + 1) * sizeof(WCHAR);
    LPVOID remote = VirtualAllocEx(hProc, NULL, cb, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) { CloseHandle(hProc); return FALSE; }

    SIZE_T wr = 0;
    if (!WriteProcessMemory(hProc, remote, dllPath, cb, &wr) || wr != cb)
    {
        VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
        CloseHandle(hProc);
        return FALSE;
    }

    HMODULE hKernel = GetModuleHandleW(L"kernel32.dll");
    FARPROC pLoadLibraryW = hKernel ? GetProcAddress(hKernel, "LoadLibraryW") : NULL;
    if (!pLoadLibraryW)
    {
        VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
        CloseHandle(hProc);
        return FALSE;
    }

    HANDLE hThread = CreateRemoteThread(hProc, NULL, 0, (LPTHREAD_START_ROUTINE)pLoadLibraryW, remote, 0, NULL);
    if (!hThread)
    {
        VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
        CloseHandle(hProc);
        return FALSE;
    }

    BOOL ok = FALSE;
    if (WaitForSingleObject(hThread, 5000) == WAIT_OBJECT_0)
    {
        DWORD exitCode = 0;
        if (GetExitCodeThread(hThread, &exitCode) && exitCode != 0)
            ok = TRUE;
    }
    CloseHandle(hThread);
    VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
    CloseHandle(hProc);

    if (!ok) return FALSE;
    WaitHookLoadedMessage(targetPid, 5000);
    return TRUE;
}

__declspec(dllexport) BOOL UninstallHooks(DWORD targetPid)
{
    if (!targetPid) return FALSE;
    char pipeName[128];
    wsprintfA(pipeName, "\\\\.\\pipe\\Spyder.VclHelper.%lu", (unsigned long)targetPid);

    HANDLE hPipe = CreateFileA(pipeName, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (hPipe == INVALID_HANDLE_VALUE) return FALSE;

    const char* req = "{\"cmd\":\"shutdown\"}";
    DWORD len = (DWORD)strlen(req);
    DWORD wr = 0;
    BOOL ok = WriteFile(hPipe, &len, sizeof(len), &wr, NULL) && wr == sizeof(len);
    if (ok) ok = WriteFile(hPipe, req, len, &wr, NULL) && wr == len;
    if (ok)
    {
        DWORD outLen = 0;
        DWORD rd = 0;
        if (ReadFile(hPipe, &outLen, sizeof(outLen), &rd, NULL) && rd == sizeof(outLen) && outLen > 0 && outLen < 4096)
        {
            char buf[4096];
            ReadFile(hPipe, buf, outLen, &rd, NULL);
        }
    }
    CloseHandle(hPipe);
    return ok;
}
