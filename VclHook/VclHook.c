/*
Copyright 2026 Daniyar Sagatov
Licensed under the Apache License 2.0
*/

// Minimal native hook DLL that runs a pipe server inside target process
#include <windows.h>
#include <stdio.h>
#include <synchapi.h>
#include "VclBridge.h"

#pragma comment(lib, "user32.lib")

static HHOOK g_hHook = NULL;
static HANDLE g_hThread = NULL;
static volatile BOOL g_stop = FALSE;
static volatile LONG g_detachState = 0;
static INIT_ONCE g_initOnce = INIT_ONCE_STATIC_INIT;
static volatile DWORD g_uiThreadId = 0;
static UINT g_wmSpyderDispatch = 0;
static volatile DWORD g_pipeBackoffUntil = 0;
#define SPYDER_HOOK_VERSION "2.0.0"

typedef struct SpyderRequestTag
{
    char cmd[64];
    HWND hwnd;
    void* self;
    int x;
    int y;
    int depth;
    char classNameFilter[128];
    char titleFilter[256];
    HANDLE hDoneEvent;
    char* resultBuf;
    size_t resultCap;
    BOOL shouldStopServer;
} SpyderRequest;

static DWORD WINAPI PipeThread(LPVOID lpParam);
static void BuildJsonResponse(char* buf, size_t cap, HWND hwnd);
static BOOL ReadExact(HANDLE h, void* buf, DWORD len);
static BOOL WriteExact(HANDLE h, const void* buf, DWORD len);
static void Utf16ToUtf8(const WCHAR* src, char* dst, int dstCap);
static const char* ControlTypeToString(VclControlType t);
static void BroadcastHookLoaded(void);
static BOOL EnsureUiDispatcherReady(void);
static BOOL DispatchToUi(SpyderRequest* req);
static void HandleCommandOnCurrentThread(const SpyderRequest* req, char* json, size_t jsonCap, BOOL* shouldStopServer);
__declspec(dllexport) void DetachFromServer(void);

#ifdef SPYDER_DEBUG
#define DBGLOGA(msg) OutputDebugStringA(msg)
#else
#define DBGLOGA(msg) ((void)0)
#endif

static BOOL CALLBACK StartPipeServerOnce(PINIT_ONCE InitOnce, PVOID Parameter, PVOID *Context)
{
    if (InterlockedCompareExchange(&g_detachState, 0, 0) != 0)
        return FALSE;
    char msg[128];
    wsprintfA(msg, "[Spyder.Hook] Pipe init requested in PID=%lu", (unsigned long)GetCurrentProcessId());
    DBGLOGA(msg);
    g_stop = FALSE;
    g_hThread = CreateThread(NULL, 0, PipeThread, NULL, 0, NULL);
    if (!g_hThread)
    {
        wsprintfA(msg, "[Spyder.Hook] CreateThread(PipeThread) failed, err=%lu", GetLastError());
        DBGLOGA(msg);
        return FALSE;
    }
    return TRUE;
}

__declspec(dllexport) LRESULT CALLBACK GetMsgHookProc(int code, WPARAM wParam, LPARAM lParam)
{
    InitOnceExecuteOnce(&g_initOnce, StartPipeServerOnce, NULL, NULL);
    if (code >= 0)
    {
        g_uiThreadId = GetCurrentThreadId();
        if (g_wmSpyderDispatch == 0)
            EnsureUiDispatcherReady();
        MSG* m = (MSG*)lParam;
        if (m && wParam == PM_REMOVE && m->message == g_wmSpyderDispatch)
        {
            SpyderRequest* req = (SpyderRequest*)m->lParam;
            if (req && req->resultBuf && req->resultCap > 0)
            {
                BOOL shouldStop = FALSE;
                __try
                {
                    HandleCommandOnCurrentThread(req, req->resultBuf, req->resultCap, &shouldStop);
                }
                __except (EXCEPTION_EXECUTE_HANDLER)
                {
                    snprintf(req->resultBuf, req->resultCap, "{\"ok\":false,\"error\":\"ui_exception\"}");
                }
                req->shouldStopServer = shouldStop;
                if (req->hDoneEvent) SetEvent(req->hDoneEvent);
                m->message = WM_NULL;
            }
        }
    }
    return CallNextHookEx(g_hHook, code, wParam, lParam);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)
{
    if (ul_reason_for_call == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        g_stop = FALSE;
        InterlockedExchange(&g_detachState, 0);
        char msg[128];
        wsprintfA(msg, "[Spyder.Hook] Loaded into PID=%lu", (unsigned long)GetCurrentProcessId());
        DBGLOGA(msg);
    }
    else if (ul_reason_for_call == DLL_PROCESS_DETACH)
    {
        DetachFromServer();
    }
    return TRUE;
}

__declspec(dllexport) void DetachFromServer(void)
{
    if (InterlockedCompareExchange(&g_detachState, 1, 0) != 0)
        return;
    g_stop = TRUE;
    if (g_hThread)
    {
        DWORD tid = GetThreadId(g_hThread);
        if (tid != GetCurrentThreadId())
            WaitForSingleObject(g_hThread, 2000);
        CloseHandle(g_hThread);
        g_hThread = NULL;
    }
    InterlockedExchange(&g_detachState, 2);
}

static BOOL EnsureUiDispatcherReady(void)
{
    if (g_wmSpyderDispatch == 0)
        g_wmSpyderDispatch = RegisterWindowMessageA("SpyderDispatch");
    return g_wmSpyderDispatch != 0 && g_uiThreadId != 0;
}

static BOOL DispatchToUi(SpyderRequest* req)
{
    if (!req || !req->resultBuf || req->resultCap == 0) return FALSE;
    req->hDoneEvent = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!req->hDoneEvent)
    {
        snprintf(req->resultBuf, req->resultCap, "{\"ok\":false,\"error\":\"event_create_failed\"}");
        return FALSE;
    }

    if (!EnsureUiDispatcherReady())
    {
        snprintf(req->resultBuf, req->resultCap, "{\"ok\":false,\"error\":\"dispatch_not_ready\"}");
        CloseHandle(req->hDoneEvent);
        req->hDoneEvent = NULL;
        return FALSE;
    }

    DWORD targetTid = g_uiThreadId;
    if (req->hwnd)
    {
        DWORD pid = 0;
        DWORD hwndTid = GetWindowThreadProcessId(req->hwnd, &pid);
        if (hwndTid != 0 && pid == GetCurrentProcessId())
            targetTid = hwndTid;
    }

    if (targetTid == 0 || !PostThreadMessageW(targetTid, g_wmSpyderDispatch, 0, (LPARAM)req))
    {
        snprintf(req->resultBuf, req->resultCap, "{\"ok\":false,\"error\":\"post_failed\"}");
        CloseHandle(req->hDoneEvent);
        req->hDoneEvent = NULL;
        return FALSE;
    }

    DWORD wr = WaitForSingleObject(req->hDoneEvent, 500);
    CloseHandle(req->hDoneEvent);
    req->hDoneEvent = NULL;

    if (wr != WAIT_OBJECT_0)
    {
        snprintf(req->resultBuf, req->resultCap, "{\"ok\":false,\"error\":\"timeout\"}");
        return FALSE;
    }
    return TRUE;
}

static DWORD WINAPI PipeThread(LPVOID lpParam)
{
    DWORD pid = GetCurrentProcessId();
    char pipeName[128];
    wsprintfA(pipeName, "\\\\.\\pipe\\Spyder.VclHelper.%lu", (unsigned long)pid);
    {
        char msg[256];
        wsprintfA(msg, "[Spyder.Hook] Pipe server starting: %s", pipeName);
        DBGLOGA(msg);
    }
    BroadcastHookLoaded();

    for (;;)
    {
        if (g_stop) break;
        if (g_pipeBackoffUntil != 0 && GetTickCount() < g_pipeBackoffUntil)
        {
            Sleep(150);
            continue;
        }
        HANDLE hPipe = CreateNamedPipeA(
            pipeName,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            4, 65536, 65536, 2000, NULL);
            
        if (hPipe == INVALID_HANDLE_VALUE)
        {
            Sleep(500);
            continue;
        }
        
        BOOL ok = ConnectNamedPipe(hPipe, NULL) ? TRUE : (GetLastError() == ERROR_PIPE_CONNECTED);
        if (!ok)
        {
            CloseHandle(hPipe);
            continue;
        }
        
        __try 
        {
            for (;;)
            {
                if (g_stop) break;
                BOOL shouldStopServer = FALSE;
                DWORD len;
                if (!ReadExact(hPipe, &len, sizeof(len))) break;
                if (len == 0 || len > 1024 * 1024) break;
                char* buf = (char*)HeapAlloc(GetProcessHeap(), 0, len + 1);
                if (!buf) break;
                if (!ReadExact(hPipe, buf, len))
                {
                    HeapFree(GetProcessHeap(), 0, buf);
                    break;
                }
                buf[len] = 0;
                
                // Simple JSON parser
                char cmd[64] = "";
                HWND hwnd = NULL;
                void* self = NULL;
                int x = 0;
                int y = 0;
                int depth = 5;
                char classNameFilter[128] = "";
                char titleFilter[256] = "";
                
                // Parse "cmd"
                char* pCmd = strstr(buf, "\"cmd\"");
                if (pCmd) {
                    pCmd = strchr(pCmd, ':');
                    if (pCmd) {
                        pCmd++;
                        while (*pCmd == ' ' || *pCmd == '"') pCmd++;
                        char* end = strchr(pCmd, '"');
                        if (end) {
                            int l = (int)(end - pCmd);
                            if (l > 63) l = 63;
                            lstrcpynA(cmd, pCmd, l + 1);
                        }
                    }
                }
                
                // Parse "hwnd"
                char* pHwnd = strstr(buf, "\"hwnd\"");
                if (pHwnd) {
                    pHwnd = strchr(pHwnd, ':');
                    if (pHwnd) { pHwnd++; hwnd = (HWND)(UINT_PTR)_strtoui64(pHwnd, NULL, 10); }
                }
                
                // Parse "self"
                char* pSelf = strstr(buf, "\"self\"");
                if (pSelf) {
                    pSelf = strchr(pSelf, ':');
                    if (pSelf) {
                        pSelf++;
                        while (*pSelf == ' ' || *pSelf == '"') pSelf++;
                        self = (void*)_strtoui64(pSelf, NULL, 16);
                    }
                }

                char* pX = strstr(buf, "\"x\"");
                if (pX) {
                    pX = strchr(pX, ':');
                    if (pX) { pX++; x = (int)strtol(pX, NULL, 10); }
                }
                char* pY = strstr(buf, "\"y\"");
                if (pY) {
                    pY = strchr(pY, ':');
                    if (pY) { pY++; y = (int)strtol(pY, NULL, 10); }
                }
                char* pDepth = strstr(buf, "\"depth\"");
                if (pDepth) {
                    pDepth = strchr(pDepth, ':');
                    if (pDepth) { pDepth++; depth = (int)strtol(pDepth, NULL, 10); }
                }
                char* pClassName = strstr(buf, "\"class_name\"");
                if (pClassName) {
                    pClassName = strchr(pClassName, ':');
                    if (pClassName) {
                        pClassName++;
                        while (*pClassName == ' ' || *pClassName == '"') pClassName++;
                        char* end = strchr(pClassName, '"');
                        if (end) {
                            int l = (int)(end - pClassName);
                            if (l > (int)sizeof(classNameFilter) - 1) l = (int)sizeof(classNameFilter) - 1;
                            lstrcpynA(classNameFilter, pClassName, l + 1);
                        }
                    }
                }
                char* pTitle = strstr(buf, "\"title\"");
                if (pTitle) {
                    pTitle = strchr(pTitle, ':');
                    if (pTitle) {
                        pTitle++;
                        while (*pTitle == ' ' || *pTitle == '"') pTitle++;
                        char* end = strchr(pTitle, '"');
                        if (end) {
                            int l = (int)(end - pTitle);
                            if (l > (int)sizeof(titleFilter) - 1) l = (int)sizeof(titleFilter) - 1;
                            lstrcpynA(titleFilter, pTitle, l + 1);
                        }
                    }
                }

                HeapFree(GetProcessHeap(), 0, buf);

                // Response buffer
                size_t jsonCap = 128 * 1024; // 128KB
                char* json = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, jsonCap); 
                if (!json) break;

                SpyderRequest req;
                ZeroMemory(&req, sizeof(req));
                lstrcpynA(req.cmd, cmd, (int)sizeof(req.cmd));
                req.hwnd = hwnd;
                req.self = self;
                req.x = x;
                req.y = y;
                req.depth = depth;
                lstrcpynA(req.classNameFilter, classNameFilter, (int)sizeof(req.classNameFilter));
                lstrcpynA(req.titleFilter, titleFilter, (int)sizeof(req.titleFilter));
                req.resultBuf = json;
                req.resultCap = jsonCap;

                if (lstrcmpiA(req.cmd, "ping") == 0 || lstrcmpiA(req.cmd, "shutdown") == 0)
                {
                    HandleCommandOnCurrentThread(&req, json, jsonCap, &shouldStopServer);
                }
                else
                {
                    BOOL dispatched = DispatchToUi(&req);
                    shouldStopServer = req.shouldStopServer;
                    if (!dispatched && json[0] == 0)
                        snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"dispatch_failed\"}");
                }
                
                DWORD outLen = (DWORD)lstrlenA(json);
                if (!WriteExact(hPipe, &outLen, sizeof(outLen))) {
                    HeapFree(GetProcessHeap(), 0, json);
                    break;
                }
                if (!WriteExact(hPipe, json, outLen)) {
                    HeapFree(GetProcessHeap(), 0, json);
                    break;
                }
                HeapFree(GetProcessHeap(), 0, json);
                if (shouldStopServer) break;
            }
        }
        __except(EXCEPTION_EXECUTE_HANDLER)
        {
            Log("[VclHook] EXCEPTION in PipeThread (code=0x%X)", GetExceptionCode());
            g_pipeBackoffUntil = GetTickCount() + 1500;
        }
        
        FlushFileBuffers(hPipe);
        DisconnectNamedPipe(hPipe);
        CloseHandle(hPipe);
    }
    return 0;
}

static void HandleCommandOnCurrentThread(const SpyderRequest* req, char* json, size_t jsonCap, BOOL* shouldStopServer)
{
    if (!req || !json || jsonCap == 0)
        return;
    if (shouldStopServer) *shouldStopServer = FALSE;

    if (lstrcmpiA(req->cmd, "resolve_by_hwnd") == 0)
    {
        if (req->hwnd) BuildJsonResponse(json, jsonCap, req->hwnd);
        else snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"missing hwnd\"}");
    }
    else if (lstrcmpiA(req->cmd, "get_children") == 0)
    {
        int count = 0;
        void** children = VclGetChildControls(req->self, &count);
        snprintf(json, jsonCap, "{\"ok\":true,\"children\":[");
        if (children)
        {
            BOOL first = TRUE;
            for (int i = 0; i < count; i++)
            {
                VclNode node;
                if (VclGetNodeBySelf(children[i], &node))
                {
                    char item[1024];
                    char cls[256] = "";
                    char name[256] = "";
                    Utf16ToUtf8(node.class_name, cls, sizeof(cls));
                    Utf16ToUtf8(node.component_name, name, sizeof(name));
                    snprintf(item, sizeof(item), "%s{\"self\":\"0x%p\",\"class\":\"%s\",\"name\":\"%s\"}",
                        first ? "" : ",", node.self, cls, name);
                    if (strlen(json) + strlen(item) < jsonCap - 16)
                    {
                        strcat_s(json, jsonCap, item);
                        first = FALSE;
                    }
                }
            }
            LocalFree(children);
        }
        if (strlen(json) < jsonCap - 4) strcat_s(json, jsonCap, "]}");
    }
    else if (lstrcmpiA(req->cmd, "get_path") == 0)
    {
        int count = 0;
        if (!req->self)
            snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"missing self\"}");
        else
        {
            VclNode* path = VclGetPathToSelf(req->self, &count);
            if (path && count > 0)
            {
                snprintf(json, jsonCap, "{\"ok\":true,\"path\":[");
                BOOL first = TRUE;
                for (int i = 0; i < count; i++)
                {
                    char item[1024];
                    char cls[256] = "";
                    char name[256] = "";
                    Utf16ToUtf8(path[i].class_name, cls, sizeof(cls));
                    Utf16ToUtf8(path[i].component_name, name, sizeof(name));
                    snprintf(item, sizeof(item), "%s{\"class\":\"%s\",\"name\":\"%s\",\"self\":\"0x%p\"}",
                        first ? "" : ",", cls, name, path[i].self);
                    if (strlen(json) + strlen(item) < jsonCap - 16)
                    {
                        strcat_s(json, jsonCap, item);
                        first = FALSE;
                    }
                }
                if (strlen(json) < jsonCap - 4) strcat_s(json, jsonCap, "]}");
                LocalFree(path);
            }
            else
            {
                snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"path_null\"}");
                if (path) LocalFree(path);
            }
        }
    }
    else if (lstrcmpiA(req->cmd, "get_properties") == 0)
    {
        char props[4096] = "";
        if (VclGetProperties(req->self, props, sizeof(props)))
            snprintf(json, jsonCap, "{\"ok\":true,\"properties\":%s}", props);
        else
            snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"failed to get properties\"}");
    }
    else if (lstrcmpiA(req->cmd, "get_full_properties") == 0)
    {
        VclFullProperties fp;
        if (VclGetFullProperties(req->self, &fp))
        {
            char cls[256] = "", name[256] = "", caption[512] = "", hint[512] = "", locator[1024] = "";
            Utf16ToUtf8(fp.class_name, cls, sizeof(cls));
            Utf16ToUtf8(fp.name, name, sizeof(name));
            Utf16ToUtf8(fp.caption, caption, sizeof(caption));
            Utf16ToUtf8(fp.hint, hint, sizeof(hint));
            Utf16ToUtf8(fp.locator, locator, sizeof(locator));
            snprintf(json, jsonCap,
                "{\"ok\":true,\"class\":\"%s\",\"name\":\"%s\",\"caption\":\"%s\",\"hint\":\"%s\",\"left\":%d,\"top\":%d,\"width\":%d,\"height\":%d,\"visible\":%s,\"enabled\":%s,\"tab_order\":%d,\"control_count\":%d,\"component_count\":%d,\"parent_self\":\"0x%p\",\"owner_self\":\"0x%p\",\"screen_left\":%d,\"screen_top\":%d,\"screen_right\":%d,\"screen_bottom\":%d,\"type\":%d,\"locator\":\"%s\"}",
                cls, name, caption, hint, fp.left, fp.top, fp.width, fp.height,
                fp.visible ? "true" : "false", fp.enabled ? "true" : "false",
                fp.tab_order, fp.control_count, fp.component_count, fp.parent_self, fp.owner_self,
                fp.screen_rect.left, fp.screen_rect.top, fp.screen_rect.right, fp.screen_rect.bottom,
                (int)fp.control_type, locator);
        }
        else
            snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"failed to get full properties\"}");
    }
    else if (lstrcmpiA(req->cmd, "recognize") == 0)
    {
        VclFullProperties fp;
        if (VclGetFullProperties(req->self, &fp))
        {
            char typeName[64] = "";
            char displayName[256] = "";
            char locator[1024] = "";
            snprintf(typeName, sizeof(typeName), "%s", ControlTypeToString(fp.control_type));
            if (fp.name[0]) Utf16ToUtf8(fp.name, displayName, sizeof(displayName));
            else Utf16ToUtf8(fp.class_name, displayName, sizeof(displayName));
            Utf16ToUtf8(fp.locator, locator, sizeof(locator));
            snprintf(json, jsonCap, "{\"ok\":true,\"type\":\"%s\",\"display_name\":\"%s\",\"locator\":\"%s\",\"self\":\"0x%p\"}",
                typeName, displayName, locator, req->self);
        }
        else
            snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"recognize failed\"}");
    }
    else if (lstrcmpiA(req->cmd, "get_tree") == 0)
    {
        void* rootSelf = req->self;
        if (!rootSelf && req->hwnd)
        {
            VclNode rootNode;
            ZeroMemory(&rootNode, sizeof(rootNode));
            if (VclResolveByHwnd(req->hwnd, &rootNode))
                rootSelf = rootNode.self;
        }
        if (!rootSelf)
            snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"missing root\"}");
        else
        {
            size_t treeCap = 512 * 1024;
            char* tree = (char*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, treeCap);
            if (tree && VclBuildTree(rootSelf, req->depth, tree, treeCap))
                snprintf(json, jsonCap, "{\"ok\":true,\"tree\":%s}", tree);
            else
                snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"tree build failed\"}");
            if (tree) HeapFree(GetProcessHeap(), 0, tree);
        }
    }
    else if (lstrcmpiA(req->cmd, "find_window") == 0)
    {
        VclNode node;
        ZeroMemory(&node, sizeof(node));
        BOOL found = FALSE;
        HWND foundHwnd = NULL;
        HWND cur = GetTopWindow(NULL);
        DWORD pid = GetCurrentProcessId();
        while (cur)
        {
            DWORD wpid = 0;
            GetWindowThreadProcessId(cur, &wpid);
            if (wpid == pid)
            {
                WCHAR wcls[256] = { 0 };
                WCHAR wtitle[256] = { 0 };
                GetClassNameW(cur, wcls, 256);
                GetWindowTextW(cur, wtitle, 256);
                char clsA[256] = "";
                char titleA[256] = "";
                Utf16ToUtf8(wcls, clsA, sizeof(clsA));
                Utf16ToUtf8(wtitle, titleA, sizeof(titleA));
                BOOL clsOk = req->classNameFilter[0] == 0 || _stricmp(clsA, req->classNameFilter) == 0;
                BOOL titleOk = req->titleFilter[0] == 0 || strstr(titleA, req->titleFilter) != NULL;
                if (clsOk && titleOk && VclResolveByHwnd(cur, &node))
                {
                    found = TRUE;
                    foundHwnd = cur;
                    break;
                }
            }
            cur = GetNextWindow(cur, GW_HWNDNEXT);
        }
        if (found)
        {
            char cls[256] = "", name[256] = "";
            Utf16ToUtf8(node.class_name, cls, sizeof(cls));
            Utf16ToUtf8(node.component_name, name, sizeof(name));
            snprintf(json, jsonCap, "{\"ok\":true,\"hwnd\":%I64u,\"self\":\"0x%p\",\"class\":\"%s\",\"name\":\"%s\"}",
                (unsigned long long)(UINT_PTR)foundHwnd, node.self, cls, name);
        }
        else
            snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"not found\"}");
    }
    else if (lstrcmpiA(req->cmd, "get_window_list") == 0)
    {
        snprintf(json, jsonCap, "{\"ok\":true,\"windows\":[");
        DWORD pid = GetCurrentProcessId();
        HWND cur = GetTopWindow(NULL);
        BOOL first = TRUE;
        while (cur)
        {
            DWORD wpid = 0;
            GetWindowThreadProcessId(cur, &wpid);
            if (wpid == pid)
            {
                VclNode node;
                ZeroMemory(&node, sizeof(node));
                if (VclResolveByHwnd(cur, &node))
                {
                    char cls[256] = "", name[256] = "";
                    Utf16ToUtf8(node.class_name, cls, sizeof(cls));
                    Utf16ToUtf8(node.component_name, name, sizeof(name));
                    char item[640];
                    snprintf(item, sizeof(item), "%s{\"hwnd\":%I64u,\"self\":\"0x%p\",\"class\":\"%s\",\"name\":\"%s\"}",
                        first ? "" : ",", (unsigned long long)(UINT_PTR)cur, node.self, cls, name);
                    if (strlen(json) + strlen(item) < jsonCap - 8)
                    {
                        strcat_s(json, jsonCap, item);
                        first = FALSE;
                    }
                }
            }
            cur = GetNextWindow(cur, GW_HWNDNEXT);
        }
        strcat_s(json, jsonCap, "]}");
    }
    else if (lstrcmpiA(req->cmd, "hit_test") == 0)
    {
        VclNode node;
        ZeroMemory(&node, sizeof(node));
        BOOL hit = FALSE;
        if (req->hwnd && req->self)
        {
            g_isCapture = TRUE;
            __try { hit = VclHitTest(req->hwnd, req->self, req->x, req->y, &node); }
            __except (EXCEPTION_EXECUTE_HANDLER) { hit = FALSE; }
            g_isCapture = FALSE;
        }
        if (hit)
        {
            char cls[256] = "";
            char name[256] = "";
            Utf16ToUtf8(node.class_name, cls, sizeof(cls));
            Utf16ToUtf8(node.component_name, name, sizeof(name));
            snprintf(json, jsonCap, "{\"ok\":true,\"hit\":true,\"self\":\"0x%p\",\"class\":\"%s\",\"name\":\"%s\",\"left\":%d,\"top\":%d,\"right\":%d,\"bottom\":%d}",
                node.self, cls, name, node.bounds.left, node.bounds.top, node.bounds.right, node.bounds.bottom);
        }
        else
            snprintf(json, jsonCap, "{\"ok\":true,\"hit\":false}");
    }
    else if (lstrcmpiA(req->cmd, "hit_test_overlay") == 0)
    {
        VclNode node;
        ZeroMemory(&node, sizeof(node));
        BOOL hit = FALSE;
        if (req->hwnd && req->self)
        {
            g_isCapture = FALSE;
            __try { hit = VclHitTest(req->hwnd, req->self, req->x, req->y, &node); }
            __except (EXCEPTION_EXECUTE_HANDLER) { hit = FALSE; }
            g_isCapture = FALSE;
        }
        if (hit)
        {
            char cls[256] = "";
            char name[256] = "";
            Utf16ToUtf8(node.class_name, cls, sizeof(cls));
            Utf16ToUtf8(node.component_name, name, sizeof(name));
            snprintf(json, jsonCap, "{\"ok\":true,\"hit\":true,\"self\":\"0x%p\",\"class\":\"%s\",\"name\":\"%s\",\"left\":%d,\"top\":%d,\"right\":%d,\"bottom\":%d}",
                node.self, cls, name, node.bounds.left, node.bounds.top, node.bounds.right, node.bounds.bottom);
        }
        else
            snprintf(json, jsonCap, "{\"ok\":true,\"hit\":false}");
    }
    else if (lstrcmpiA(req->cmd, "ping") == 0)
    {
        snprintf(json, jsonCap, "{\"ok\":true,\"pid\":%lu,\"hook_version\":\"%s\"}", (unsigned long)GetCurrentProcessId(), SPYDER_HOOK_VERSION);
    }
    else if (lstrcmpiA(req->cmd, "shutdown") == 0)
    {
        g_stop = TRUE;
        if (shouldStopServer) *shouldStopServer = TRUE;
        snprintf(json, jsonCap, "{\"ok\":true,\"shutdown\":true}");
    }
    else
    {
        snprintf(json, jsonCap, "{\"ok\":false,\"error\":\"unknown command\"}");
    }
}


static void BuildJsonResponse(char* buf, size_t cap, HWND hwnd)
{
    VclNode node;
    BOOL isVcl = VclResolveByHwnd(hwnd, &node);
    
    char cls[512] = "";
    char name[512] = "";
    char caption[512] = "";
    char parentCls[512] = "";
    char parentName[512] = "";
    
    Utf16ToUtf8(node.class_name, cls, sizeof(cls));
    Utf16ToUtf8(node.component_name, name, sizeof(name));
    Utf16ToUtf8(node.caption, caption, sizeof(caption));
    
    // Parent info (placeholder for now, next step will implement real VCL parent)
    if (node.parent_hwnd) {
        WCHAR pcw[256] = L"";
        GetClassNameW(node.parent_hwnd, pcw, 256);
        Utf16ToUtf8(pcw, parentCls, sizeof(parentCls));
    }

    // JSON format:
    // {
    //   "ok": true,
    //   "is_vcl": true/false,
    //   "vcl_self": "0x...",
    //   "vcl_class": "...",
    //   "vcl_name": "...",
    //   "caption": "...",
    //   "hwnd": 123,
    //   "parent_hwnd": 123,
    //   "parent_vcl_name": "...",
    //   "vcl_parent_self": "0x...",
    //   "confidence": 80
    // }

    snprintf(buf, cap, 
        "{\"ok\":true,\"is_vcl\":%s,\"vcl_self\":\"0x%p\",\"vcl_class\":\"%s\",\"vcl_name\":\"%s\",\"caption\":\"%s\",\"hwnd\":%I64u,\"parent_hwnd\":%I64u,\"parent_vcl_name\":\"%s\",\"vcl_parent_self\":\"0x%p\",\"confidence\":%d}",
        isVcl ? "true" : "false",
        node.self,
        cls,
        name[0] ? name : "null",
        caption,
        (unsigned long long)(UINT_PTR)hwnd,
        (unsigned long long)(UINT_PTR)node.parent_hwnd,
        parentCls[0] ? parentCls : "null",
        node.parent_self,
        node.confidence
    );
}


static BOOL ReadExact(HANDLE h, void* buf, DWORD len)
{
    DWORD got = 0, total = 0;
    while (total < len)
    {
        if (!ReadFile(h, (char*)buf + total, len - total, &got, NULL)) return FALSE;
        if (got == 0) return FALSE;
        total += got;
    }
    return TRUE;
}

static BOOL WriteExact(HANDLE h, const void* buf, DWORD len)
{
    DWORD sent = 0, total = 0;
    while (total < len)
    {
        if (!WriteFile(h, (char*)buf + total, len - total, &sent, NULL)) return FALSE;
        if (sent == 0) return FALSE;
        total += sent;
    }
    return TRUE;
}

static void BroadcastHookLoaded(void)
{
    char msgName[128];
    wsprintfA(msgName, "Spyder.HookLoaded.%lu", (unsigned long)GetCurrentProcessId());
    UINT m = RegisterWindowMessageA(msgName);
    if (m != 0)
        PostMessage(HWND_BROADCAST, m, (WPARAM)GetCurrentProcessId(), 0);
}

static void Utf16ToUtf8(const WCHAR* src, char* dst, int dstCap)
{
    int n = WideCharToMultiByte(CP_UTF8, 0, src, -1, dst, dstCap, NULL, NULL);
    if (n == 0) lstrcpyA(dst, "");
}

static const char* ControlTypeToString(VclControlType t)
{
    switch (t)
    {
    case VCL_TYPE_FORM: return "Form";
    case VCL_TYPE_BUTTON: return "Button";
    case VCL_TYPE_EDIT: return "Edit";
    case VCL_TYPE_LABEL: return "Label";
    case VCL_TYPE_PANEL: return "Panel";
    case VCL_TYPE_GRID: return "Grid";
    case VCL_TYPE_COMBOBOX: return "ComboBox";
    case VCL_TYPE_LISTBOX: return "ListBox";
    case VCL_TYPE_CHECKBOX: return "CheckBox";
    case VCL_TYPE_RADIOBUTTON: return "RadioButton";
    case VCL_TYPE_MEMO: return "Memo";
    case VCL_TYPE_TABCONTROL: return "TabControl";
    case VCL_TYPE_TOOLBAR: return "ToolBar";
    case VCL_TYPE_MENU: return "Menu";
    case VCL_TYPE_TREEVIEW: return "TreeView";
    case VCL_TYPE_GROUPBOX: return "GroupBox";
    default: return "Unknown";
    }
}

static BOOL IsPtrReadable(const void* p)
{
    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return FALSE;
    DWORD prot = mbi.Protect & 0xFF;
    if (prot == PAGE_NOACCESS || prot == PAGE_EXECUTE) return FALSE;
    return TRUE;
}

static BOOL IsPtrRX(const void* p)
{
    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery(p, &mbi, sizeof(mbi)) == 0) return FALSE;
    DWORD prot = mbi.Protect & 0xFF;
    return prot == PAGE_EXECUTE || prot == PAGE_EXECUTE_READ || prot == PAGE_EXECUTE_READWRITE || prot == PAGE_EXECUTE_WRITECOPY;
}

static BOOL TryGetDelphiSelfFromWndProc(HWND hwnd, void** outSelf, void** outVmt)
{
    *outSelf = NULL;
    *outVmt = NULL;
#ifdef _WIN64
    ULONG_PTR p = (ULONG_PTR)GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
#else
    ULONG_PTR p = (ULONG_PTR)GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
#endif
    if (!IsPtrRX((void*)p)) return FALSE;
    unsigned char buf[64];
    SIZE_T read = 0;
    __try {
        memcpy(buf, (void*)p, sizeof(buf));
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        return FALSE;
    }
#ifdef _WIN64
    for (int i = 0; i < 48; i++)
    {
        if (buf[i] == 0x48 && buf[i + 1] == 0xB9)
        {
            ULONG_PTR imm = *(ULONG_PTR*)(buf + i + 2);
            void* cand = (void*)imm;
            if (IsPtrReadable(cand) && IsPtrReadable(*(void**)cand))
            {
                *outSelf = cand;
                *outVmt = *(void**)cand;
                return TRUE;
            }
        }
        if (buf[i] == 0x48 && buf[i + 1] == 0xB8 && buf[i + 10] == 0x48 && buf[i + 11] == 0x89 && buf[i + 12] == 0xC1)
        {
            ULONG_PTR imm = *(ULONG_PTR*)(buf + i + 2);
            void* cand = (void*)imm;
            if (IsPtrReadable(cand) && IsPtrReadable(*(void**)cand))
            {
                *outSelf = cand;
                *outVmt = *(void**)cand;
                return TRUE;
            }
        }
    }
#else
    for (int i = 0; i < 32; i++)
    {
        if (buf[i] == 0xB8)
        {
            ULONG_PTR imm = *(ULONG_PTR*)(buf + i + 1);
            void* cand = (void*)imm;
            if (IsPtrReadable(cand) && IsPtrReadable(*(void**)cand))
            {
                *outSelf = cand;
                *outVmt = *(void**)cand;
                return TRUE;
            }
        }
        if (buf[i] == 0x68)
        {
            ULONG_PTR imm = *(ULONG_PTR*)(buf + i + 1);
            void* cand = (void*)imm;
            if (IsPtrReadable(cand) && IsPtrReadable(*(void**)cand))
            {
                *outSelf = cand;
                *outVmt = *(void**)cand;
                return TRUE;
            }
        }
    }
#endif
    return FALSE;
}

static BOOL TryGetVclParent(HWND hwnd, void* self, HWND* outParentHwnd, void** outParentSelf)
{
    *outParentHwnd = NULL;
    *outParentSelf = NULL;
    if (hwnd)
    {
        HWND p = GetAncestor(hwnd, GA_PARENT);
        if (p) *outParentHwnd = p;
    }
    return *outParentHwnd != NULL;
}
