/*
Copyright 2026 Daniyar Sagatov
Licensed under the Apache License 2.0
*/

#include "VclBridge.h"
#include <windows.h>
#include <stdio.h>
#include <synchapi.h>
#include <wchar.h>

// Forward declarations
static BOOL TryGetVclClassName(void* vmt, WCHAR* outBuf, size_t outCch);
static BOOL ParseVclClassNameFromVmt(void* vmt, WCHAR* outBuf, size_t outCch);
static BOOL TryGetComponentName(void* self, WCHAR* outBuf, size_t outCch);
static int TryGetDelphiSelfFromWndProc(HWND hwnd, void** outSelf, void** outVmt);
static BOOL WStartsWithI(const WCHAR* s, const WCHAR* prefix);
static BOOL TryGetVclScreenRect(void* self, RECT* outRc);
static BOOL IsIgnoredClass(const WCHAR* cls);
typedef RECT(__stdcall *SpyderGetControlRectHelperFn)(void* controlSelf);
static SpyderGetControlRectHelperFn g_GetControlRectHelper = NULL;

// These will be defined later
static void* GetVclParent(void* self);
static void* GetVclOwner(void* self);

// --- Logging ---

static volatile LONG g_logInit = 0;
static BOOL g_logEnabled = FALSE;
static HANDLE g_logFile = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION g_logCs;
volatile BOOL g_isCapture = FALSE;
static volatile DWORD g_hitDeadlineTick = 0;

static void EnsureLogInit(void)
{
    if (InterlockedCompareExchange(&g_logInit, 1, 0) != 0) return;
    InitializeCriticalSection(&g_logCs);
    char ev[8] = { 0 };
    if (GetEnvironmentVariableA("SPYDER_VCL_TRACE", ev, (DWORD)sizeof(ev)) == 0) return;
    if (lstrcmpiA(ev, "1") != 0) return;
    g_logEnabled = TRUE;

    char base[MAX_PATH] = { 0 };
    if (GetEnvironmentVariableA("LOCALAPPDATA", base, MAX_PATH) == 0)
        lstrcpynA(base, ".", MAX_PATH);
    char d1[MAX_PATH], d2[MAX_PATH], p[MAX_PATH];
    snprintf(d1, sizeof(d1), "%s\\Spyder", base);
    snprintf(d2, sizeof(d2), "%s\\Spyder\\logs", base);
    CreateDirectoryA(d1, NULL);
    CreateDirectoryA(d2, NULL);
    snprintf(p, sizeof(p), "%s\\Spyder\\logs\\vclhook-%lu.log", base, (unsigned long)GetCurrentProcessId());
    g_logFile = CreateFileA(p, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
}

void Log(const char* fmt, ...)
{
    EnsureLogInit();
    if (!g_logEnabled) return;
    char msg[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(msg, sizeof(msg), fmt, args);
    va_end(args);
    OutputDebugStringA(msg);
    if (g_logFile == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[1200];
    int n = snprintf(line, sizeof(line), "%04u-%02u-%02u %02u:%02u:%02u.%03u %s\r\n",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, msg);
    if (n <= 0) return;
    if (n > (int)sizeof(line)) n = (int)sizeof(line);
    EnterCriticalSection(&g_logCs);
    DWORD wr = 0;
    WriteFile(g_logFile, line, (DWORD)n, &wr, NULL);
    LeaveCriticalSection(&g_logCs);
}

typedef struct _VmtCacheEntry {
    void* vmt;
    WCHAR class_name[256];
} VmtCacheEntry;

static VmtCacheEntry g_VmtCache[512];
static int g_VmtCacheCount = 0;
static CRITICAL_SECTION g_VmtCacheCs;
static volatile LONG g_VmtCacheCsInit = 0;

static void EnsureVmtCacheCs(void)
{
    if (InterlockedCompareExchange(&g_VmtCacheCsInit, 1, 0) == 0)
        InitializeCriticalSection(&g_VmtCacheCs);
}

// --- Helper Declarations ---
static BOOL IsPtrReadable(const void* p)
{
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(p, &mbi, sizeof(mbi))) return FALSE;
    return (mbi.State == MEM_COMMIT) &&
           (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE));
}

static BOOL IsPtrRX(const void* p)
{
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(p, &mbi, sizeof(mbi))) return FALSE;
    DWORD prot = mbi.Protect & 0xFF;
    return (mbi.State == MEM_COMMIT) &&
           (prot == PAGE_EXECUTE || prot == PAGE_EXECUTE_READ || 
            prot == PAGE_EXECUTE_READWRITE || prot == PAGE_EXECUTE_WRITECOPY);
}

// --- Safe Memory Access ---

static BOOL SafeReadPtr(const void* src, void** outValue)
{
    if (!src || !outValue) return FALSE;
    if (!IsPtrReadable(src)) {
        // Log("[SafeReadPtr] Not readable: %p", src);
        return FALSE;
    }
    __try {
        *outValue = *(void**)src;
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) {
        Log("[SafeReadPtr] Exception reading %p", src);
        return FALSE;
    }
}

static BOOL SafeReadInt(const void* src, int* outValue)
{
    if (!src || !outValue) return FALSE;
    if (!IsPtrReadable(src)) return FALSE;
    __try {
        *outValue = *(int*)src;
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) {
        return FALSE;
    }
}

static BOOL SafeReadByte(const void* src, unsigned char* outValue)
{
    if (!src || !outValue) return FALSE;
    if (!IsPtrReadable(src)) return FALSE;
    __try {
        *outValue = *(unsigned char*)src;
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) {
        return FALSE;
    }
}

static BOOL VclIsObjectAlive(void* self)
{
    if (!self) return FALSE;
    if (!IsPtrReadable(self)) return FALSE;
    void* vmt = NULL;
    if (!SafeReadPtr(self, &vmt)) return FALSE;
    if (!IsPtrRX(vmt)) return FALSE;
    WCHAR cls[64] = { 0 };
    if (TryGetVclClassName(vmt, cls, 64))
        return cls[0] == L'T';

    void* m0 = NULL;
    if (!SafeReadPtr(vmt, &m0)) return FALSE;
    if (!m0 || !IsPtrReadable(m0)) return FALSE;
    return TRUE;
}

// --- Class Name Extraction ---

static BOOL TryGetVclClassName(void* vmt, WCHAR* outBuf, size_t outCch)
{
    if (!vmt || !outBuf) return FALSE;
    outBuf[0] = 0;
    EnsureVmtCacheCs();
    int idx = ((int)(UINT_PTR)vmt >> 4) & 511;
    int idx2 = (idx + 257) & 511;

    EnterCriticalSection(&g_VmtCacheCs);
    if (g_VmtCache[idx].vmt == vmt)
    {
        lstrcpynW(outBuf, g_VmtCache[idx].class_name, (int)outCch);
        LeaveCriticalSection(&g_VmtCacheCs);
        return TRUE;
    }
    if (g_VmtCache[idx2].vmt == vmt)
    {
        lstrcpynW(outBuf, g_VmtCache[idx2].class_name, (int)outCch);
        LeaveCriticalSection(&g_VmtCacheCs);
        return TRUE;
    }
    LeaveCriticalSection(&g_VmtCacheCs);

    WCHAR parsed[256] = { 0 };
    if (!ParseVclClassNameFromVmt(vmt, parsed, 256))
        return FALSE;

    EnterCriticalSection(&g_VmtCacheCs);
    if (!g_VmtCache[idx].vmt || g_VmtCache[idx].vmt == vmt)
    {
        if (!g_VmtCache[idx].vmt && g_VmtCacheCount < 512) g_VmtCacheCount++;
        g_VmtCache[idx].vmt = vmt;
        lstrcpynW(g_VmtCache[idx].class_name, parsed, 256);
    }
    else if (!g_VmtCache[idx2].vmt || g_VmtCache[idx2].vmt == vmt)
    {
        if (!g_VmtCache[idx2].vmt && g_VmtCacheCount < 512) g_VmtCacheCount++;
        g_VmtCache[idx2].vmt = vmt;
        lstrcpynW(g_VmtCache[idx2].class_name, parsed, 256);
    }
    else
    {
        g_VmtCache[idx].vmt = vmt;
        lstrcpynW(g_VmtCache[idx].class_name, parsed, 256);
    }
    LeaveCriticalSection(&g_VmtCacheCs);

    lstrcpynW(outBuf, parsed, (int)outCch);
    return TRUE;
}

static BOOL ParseVclClassNameFromVmt(void* vmt, WCHAR* outBuf, size_t outCch)
{
    if (!vmt || !outBuf) return FALSE;
    outBuf[0] = 0;
    __try
    {
        unsigned char* base = (unsigned char*)vmt;
        for (int off = -4; off >= -128; off -= 4)
        {
            void* ptrToName = NULL;
            if (!SafeReadPtr(base + off, &ptrToName)) continue;
            if (!ptrToName || !IsPtrReadable(ptrToName)) continue;

            unsigned char len = 0;
            if (!SafeReadByte(ptrToName, &len)) continue;
            if (len < 2 || len > 64) continue;

            char tmp[128];
            BOOL ok = TRUE;
            __try {
                for (int i = 0; i < len; i++) {
                    char c = ((char*)ptrToName)[1 + i];
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) {
                        ok = FALSE; break;
                    }
                    tmp[i] = c;
                }
            }
            __except(EXCEPTION_EXECUTE_HANDLER) { continue; }

            if (!ok) continue;
            tmp[len] = 0;
            if (tmp[0] != 'T') continue;

            MultiByteToWideChar(CP_ACP, 0, tmp, -1, outBuf, (int)outCch);
            return TRUE;
        }
    }
    __except(EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
    return FALSE;
}

// --- Profile & Tree Traversal ---

typedef struct {
    int offName;
    int offOwner;
    int offParent;       
    int offControls;     
    int offControlCount; 
    int offComponents;   
    int offComponentCount; 
    int offVisible;
    BOOL detected;
    int offLeft;
    int offTop;
    int offWidth;
    int offHeight;
    int offControlAtPos;
    int controlAtPosKind;
    BOOL geom_detected;
} VclProfile;

static VclProfile g_Profile = { 0 };
static BOOL g_ProfileDefaultsInit = FALSE;

static void EnsureProfileDefaults(void)
{
    if (g_ProfileDefaultsInit) return;
    g_Profile.offControls = -1;
    g_Profile.offControlCount = -1;
    g_Profile.offVisible = -1;
    g_Profile.offControlAtPos = -1;
    g_Profile.controlAtPosKind = -1;
    g_ProfileDefaultsInit = TRUE;
}

void** VclGetChildControls(void* self, int* count);
static BOOL IsIgnoredClass(const WCHAR* cls);
static BOOL IsCodePtr(void* p);

static BOOL IsParentByControls(void* possibleParent, void* child)
{
    if (!possibleParent || !child) return FALSE;

    int count = 0;
    void** children = VclGetChildControls(possibleParent, &count);
    if (!children || count <= 0) {
        if (children) LocalFree(children);
        return FALSE;
    }

    int limit = count > 2048 ? 2048 : count;
    BOOL ok = FALSE;
    for (int i = 0; i < limit; i++)
    {
        if (children[i] == child) { ok = TRUE; break; }
    }

    LocalFree(children);
    return ok;
}

static void DetectGeometryFromHwnd(void* self, HWND hwnd)
{
    if (g_Profile.geom_detected) return;
    if (!self || !hwnd) return;

    HWND parent = GetParent(hwnd);
    if (!parent) return;

    RECT wr;
    if (!GetWindowRect(hwnd, &wr)) return;

    POINT origin = { 0, 0 };
    if (!ClientToScreen(parent, &origin)) return;

    int expLeft = wr.left - origin.x;
    int expTop = wr.top - origin.y;
    int expW = wr.right - wr.left;
    int expH = wr.bottom - wr.top;

    if (expW <= 0 || expH <= 0) return;
    if (expW > 20000 || expH > 20000) return;

    unsigned char* base = (unsigned char*)self;
    for (int off = 0; off < 512; off += 4)
    {
        int l = 0, t = 0, w = 0, h = 0;
        if (!SafeReadInt(base + off, &l)) continue;
        if (l < expLeft - 2 || l > expLeft + 2) continue;
        if (!SafeReadInt(base + off + 4, &t)) continue;
        if (t < expTop - 2 || t > expTop + 2) continue;
        if (!SafeReadInt(base + off + 8, &w)) continue;
        if (!SafeReadInt(base + off + 12, &h)) continue;
        if (w < expW - 2 || w > expW + 2) continue;
        if (h < expH - 2 || h > expH + 2) continue;

        g_Profile.offLeft = off;
        g_Profile.offTop = off + 4;
        g_Profile.offWidth = off + 8;
        g_Profile.offHeight = off + 12;
        g_Profile.geom_detected = TRUE;
        Log("[DetectProfile] Found geometry offsets L=%d T=%d W=%d H=%d", g_Profile.offLeft, g_Profile.offTop, g_Profile.offWidth, g_Profile.offHeight);
        return;
    }
}

static void DetectProfile(void* self)
{
    EnsureProfileDefaults();
    if (g_Profile.detected) return;
    
    unsigned char* base = (unsigned char*)self;
    int ptrSize = sizeof(void*);
    
    // 1. Name offset
    for (int off = ptrSize; off < 512; off += ptrSize)
    {
        void* ptr = NULL;
        if (!SafeReadPtr(base + off, &ptr)) continue;
        if (!ptr || !IsPtrReadable(ptr)) continue;
        
        int strLen = 0;
        if (!SafeReadInt((char*)ptr - 4, &strLen)) continue;
        
        if (strLen > 0 && strLen < 64)
        {
            BOOL ok = TRUE;
            WCHAR tmp[128];
            __try {
                for (int i = 0; i < strLen; i++) {
                    WCHAR c = 0;
                    if (!SafeReadByte((unsigned char*)ptr + i * sizeof(WCHAR), (unsigned char*)&c)) { ok = FALSE; break; }
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) {
                        ok = FALSE; break;
                    }
                    tmp[i] = c;
                }
            } __except(EXCEPTION_EXECUTE_HANDLER) { ok = FALSE; }
            
            if (ok) {
                if (g_Profile.offName == 0) {
                    g_Profile.offName = off;
                    Log("[DetectProfile] Found Name offset: %d", off);
                }
                break;
            }
        }
    }
    
    // 2. Parent offset
    int ownerOffset = 0;
    int parentOffset = 0;
    
    for (int off = ptrSize; off < 512; off += ptrSize)
    {
        if (off == g_Profile.offName) continue; 
        
        void* ptr = NULL;
        if (!SafeReadPtr(base + off, &ptr)) continue;
        if (!ptr || !IsPtrReadable(ptr)) continue;
        
        // Check if it's part of a TMethod (Event Handler)
        void* prevPtr = NULL;
        if (SafeReadPtr(base + off - ptrSize, &prevPtr))
        {
            if (IsCodePtr(prevPtr)) continue; // Skip event handlers
        }
        
        void* vmt = NULL;
        if (!SafeReadPtr(ptr, &vmt)) continue;
        if (!IsPtrRX(vmt)) continue;
        
        WCHAR cls[128];
        if (TryGetVclClassName(vmt, cls, 128)) {
            if (IsIgnoredClass(cls)) continue;
            
            if (ownerOffset == 0) ownerOffset = off;
            if (IsParentByControls(ptr, self)) {
                parentOffset = off;
                break;
            }
        }
    }
    
    if (ownerOffset > 0) {
        g_Profile.offOwner = ownerOffset;
        Log("[DetectProfile] Found Owner offset: %d", ownerOffset);
    }

    if (parentOffset > 0) {
        g_Profile.offParent = parentOffset;
        Log("[DetectProfile] Found Parent offset: %d", parentOffset);
    }
    
    g_Profile.detected = TRUE;
}

static void DetectControlsOffsets(void* self)
{
    EnsureProfileDefaults();
    if (!self) return;
    if (g_Profile.offControls > 0 && g_Profile.offControlCount > 0) return;

    unsigned char* base = (unsigned char*)self;
    int ptrSize = sizeof(void*);

    for (int offList = ptrSize; offList <= 512; offList += ptrSize)
    {
        if (offList <= 0) continue;

        void* listObj = NULL;
        if (!SafeReadPtr(base + offList, &listObj)) continue;
        if (!listObj || !IsPtrReadable(listObj)) continue;

        void* listVmt = NULL;
        if (!SafeReadPtr(listObj, &listVmt)) continue;
        if (!IsPtrRX(listVmt)) continue;

        void* fList = NULL;
        int fCount = 0;
        if (!SafeReadPtr((unsigned char*)listObj + ptrSize, &fList)) continue;
        if (!SafeReadInt((unsigned char*)listObj + ptrSize * 2, &fCount)) continue;

        if (fCount <= 0 || fCount > 256) continue;
        if (!fList || !IsPtrReadable(fList)) continue;

        int valid = 0;
        int want = fCount < 16 ? fCount : 16;
        for (int i = 0; i < want; i++)
        {
            void* ch = NULL;
            if (!SafeReadPtr((unsigned char*)fList + (size_t)i * (size_t)ptrSize, &ch)) break;
            if (!ch || !IsPtrReadable(ch)) continue;

            void* vmt = NULL;
            if (!SafeReadPtr(ch, &vmt)) continue;
            if (!IsPtrRX(vmt)) continue;

            WCHAR cls[128] = { 0 };
            if (!TryGetVclClassName(vmt, cls, 128)) continue;
            if (!cls[0]) continue;
            if (IsIgnoredClass(cls)) continue;
            valid++;
        }

        int minValid = fCount < 2 ? fCount : 2;
        if (valid < minValid) continue;

        for (int offCount = 4; offCount <= 512; offCount += 4)
        {
            if (offCount <= 0) continue;
            if (offCount == offList) continue;

            int count = 0;
            if (!SafeReadInt(base + offCount, &count)) continue;
            if (count != fCount) continue;

            g_Profile.offControlCount = offCount;
            g_Profile.offControls = offList;

            printf("[DetectProfile] Controls offsets fixed: ControlCount=%d Controls=%d count=%d valid=%d\n",
                g_Profile.offControlCount, g_Profile.offControls, fCount, valid);
            Log("[DetectProfile] Controls offsets fixed: ControlCount=%d Controls=%d count=%d valid=%d",
                g_Profile.offControlCount, g_Profile.offControls, fCount, valid);
            return;
        }
    }
}

static void DetectVisibleOffset(void* self)
{
    EnsureProfileDefaults();
    if (!self) return;
    if (g_Profile.offVisible > 0) return;

    void* sample[64];
    int sampleCount = 0;
    sample[sampleCount++] = self;

    int childCount = 0;
    void** children = VclGetChildControls(self, &childCount);
    if (children)
    {
        int limit = childCount > 63 ? 63 : childCount;
        for (int i = 0; i < limit; i++)
        {
            if (!children[i] || !IsPtrReadable(children[i])) continue;
            sample[sampleCount++] = children[i];
            if (sampleCount >= 64) break;
        }
        LocalFree(children);
    }

    if (sampleCount < 2) return;

    int bestOff = 0;
    int bestBalance = 0;

    for (int off = 0; off < 512; off++)
    {
        int c0 = 0, c1 = 0, cOther = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            unsigned char b = 0xFF;
            if (!SafeReadByte((unsigned char*)sample[i] + off, &b)) { cOther++; continue; }
            if (b == 0) c0++;
            else if (b == 1) c1++;
            else cOther++;
        }

        if (cOther != 0) continue;
        if (c0 == 0 || c1 == 0) continue;

        int balance = c0 < c1 ? c0 : c1;
        if (balance > bestBalance)
        {
            bestBalance = balance;
            bestOff = off;
        }
    }

    if (bestOff > 0)
    {
        g_Profile.offVisible = bestOff;
        Log("[DetectProfile] Found Visible offset: %d", g_Profile.offVisible);
    }
}

static BOOL TryReadVisible(void* self, BOOL* outVisible)
{
    if (!outVisible) return FALSE;
    if (!self) return FALSE;
    if (g_Profile.offVisible <= 0) return FALSE;

    unsigned char b = 0;
    if (!SafeReadByte((unsigned char*)self + g_Profile.offVisible, &b)) return FALSE;
    *outVisible = (b != 0);
    return TRUE;
}

static void** GetDirectChildrenForHitTest(void* self, int* count)
{
    if (count) *count = 0;
    if (!self || !count) return NULL;
    if (!IsPtrReadable(self)) return NULL;

    __try
    {
        EnsureProfileDefaults();
        if (!g_Profile.detected) DetectProfile(self);
        if (g_Profile.offControlCount < 0 || g_Profile.offControls < 0)
            DetectControlsOffsets(self);

        if (g_Profile.offControlCount <= 0 || g_Profile.offControls <= 0)
        {
            Log("[hit_test] controls offsets not ready");
            *count = 0;
            return NULL;
        }

        int c = 0;
        if (!SafeReadInt((unsigned char*)self + g_Profile.offControlCount, &c))
        {
            *count = 0;
            return NULL;
        }

        void* listObj = NULL;
        if (!SafeReadPtr((unsigned char*)self + g_Profile.offControls, &listObj))
        {
            *count = 0;
            return NULL;
        }

        if (c <= 0 || c > 256 || !listObj || !IsPtrReadable(listObj))
        {
            Log("[hit_test] invalid children: count=%d listObj=%p", c, listObj);
            *count = 0;
            return NULL;
        }

        int ptrSize = sizeof(void*);
        void* fList = NULL;
        int fCount = 0;
        if (!SafeReadPtr((unsigned char*)listObj + ptrSize, &fList)) { *count = 0; return NULL; }
        if (!SafeReadInt((unsigned char*)listObj + ptrSize * 2, &fCount)) { *count = 0; return NULL; }
        if (!fList || !IsPtrReadable(fList)) { *count = 0; return NULL; }
        if (fCount <= 0 || fCount > 256) { *count = 0; return NULL; }

        int take = c < fCount ? c : fCount;
        void** result = (void**)LocalAlloc(LPTR, (size_t)take * sizeof(void*));
        if (!result) { *count = 0; return NULL; }

        int written = 0;
        for (int i = 0; i < take; i++)
        {
            void* ch = NULL;
            if (!SafeReadPtr((unsigned char*)fList + (size_t)i * (size_t)ptrSize, &ch)) continue;
            if (!ch || !IsPtrReadable(ch)) continue;
            result[written++] = ch;
        }

        *count = written;
        Log("[hit_test] direct_children self=%p count=%d", self, written);
        return result;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *count = 0;
        return NULL;
    }
}

static BOOL TryGetChildRectFromParentRect(void* self, RECT parentRect, RECT* outRc)
{
    if (!self || !outRc) return FALSE;
    int l = 0, t = 0, w = 0, h = 0;
    if (!SafeReadInt((unsigned char*)self + g_Profile.offLeft, &l)) return FALSE;
    if (!SafeReadInt((unsigned char*)self + g_Profile.offTop, &t)) return FALSE;
    if (!SafeReadInt((unsigned char*)self + g_Profile.offWidth, &w)) return FALSE;
    if (!SafeReadInt((unsigned char*)self + g_Profile.offHeight, &h)) return FALSE;
    if (w <= 0 || h <= 0) return FALSE;
    if (w > 20000 || h > 20000) return FALSE;

    outRc->left = parentRect.left + l;
    outRc->top = parentRect.top + t;
    outRc->right = outRc->left + w;
    outRc->bottom = outRc->top + h;
    return TRUE;
}

static void* FindControlRecursive(void* parentSelf, POINT screenPt, RECT parentRect, RECT* outHitRect, int depth, int* visited)
{
    if (!parentSelf || !outHitRect || !visited) return NULL;
    if (depth > 64) return parentSelf;
    if (*visited > 5000) return parentSelf;
    if (g_hitDeadlineTick != 0 && GetTickCount() > g_hitDeadlineTick) return parentSelf;

    int count = 0;
    void** children = GetDirectChildrenForHitTest(parentSelf, &count);
    if (children && count > 0)
    {
        int limit = count > 256 ? 256 : count;
        for (int i = limit - 1; i >= 0; --i)
        {
            (*visited)++;
            if (*visited > 5000) break;
            if (g_hitDeadlineTick != 0 && GetTickCount() > g_hitDeadlineTick) break;

            void* ch = children[i];
            if (!ch || !IsPtrReadable(ch)) continue;
            if (ch == parentSelf) continue;

            BOOL vis = TRUE;
            TryReadVisible(ch, &vis);
            if (!vis) continue;

            RECT rc = { 0 };
            if (!TryGetChildRectFromParentRect(ch, parentRect, &rc)) continue;

            WCHAR cls[128] = { 0 };
            WCHAR name[128] = { 0 };
            void* vmt = NULL;
            if (SafeReadPtr(ch, &vmt)) TryGetVclClassName(vmt, cls, 128);
            TryGetComponentName(ch, name, 128);
            Log("[hit_test] depth=%d child=%S name=%S rect=(%ld,%ld,%ld,%ld)", depth, cls, name, rc.left, rc.top, rc.right, rc.bottom);

            BOOL ignoredClass = (cls[0] != 0) ? IsIgnoredClass(cls) : FALSE;
            if (ignoredClass) continue;

            if (screenPt.x >= rc.left && screenPt.x <= rc.right &&
                screenPt.y >= rc.top && screenPt.y <= rc.bottom)
            {
                RECT deeperRect = rc;
                void* deeper = FindControlRecursive(ch, screenPt, rc, &deeperRect, depth + 1, visited);
                if (children) LocalFree(children);
                if (deeper)
                {
                    *outHitRect = deeperRect;
                    return deeper;
                }
                if (!ignoredClass)
                {
                    *outHitRect = rc;
                    return ch;
                }
                continue;
            }
        }
    }

    if (children) LocalFree(children);
    *outHitRect = parentRect;
    return parentSelf;
}

static BOOL IsValidVclObjectForHitTest(void* self)
{
    if (!self || !IsPtrReadable(self)) return FALSE;
    void* vmt = NULL;
    if (!SafeReadPtr(self, &vmt) || !IsPtrRX(vmt)) return FALSE;
    WCHAR cls[64] = { 0 };
    if (!TryGetVclClassName(vmt, cls, 64)) return FALSE;
    return cls[0] == L'T';
}

#ifdef _M_IX86
static void* CallControlAtPos_x86(void* fn, void* self, POINT pt, BOOL allowDisabled, BOOL allowWinControls, BOOL allLevels)
{
    void* res = NULL;
    int ad = allowDisabled ? 1 : 0;
    int aw = allowWinControls ? 1 : 0;
    int av = allLevels ? 1 : 0;
    __asm {
        push ebx
        mov ecx, self
        xor edx, edx
        mov ebx, fn
        mov eax, av
        push eax
        mov eax, aw
        push eax
        mov eax, ad
        push eax
        mov eax, dword ptr [pt+4]
        push eax
        mov eax, dword ptr [pt]
        push eax
        call ebx
        mov res, eax
        add esp, 20
        pop ebx
    }
    return res;
}
#endif

static BOOL IsSameOrDescendantVcl(void* root, void* candidate)
{
    if (!root || !candidate) return FALSE;
    if (root == candidate) return TRUE;
    if (!IsPtrReadable(candidate)) return FALSE;

    int visited = 0;
    void* stack[64];
    int sp = 0;
    stack[sp++] = root;

    while (sp > 0 && visited < 20000)
    {
        void* cur = stack[--sp];
        if (!cur || !IsPtrReadable(cur)) continue;

        int count = 0;
        void** children = VclGetChildControls(cur, &count);
        if (!children) continue;

        int limit = count > 2048 ? 2048 : count;
        for (int i = 0; i < limit; i++)
        {
            visited++;
            void* ch = children[i];
            if (!ch) continue;
            if (ch == candidate)
            {
                LocalFree(children);
                return TRUE;
            }
            if (sp < 64) stack[sp++] = ch;
            if (visited >= 20000) break;
        }
        LocalFree(children);
    }

    return FALSE;
}

static BOOL IsWinControlForHitTest(void* self)
{
    if (!self || !IsPtrReadable(self)) return FALSE;
    void* vmt = NULL;
    WCHAR cls[128] = { 0 };
    if (!SafeReadPtr(self, &vmt) || !IsPtrRX(vmt)) return FALSE;
    if (!TryGetVclClassName(vmt, cls, 128)) return FALSE;

    if (lstrcmpiW(cls, L"TLabel") == 0) return FALSE;
    if (lstrcmpiW(cls, L"TShape") == 0) return FALSE;
    if (lstrcmpiW(cls, L"TPaintBox") == 0) return FALSE;
    if (lstrcmpiW(cls, L"TImage") == 0) return FALSE;
    return TRUE;
}

typedef void* (__stdcall *SpyderControlAtPosHelperFn)(void* rootSelf, int x, int y);
typedef const char* (__stdcall *SpyderGetCaptionHelperFn)(void* controlSelf);
typedef int (__stdcall *SpyderGetTabOrderHelperFn)(void* controlSelf);
typedef BOOL (__stdcall *SpyderIsEnabledHelperFn)(void* controlSelf);
typedef void* (__stdcall *SpyderGetParentHelperFn)(void* controlSelf);
typedef int (__stdcall *SpyderGetControlTypeHelperFn)(void* controlSelf);
static SpyderControlAtPosHelperFn g_ControlAtPosHelper = NULL;
static SpyderGetCaptionHelperFn g_GetCaptionHelper = NULL;
static SpyderGetTabOrderHelperFn g_GetTabOrderHelper = NULL;
static SpyderIsEnabledHelperFn g_IsEnabledHelper = NULL;
static SpyderGetParentHelperFn g_GetParentHelper = NULL;
static SpyderGetControlTypeHelperFn g_GetControlTypeHelper = NULL;
static BOOL g_ControlAtPosHelperResolved = FALSE;

// Hit-test context for VCL bounds calculation
static void* g_HitCtxRootSelf = NULL;
static HWND g_HitCtxHwnd = NULL;

static BOOL TryGetVclScreenRect(void* self, RECT* outRc)
{
    if (!self || !outRc) return FALSE;
    if (!g_Profile.geom_detected) return FALSE;

    if (self == g_HitCtxRootSelf && g_HitCtxHwnd)
    {
        RECT wr;
        if (GetWindowRect(g_HitCtxHwnd, &wr))
        {
            *outRc = wr;
            return TRUE;
        }
        return FALSE;
    }

    int l = 0, t = 0, w = 0, h = 0;
    if (!SafeReadInt((unsigned char*)self + g_Profile.offLeft, &l)) return FALSE;
    if (!SafeReadInt((unsigned char*)self + g_Profile.offTop, &t)) return FALSE;
    if (!SafeReadInt((unsigned char*)self + g_Profile.offWidth, &w)) return FALSE;
    if (!SafeReadInt((unsigned char*)self + g_Profile.offHeight, &h)) return FALSE;
    if (w <= 0 || h <= 0) return FALSE;

    void* parent = GetVclParent(self);
    RECT pr = { 0 };
    if (!TryGetVclScreenRect(parent, &pr)) return FALSE;

    outRc->left = pr.left + l;
    outRc->top = pr.top + t;
    outRc->right = outRc->left + w;
    outRc->bottom = outRc->top + h;
    return TRUE;
}

static BOOL TryResolveControlAtPosHelper(void)
{
    if (g_ControlAtPosHelperResolved) return g_ControlAtPosHelper != NULL;
    g_ControlAtPosHelperResolved = TRUE;

    HMODULE hExe = GetModuleHandleW(NULL);
    if (hExe)
    {
        FARPROC p = GetProcAddress(hExe, "Spyder_ControlAtPos");
        FARPROC pr = GetProcAddress(hExe, "Spyder_GetControlRect");
        if (p)
        {
            g_ControlAtPosHelper = (SpyderControlAtPosHelperFn)p;
            g_GetControlRectHelper = (SpyderGetControlRectHelperFn)pr;
            g_GetCaptionHelper = (SpyderGetCaptionHelperFn)GetProcAddress(hExe, "Spyder_GetCaption");
            g_GetTabOrderHelper = (SpyderGetTabOrderHelperFn)GetProcAddress(hExe, "Spyder_GetTabOrder");
            g_IsEnabledHelper = (SpyderIsEnabledHelperFn)GetProcAddress(hExe, "Spyder_IsEnabled");
            g_GetParentHelper = (SpyderGetParentHelperFn)GetProcAddress(hExe, "Spyder_GetParent");
            g_GetControlTypeHelper = (SpyderGetControlTypeHelperFn)GetProcAddress(hExe, "Spyder_GetControlType");
            return TRUE;
        }
    }

    WCHAR selfPath[MAX_PATH] = { 0 };
    HMODULE hSelf = NULL;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCWSTR)&TryResolveControlAtPosHelper, &hSelf) && hSelf)
    {
        if (GetModuleFileNameW(hSelf, selfPath, MAX_PATH) > 0)
        {
            WCHAR* slash = wcsrchr(selfPath, L'\\');
            if (slash) *(slash + 1) = 0;
            WCHAR candidate[MAX_PATH] = { 0 };
#ifdef _WIN64
            wsprintfW(candidate, L"%sSpyderVclHelper64.dll", selfPath);
#else
            wsprintfW(candidate, L"%sSpyderVclHelper32.dll", selfPath);
#endif
            HMODULE hHelper = LoadLibraryW(candidate);
            if (hHelper)
            {
                FARPROC p = GetProcAddress(hHelper, "Spyder_ControlAtPos");
                FARPROC pr = GetProcAddress(hHelper, "Spyder_GetControlRect");
                if (p)
                {
                    g_ControlAtPosHelper = (SpyderControlAtPosHelperFn)p;
                    g_GetControlRectHelper = (SpyderGetControlRectHelperFn)pr;
                    g_GetCaptionHelper = (SpyderGetCaptionHelperFn)GetProcAddress(hHelper, "Spyder_GetCaption");
                    g_GetTabOrderHelper = (SpyderGetTabOrderHelperFn)GetProcAddress(hHelper, "Spyder_GetTabOrder");
                    g_IsEnabledHelper = (SpyderIsEnabledHelperFn)GetProcAddress(hHelper, "Spyder_IsEnabled");
                    g_GetParentHelper = (SpyderGetParentHelperFn)GetProcAddress(hHelper, "Spyder_GetParent");
                    g_GetControlTypeHelper = (SpyderGetControlTypeHelperFn)GetProcAddress(hHelper, "Spyder_GetControlType");
                    return TRUE;
                }
            }
        }
    }

    return FALSE;
}

static BOOL VclFillNodeBasic(void* self, VclNode* outNode)
{
    if (!self || !outNode) return FALSE;
    ZeroMemory(outNode, sizeof(VclNode));
    if (!IsPtrReadable(self)) return FALSE;

    void* vmt = NULL;
    if (!SafeReadPtr(self, &vmt)) return FALSE;
    if (!IsPtrRX(vmt)) return FALSE;

    outNode->self = self;
    outNode->vmt = vmt;
    outNode->is_vcl = TRUE;

    if (TryGetVclClassName(vmt, outNode->class_name, 256))
        outNode->confidence += 20;
    if (TryGetComponentName(self, outNode->component_name, 256))
        outNode->confidence += 20;
    return TRUE;
}

BOOL VclHitTest(HWND parentHwnd, void* parentSelf, int screenX, int screenY, VclNode* outNode)
{
    if (!outNode) return FALSE;
    ZeroMemory(outNode, sizeof(VclNode));
    if (!parentHwnd || !parentSelf) return FALSE;
    if (!IsWindow(parentHwnd)) return FALSE;
    DWORD wndPid = 0;
    GetWindowThreadProcessId(parentHwnd, &wndPid);
    if (wndPid != GetCurrentProcessId()) return FALSE;
    if (!IsPtrReadable(parentSelf)) return FALSE;

    void* winRoot = parentSelf;

    if (!g_Profile.geom_detected) DetectGeometryFromHwnd(winRoot, parentHwnd);
    g_HitCtxRootSelf = winRoot;
    g_HitCtxHwnd = parentHwnd;

    WCHAR rootCls[128] = { 0 };
    WCHAR rootName[128] = { 0 };
    void* rootVmt = NULL;
    if (SafeReadPtr(winRoot, &rootVmt)) TryGetVclClassName(rootVmt, rootCls, 128);
    TryGetComponentName(winRoot, rootName, 128);
    Log("[hit_test] root class=%S name=%S hwnd=0x%llX", rootCls, rootName, (unsigned long long)(UINT_PTR)parentHwnd);

    POINT pt = { screenX, screenY };
    if (!ScreenToClient(parentHwnd, &pt)) return FALSE;
    Log("[hit_test] clientPt=%ld,%ld", pt.x, pt.y);
    RECT clientRc = { 0 };
    if (!GetClientRect(parentHwnd, &clientRc)) return FALSE;
    BOOL ptInsideClient = !(pt.x < clientRc.left || pt.y < clientRc.top || pt.x >= clientRc.right || pt.y >= clientRc.bottom);
    if (!ptInsideClient)
        Log("[hit_test] point outside client, will use deep screen fallback");

    void* res = winRoot;
    if (ptInsideClient && IsWinControlForHitTest(winRoot) && TryResolveControlAtPosHelper() && g_ControlAtPosHelper)
    {
        __try
        {
            res = g_ControlAtPosHelper(winRoot, pt.x, pt.y);
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            res = NULL;
        }
    }
    if (!res) res = winRoot;
    if (!IsPtrReadable(res))
        res = winRoot;
    if (!IsValidVclObjectForHitTest(res))
        res = winRoot;
    if (!IsPtrReadable(res))
        return FALSE;

    Log("[hit_test] helper result ptr=%p", res);

    void* resVmt = NULL;
    WCHAR resCls[128] = { 0 };
    WCHAR resName[128] = { 0 };
    if (SafeReadPtr(res, &resVmt)) TryGetVclClassName(resVmt, resCls, 128);
    TryGetComponentName(res, resName, 128);
    if (resCls[0] && IsIgnoredClass(resCls))
    {
        Log("[hit_test] helper returned ignored class=%S, fallback to root/deep", resCls);
        res = winRoot;
        resVmt = NULL;
        ZeroMemory(resCls, sizeof(resCls));
        ZeroMemory(resName, sizeof(resName));
        if (SafeReadPtr(res, &resVmt)) TryGetVclClassName(resVmt, resCls, 128);
        TryGetComponentName(res, resName, 128);
    }
    Log("[hit_test] result class=%S name=%S", resCls, resName);

    BOOL refineDeep = FALSE;
    if (!ptInsideClient) refineDeep = TRUE;
    if (res == winRoot) refineDeep = TRUE;
    if (WStartsWithI(resCls, L"TPanel")) refineDeep = TRUE;
    if (WStartsWithI(resCls, L"TExPanel")) refineDeep = TRUE;
    if (WStartsWithI(resCls, L"TToolBar") || WStartsWithI(resCls, L"TExEdit")) refineDeep = TRUE;
    if (WStartsWithI(resCls, L"TEdit")) refineDeep = TRUE;
    if (!g_isCapture) refineDeep = FALSE;
    if (refineDeep)
    {
        RECT rootRect = { 0 };
        if (GetWindowRect(parentHwnd, &rootRect))
        {
            g_hitDeadlineTick = GetTickCount() + (g_isCapture ? 180 : 70);
            RECT deepRect = rootRect;
            int visited = 0;
            POINT spt = { screenX, screenY };
            void* deep = FindControlRecursive(winRoot, spt, rootRect, &deepRect, 0, &visited);
            g_hitDeadlineTick = 0;
            if (deep && deep != res && IsValidVclObjectForHitTest(deep))
            {
                void* deepVmt = NULL;
                WCHAR deepCls[128] = { 0 };
                if (SafeReadPtr(deep, &deepVmt)) TryGetVclClassName(deepVmt, deepCls, 128);
                if (deepCls[0] && !IsIgnoredClass(deepCls))
                {
                    res = deep;
                    resVmt = NULL;
                    ZeroMemory(resCls, sizeof(resCls));
                    ZeroMemory(resName, sizeof(resName));
                    if (SafeReadPtr(res, &resVmt)) TryGetVclClassName(resVmt, resCls, 128);
                    TryGetComponentName(res, resName, 128);
                    Log("[hit_test] refined class=%S name=%S", resCls, resName);
                }
            }
        }
    }

    if (!VclFillNodeBasic(res, outNode))
    {
        if (res != winRoot && IsPtrReadable(winRoot) && IsValidVclObjectForHitTest(winRoot) && VclFillNodeBasic(winRoot, outNode))
        {
            res = winRoot;
            if (SafeReadPtr(res, &resVmt)) TryGetVclClassName(resVmt, resCls, 128);
            TryGetComponentName(res, resName, 128);
            Log("[hit_test] result class=%S name=%S", resCls, resName);
        }
        else
        {
            return FALSE;
        }
    }

    RECT rc = { 0 };
    BOOL rectOk = FALSE;
    if (!IsPtrReadable(res)) return FALSE;
    if (g_GetControlRectHelper)
    {
        __try
        {
            rc = g_GetControlRectHelper(res);
            int w = rc.right - rc.left;
            int h = rc.bottom - rc.top;
            if (w > 0 && h > 0 &&
                w < 50000 && h < 50000 &&
                rc.left > -200000 && rc.left < 200000 &&
                rc.top > -200000 && rc.top < 200000 &&
                rc.right > -200000 && rc.right < 200000 &&
                rc.bottom > -200000 && rc.bottom < 200000)
                rectOk = TRUE;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            rectOk = FALSE;
        }
    }
    if (!rectOk && TryGetVclScreenRect(res, &rc))
        rectOk = TRUE;
    if (rectOk) outNode->bounds = rc;

    return TRUE;
}

// --- Component Name Extraction ---

static BOOL TryGetComponentName(void* self, WCHAR* outBuf, size_t outCch)
{
    if (!self || !outBuf) return FALSE;
    if (!IsPtrReadable(self)) return FALSE;
    outBuf[0] = 0;
    
    if (!g_Profile.detected) DetectProfile(self);
    
    unsigned char* base = (unsigned char*)self;
    
    if (g_Profile.offName > 0)
    {
        void* ptr = NULL;
        if (SafeReadPtr(base + g_Profile.offName, &ptr)) {
            if (ptr && IsPtrReadable(ptr)) {
                int strLen = 0;
                if (SafeReadInt((char*)ptr - 4, &strLen)) {
                    if (strLen > 0 && strLen < 256) {
                        WCHAR tmp[256];
                        BOOL ok = TRUE;
                        __try {
                            for (int i = 0; i < strLen; i++) {
                                WCHAR c = 0;
                                if (!SafeReadByte((unsigned char*)ptr + i * sizeof(WCHAR), (unsigned char*)&c)) { ok = FALSE; break; }
                                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) {
                                    ok = FALSE; break;
                                }
                                tmp[i] = c;
                            }
                        } __except(EXCEPTION_EXECUTE_HANDLER) { ok = FALSE; }
                        
                        if (ok) {
                            tmp[strLen] = 0;
                            lstrcpynW(outBuf, tmp, (int)outCch);
                            return TRUE;
                        }
                    }
                }
            }
        }
    }
    
    // Fallback scan
    int ptrSize = sizeof(void*);
    
    for (int off = ptrSize; off < 1024; off += ptrSize)
    {
        void* ptr = NULL;
        if (!SafeReadPtr(base + off, &ptr)) continue;
        
        if (!ptr || !IsPtrReadable(ptr)) continue;
        
        int strLen = 0;
        if (!SafeReadInt((char*)ptr - 4, &strLen)) continue;
        
        if (strLen > 0 && strLen < 64)
        {
            BOOL ok = TRUE;
            WCHAR tmp[128];
            __try {
                for (int i = 0; i < strLen; i++) {
                    WCHAR c = 0;
                    if (!SafeReadByte((unsigned char*)ptr + i * sizeof(WCHAR), (unsigned char*)&c)) { ok = FALSE; break; }
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) {
                        ok = FALSE; break;
                    }
                    tmp[i] = c;
                }
            } __except(EXCEPTION_EXECUTE_HANDLER) { ok = FALSE; }
            
            if (ok) {
                tmp[strLen] = 0;
                lstrcpynW(outBuf, tmp, (int)outCch);
                return TRUE;
            }
        }
    }

    void* vmt = NULL;
    WCHAR cls[64] = { 0 };
    if (SafeReadPtr(self, &vmt) && TryGetVclClassName(vmt, cls, 64))
    {
        if (lstrcmpiW(cls, L"TApplication") == 0)
        {
            lstrcpynW(outBuf, L"Application", (int)outCch);
            return TRUE;
        }
    }
    return FALSE;
}

// Old GetVclParent removed

static void* GetVclOwner(void* self)
{
    return NULL;
}

// --- Core Resolution ---

BOOL VclGetNodeBySelf(void* self, VclNode* outNode)
{
    if (!outNode) return FALSE;
    ZeroMemory(outNode, sizeof(VclNode));
    
    if (!VclIsObjectAlive(self)) {
        Log("[VclGetNodeBySelf] Self %p invalid", self);
        return FALSE;
    }
    
    void* vmt = NULL;
    if (!SafeReadPtr(self, &vmt)) {
        Log("[VclGetNodeBySelf] Failed to read VMT from %p", self);
        return FALSE;
    }
    if (!IsPtrRX(vmt)) {
        Log("[VclGetNodeBySelf] VMT %p not RX", vmt);
        return FALSE;
    }
    
    __try
    {
        outNode->self = self;
        outNode->vmt = vmt;
        outNode->is_vcl = TRUE;
        
        if (TryGetVclClassName(vmt, outNode->class_name, 256))
            outNode->confidence += 20;
        else
            Log("[VclGetNodeBySelf] Class name extraction failed for %p", self);
        
        if (TryGetComponentName(self, outNode->component_name, 256))
            outNode->confidence += 20;

        outNode->parent_self = GetVclParent(self);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return FALSE;
    }
    
    return TRUE;
}

BOOL VclResolveByHwnd(HWND hwnd, VclNode* outNode)
{
    if (!outNode) return FALSE;
    ZeroMemory(outNode, sizeof(VclNode));
    
    outNode->hwnd = hwnd;
    
    void* self = NULL;
    void* vmt = NULL;
    int confidence = TryGetDelphiSelfFromWndProc(hwnd, &self, &vmt);
    
    Log("[VclResolveByHwnd] HWND 0x%llX -> self=%p vmt=%p conf=%d", 
        (unsigned long long)(UINT_PTR)hwnd, self, vmt, confidence);
    
    if (confidence > 0 && self)
    {
        if (VclGetNodeBySelf(self, outNode))
        {
            outNode->confidence += confidence;
            return TRUE;
        }
        else
        {
            Log("[VclResolveByHwnd] VclGetNodeBySelf failed for self=%p", self);
        }
    }
    else
    {
        Log("[VclResolveByHwnd] TryGetDelphiSelfFromWndProc failed or low confidence");
    }
    
    outNode->parent_hwnd = GetAncestor(hwnd, GA_PARENT);
    GetWindowRect(hwnd, &outNode->bounds);
    outNode->visible = IsWindowVisible(hwnd);
    outNode->enabled = IsWindowEnabled(hwnd);
    
    return FALSE;
}

// --- VCL Object Discovery ---

static int TryGetDelphiSelfFromWndProc(HWND hwnd, void** outSelf, void** outVmt)
{
    *outSelf = NULL;
    *outVmt = NULL;
    
    // Check unicode status to choose correct API
    BOOL isUnicode = IsWindowUnicode(hwnd);
    Log("[TryGetDelphiSelfFromWndProc] IsWindowUnicode(%p) = %d", hwnd, isUnicode);
    
    WNDPROC p = NULL;
    if (isUnicode) {
        p = (WNDPROC)GetWindowLongPtrW(hwnd, GWLP_WNDPROC);
    } else {
        p = (WNDPROC)GetWindowLongPtrA(hwnd, GWLP_WNDPROC);
    }
    
    if (!p) {
        Log("[TryGetDelphiSelfFromWndProc] GetWindowLongPtr failed");
        return 0;
    }
    
    if (!IsPtrRX(p)) {
        Log("[TryGetDelphiSelfFromWndProc] WndProc %p is not executable", p);
        return 0;
    }

    unsigned char* code = (unsigned char*)p;
    
    // Log first few bytes for debugging
    unsigned char bytes[16] = {0};
    __try { memcpy(bytes, code, 16); } __except(1) {}
    Log("[TryGetDelphiSelfFromWndProc] WndProc=%p Bytes: %02X %02X %02X %02X %02X %02X %02X %02X", 
        p, bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7]);
    
    void* candidateSelf = NULL;
    
    __try {
#ifdef _WIN64
        // Check for "mov rcx, constant" (StdWndProc uses this to load Self)
        // 48 B9 xx xx xx xx xx xx xx xx  mov rcx, 64-bit constant
        if (code[0] == 0x48 && code[1] == 0xB9) 
        {
            candidateSelf = *(void**)(code + 2);
            Log("[TryGetDelphiSelfFromWndProc] Found 'mov rcx, imm64': %p", candidateSelf);
        }
        // Check for "mov eax, constant" (sometimes used in wrappers)
        // B8 xx xx xx xx
        else if (code[0] == 0xB8)
        {
             candidateSelf = *(void**)(code + 1); // 32-bit constant in 64-bit mode? rare but check
             Log("[TryGetDelphiSelfFromWndProc] Found 'mov eax, imm32': %p", candidateSelf);
        }
        // Check for relative or other instructions if needed
#else
        // 32-bit StdWndProc
        // B8 xx xx xx xx       mov eax, constant (Self)
        // E8 .. .. .. ..       call ...
        if (code[0] == 0xB8) 
        {
            candidateSelf = *(void**)(code + 1);
            Log("[TryGetDelphiSelfFromWndProc] Found 'mov eax, imm32': %p", candidateSelf);
        }
        else if (code[0] == 0x68) // push constant
        {
            candidateSelf = *(void**)(code + 1);
            Log("[TryGetDelphiSelfFromWndProc] Found 'push imm32': %p", candidateSelf);
        }
        else if (code[0] == 0xE8) // call relative
        {
            // E8 xx xx xx xx
            int offset = *(int*)(code + 1);
            unsigned char* target = code + 5 + offset;
            Log("[TryGetDelphiSelfFromWndProc] Follow CALL to %p", target);
            
            if (IsPtrRX(target))
            {
                // Check target code
                // Log target bytes
                unsigned char tbytes[16] = {0};
                __try { memcpy(tbytes, target, 16); } __except(1) {}
                Log("[TryGetDelphiSelfFromWndProc] Target=%p Bytes: %02X %02X %02X %02X %02X %02X %02X %02X", 
                    target, tbytes[0], tbytes[1], tbytes[2], tbytes[3], tbytes[4], tbytes[5], tbytes[6], tbytes[7]);
                
                // If target is simple:
                // B8 xx xx xx xx   mov eax, self
                if (target[0] == 0xB8)
                {
                    candidateSelf = *(void**)(target + 1);
                    Log("[TryGetDelphiSelfFromWndProc] Found 'mov eax, imm32' in target: %p", candidateSelf);
                }
                // JMP relative? E9 ...
                else if (target[0] == 0xE9)
                {
                    int off2 = *(int*)(target + 1);
                    unsigned char* target2 = target + 5 + off2;
                    Log("[TryGetDelphiSelfFromWndProc] Follow JMP to %p", target2);
                    if (IsPtrRX(target2) && target2[0] == 0xB8)
                    {
                        candidateSelf = *(void**)(target2 + 1);
                        Log("[TryGetDelphiSelfFromWndProc] Found 'mov eax, imm32' in target2: %p", candidateSelf);
                    }
                }
                // POP ECX (59) + JMP (E9) ? 
                // Target=01B00004 Bytes: 59 E9 F2 B5 B3 FE
                else if (target[0] == 0x59 && target[1] == 0xE9)
                {
                    // Skip 59 (1 byte)
                    // JMP is at target+1
                    int off2 = *(int*)(target + 2); // E9 is 1 byte, offset is 4 bytes
                    unsigned char* target2 = target + 1 + 5 + off2;
                    Log("[TryGetDelphiSelfFromWndProc] Found 'pop ecx; jmp ...' -> Follow to %p", target2);
                    
                    if (IsPtrRX(target2))
                    {
                        // Log target2 bytes
                        unsigned char t2bytes[16] = {0};
                        __try { memcpy(t2bytes, target2, 16); } __except(1) {}
                        Log("[TryGetDelphiSelfFromWndProc] Target2=%p Bytes: %02X %02X %02X %02X %02X %02X %02X %02X", 
                            target2, t2bytes[0], t2bytes[1], t2bytes[2], t2bytes[3], t2bytes[4], t2bytes[5], t2bytes[6], t2bytes[7]);

                        if (target2[0] == 0xB8)
                        {
                            candidateSelf = *(void**)(target2 + 1);
                            Log("[TryGetDelphiSelfFromWndProc] Found 'mov eax, imm32' in target2: %p", candidateSelf);
                        }
                        // PUSH EBP; MOV EBP, ESP; PUSH 0; PUSH imm32
                        // 55 8B EC 6A 00 68 xx xx xx xx
                        else if (target2[0] == 0x55 && target2[1] == 0x8B && target2[2] == 0xEC && 
                                 target2[3] == 0x6A && target2[4] == 0x00 && target2[5] == 0x68)
                        {
                             candidateSelf = *(void**)(target2 + 6);
                             Log("[TryGetDelphiSelfFromWndProc] Found 'StdWndProc push imm32' in target2: %p", candidateSelf);
                        }
                    }
                    
                    // Also check if Self is embedded in the WndProc itself, after the CALL
                    // E8 xx xx xx xx [MethodInstance]
                    // POP ECX in thunk means ECX points to MethodInstance
                    // TMethodInstance { Code, Data (Self) }
                    if (!candidateSelf)
                    {
                        void* methodInstance = (void*)(code + 5);
                        Log("[TryGetDelphiSelfFromWndProc] Checking TMethodInstance at %p", methodInstance);
                        
                        if (!IsPtrReadable(methodInstance)) {
                             Log("[TryGetDelphiSelfFromWndProc] MethodInstance %p is not readable", methodInstance);
                        } else {
                             // TMethodInstance structure:
                             // Offset 0: Method Address
                             // Offset 4: Data (Self)
                             
                             void* methodPtr = NULL;
                             void* dataPtr = NULL;
                             int ptrSize = sizeof(void*);
                             
                             if (SafeReadPtr(methodInstance, &methodPtr) && 
                                 SafeReadPtr((char*)methodInstance + ptrSize, &dataPtr))
                             {
                                 Log("[TryGetDelphiSelfFromWndProc] TMethodInstance: Method=%p Data(Self)=%p", methodPtr, dataPtr);
                                 
                                 // Check if dataPtr is a valid object
                                 if (IsPtrReadable(dataPtr))
                                 {
                                     void* vmt = NULL;
                                     if (SafeReadPtr(dataPtr, &vmt) && IsPtrReadable(vmt))
                                     {
                                         WCHAR cls[128];
                                         if (TryGetVclClassName(vmt, cls, 128))
                                         {
                                             candidateSelf = dataPtr;
                                             Log("[TryGetDelphiSelfFromWndProc] MATCH: Found Self %p Class %S", candidateSelf, cls);
                                         }
                                         else
                                         {
                                             Log("[TryGetDelphiSelfFromWndProc] Data VMT %p has no class name", vmt);
                                         }
                                     }
                                     else
                                     {
                                         Log("[TryGetDelphiSelfFromWndProc] Data %p is not a valid object", dataPtr);
                                     }
                                 }
                                 else
                                 {
                                     Log("[TryGetDelphiSelfFromWndProc] Data ptr %p is not readable", dataPtr);
                                 }
                             }
                             else
                             {
                                 Log("[TryGetDelphiSelfFromWndProc] Failed to read TMethodInstance fields");
                             }
                        }
                    }
                }
            }
        }
#endif
    }
    __except(EXCEPTION_EXECUTE_HANDLER) {
        Log("[TryGetDelphiSelfFromWndProc] Exception reading code at %p", p);
        return 0;
    }

    if (!candidateSelf) {
        Log("[TryGetDelphiSelfFromWndProc] No pattern matched - final failure");
        return 0;
    }

    // Strict validation of candidateSelf
    if (!IsPtrReadable(candidateSelf)) {
        Log("[TryGetDelphiSelfFromWndProc] Candidate %p is not readable", candidateSelf);
        return 0;
    }
    
    // Check if it looks like an object (first field is VMT)
    void* candidateVmt = NULL;
    if (!SafeReadPtr(candidateSelf, &candidateVmt)) {
        Log("[TryGetDelphiSelfFromWndProc] Failed to read VMT from %p", candidateSelf);
        return 0;
    }

    if (!IsPtrReadable(candidateVmt)) {
        Log("[TryGetDelphiSelfFromWndProc] VMT %p is not readable", candidateVmt);
        return 0;
    }
    
    // Optional: Check if VMT points to code (virtual methods)
    // Most VMT entries are code pointers.
    // Let's try to extract class name to be sure
    WCHAR cls[128];
    if (!TryGetVclClassName(candidateVmt, cls, 128)) {
        Log("[TryGetDelphiSelfFromWndProc] Candidate %p has VMT %p but no class name found", candidateSelf, candidateVmt);
        // Don't fail immediately, maybe class name logic is too strict?
        // But for now, let's require it to filter garbage.
        return 0;
    }
    
    Log("[TryGetDelphiSelfFromWndProc] Confirmed match: Self=%p Class=%S", candidateSelf, cls);
    
    *outSelf = candidateSelf;
    *outVmt = candidateVmt;
    
    return 80; 
}

// --- Hierarchy & Traversal ---

void** VclGetChildControls(void* self, int* count)
{
    if (!count) return NULL;
    *count = 0;
    if (!VclIsObjectAlive(self)) return NULL;
    
    unsigned char* base = (unsigned char*)self;
    int ptrSize = sizeof(void*);

    if (!g_Profile.detected) DetectProfile(self);
    if (g_Profile.offControls > 0 && g_Profile.offControlCount > 0)
    {
        void* fList = NULL;
        int fCount = 0;
        if (SafeReadPtr(base + g_Profile.offControls, &fList) &&
            SafeReadInt(base + g_Profile.offControlCount, &fCount) &&
            fCount >= 0 && fCount <= 4096 &&
            (fCount == 0 || (fList && IsPtrReadable(fList))))
        {
            if (fCount == 0) return NULL;
            void** result = (void**)LocalAlloc(LPTR, fCount * sizeof(void*));
            if (!result) return NULL;
            __try {
                for (int i = 0; i < fCount; i++)
                {
                    void* el = NULL;
                    void* elVmt = NULL;
                    if (!SafeReadPtr((char*)fList + i * ptrSize, &el) ||
                        !el || !IsPtrReadable(el) ||
                        !SafeReadPtr(el, &elVmt) || !IsPtrRX(elVmt))
                        result[i] = NULL;
                    else
                        result[i] = el;
                }
            } __except(EXCEPTION_EXECUTE_HANDLER) {
                LocalFree(result);
                return NULL;
            }
            *count = fCount;
            return result;
        }
    }
    
    for (int off = ptrSize; off < 256; off += ptrSize)
    {
        void* ptr = NULL;
        if (!SafeReadPtr(base + off, &ptr)) continue;
        
        if (!ptr || !IsPtrReadable(ptr)) continue;
        
        void* listVmt = NULL;
        if (!SafeReadPtr(ptr, &listVmt)) continue;
        
        if (!IsPtrRX(listVmt)) continue; 
        
        void* fList = NULL;
        int fCount = 0;
        
        if (!SafeReadPtr((char*)ptr + ptrSize, &fList)) continue;
        if (!SafeReadInt((char*)ptr + ptrSize * 2, &fCount)) continue;
        
        if (fCount < 0 || fCount > 2048) continue;
        if (fCount > 0 && (!fList || !IsPtrReadable(fList))) continue;
        
        if (fCount > 0)
        {
            BOOL allControls = TRUE;
            for (int i = 0; i < (fCount > 5 ? 5 : fCount); i++)
            {
                void* el = NULL;
                if (!SafeReadPtr((char*)fList + i * ptrSize, &el)) { allControls = FALSE; break; }
                
                if (!el || !IsPtrReadable(el)) { allControls = FALSE; break; }
                
                void* elVmt = NULL;
                if (!SafeReadPtr(el, &elVmt)) { allControls = FALSE; break; }
                
                if (!IsPtrRX(elVmt)) { allControls = FALSE; break; }
                
                WCHAR cls[64];
                if (!TryGetVclClassName(elVmt, cls, 64)) { allControls = FALSE; break; }
            }
            
            if (allControls)
            {
                *count = fCount;
                void** result = (void**)LocalAlloc(LPTR, fCount * sizeof(void*));
                if (result)
                {
                    __try {
                        for (int i = 0; i < fCount; i++) {
                            if (!SafeReadPtr((char*)fList + i * ptrSize, &result[i])) result[i] = NULL;
                        }
                    } __except(EXCEPTION_EXECUTE_HANDLER) {
                        LocalFree(result);
                        *count = 0;
                        return NULL;
                    }
                }
                return result;
            }
        }
    }
    
    return NULL;
}

// --- Parent Extraction Logic ---

static BOOL IsIgnoredClass(const WCHAR* cls)
{
    // Filter out non-visual components and common property types
    if (lstrcmpiW(cls, L"TApplication") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TScreen") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TFont") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TBrush") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TPen") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TImageList") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TIcon") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TMetafile") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TPicture") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TAction") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TActionList") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TMenuItem") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TPopupMenu") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TMainMenu") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TTimer") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TDataSource") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TDataSet") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TField") == 0) return TRUE;
    if (WStartsWithI(cls, L"TOra")) return TRUE;
    if (WStartsWithI(cls, L"TMemDataSet")) return TRUE;
    if (WStartsWithI(cls, L"TCustomDADataSet")) return TRUE;
    if (WStartsWithI(cls, L"TCRDBGridDataSource")) return TRUE;
    if (WStartsWithI(cls, L"TCssOCI")) return TRUE;
    if (lstrcmpiW(cls, L"TStrings") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TStringList") == 0) return TRUE;
    // Internal lists and structures
    if (lstrcmpiW(cls, L"TList") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TObjectList") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TInterfaceList") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TPadding") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TMargins") == 0) return TRUE;
    if (lstrcmpiW(cls, L"TSizeConstraints") == 0) return TRUE;
    return FALSE;
}

// Check if a pointer is likely a code pointer (executable)
static BOOL IsCodePtr(void* p)
{
    return IsPtrRX(p);
}

static void* GetVclParent(void* self)
{
    if (!VclIsObjectAlive(self)) return NULL;
    BOOL helperTried = FALSE;
    if (TryResolveControlAtPosHelper() && g_GetParentHelper)
    {
        helperTried = TRUE;
        void* p = NULL;
        __try { p = g_GetParentHelper(self); } __except(EXCEPTION_EXECUTE_HANDLER) { p = NULL; }
        if (p && p != self && VclIsObjectAlive(p))
            return p;
    }
    
    // Ensure profile is ready
    if (!g_Profile.detected) DetectProfile(self);
    
    unsigned char* base = (unsigned char*)self;

    // STRATEGY 1: Use profiled offset
    if (g_Profile.offParent > 0)
    {
        void* parent = NULL;
        if (SafeReadPtr(base + g_Profile.offParent, &parent))
        {
            if (VclIsObjectAlive(parent))
            {
                // Verify VCL object
                void* vmt = NULL;
                if (SafeReadPtr(parent, &vmt) && IsPtrReadable(vmt))
                {
                    WCHAR cls[128];
                    if (TryGetVclClassName(vmt, cls, 128))
                    {
                        if (!IsIgnoredClass(cls) && IsParentByControls(parent, self))
                        {
                            Log("[VclParent] Using profiled Parent offset %d parent=%p class=%S", g_Profile.offParent, parent, cls);
                            return parent;
                        }
                    }
                }
            }
        }
        Log("[VclParent] Profiled Parent offset %d failed validation", g_Profile.offParent);
    }
    
    if (helperTried)
        Log("[VclParent] No parent from helper");
    else
        Log("[VclParent] No parent found");
    return NULL;
}

void** VclGetComponents(void* self, int* count) { *count = 0; return NULL; }

VclNode* VclGetPathToSelf(void* self, int* count)
{
    *count = 0;
    if (!self) {
        Log("[VclGetPathToSelf] Called with NULL self");
        return NULL;
    }
    
    Log("[VclGetPathToSelf] Start building path for self=%p", self);
    
    void* path[64];
    int depth = 0;
    
    void* current = self;
    while (current && depth < 64)
    {
        path[depth] = current;
        
        // Log current step details
        WCHAR cls[128] = {0};
        WCHAR name[128] = {0};
        void* vmt = NULL;
        if (SafeReadPtr(current, &vmt)) TryGetVclClassName(vmt, cls, 128);
        TryGetComponentName(current, name, 128);
        
        Log("[VclGetPathToSelf] Step %d: ptr=%p class=%S name=%S", depth, current, cls, name);
        
        depth++;
        
        void* parent = GetVclParent(current);
        if (!parent) {
            Log("[VclGetPathToSelf] Stopped: Parent is NULL for %p", current);
            break;
        }
        if (parent == current) {
            Log("[VclGetPathToSelf] Stopped: Parent loop (parent==self) for %p", current);
            break; 
        }
        
        // Log choice
        Log("[VclGetPathToSelf] Moving up to parent: %p", parent);
        
        // Loop check in path
        BOOL loop = FALSE;
        for (int i = 0; i < depth; i++) {
            if (path[i] == parent) {
                loop = TRUE;
                Log("[VclGetPathToSelf] Stopped: Cycle detected (parent %p already in path)", parent);
                break;
            }
        }
        if (loop) break;
        
        current = parent;
    }
    
    if (depth == 0) return NULL;
    
    VclNode* result = (VclNode*)LocalAlloc(LPTR, depth * sizeof(VclNode));
    if (!result) return NULL;
    
    int outDepth = 0;
    for (int i = 0; i < depth; i++)
    {
        void* nodeSelf = path[depth - 1 - i];
        WCHAR nodeCls[256] = { 0 };
        void* nodeVmt = NULL;
        if (SafeReadPtr(nodeSelf, &nodeVmt))
            TryGetVclClassName(nodeVmt, nodeCls, 256);
        if (lstrcmpiW(nodeCls, L"TApplication") == 0 && i < depth - 1)
            continue;
        
        // We use VclGetNodeBySelf to fill details, but even if name extraction fails, 
        // we keep the node if we at least have a class name or just self.
        if (!VclGetNodeBySelf(nodeSelf, &result[outDepth]))
        {
            // Fallback: manually fill what we can
            ZeroMemory(&result[outDepth], sizeof(VclNode));
            result[outDepth].self = nodeSelf;
            result[outDepth].is_vcl = TRUE;
            
            void* vmt = NULL;
            if (SafeReadPtr(nodeSelf, &vmt)) {
                 TryGetVclClassName(vmt, result[outDepth].class_name, 256);
            }
            // Name might be empty, that's fine for diagnosis
            Log("[VclGetPathToSelf] Warning: VclGetNodeBySelf failed for %p, using fallback", nodeSelf);
        }
        outDepth++;
    }
    
    *count = outDepth;
    Log("[VclGetPathToSelf] Path built successfully, depth=%d", outDepth);
    return result;
}

// --- Properties Extraction ---

static BOOL WStartsWithI(const WCHAR* s, const WCHAR* prefix)
{
    if (!s || !prefix) return FALSE;
    size_t n = lstrlenW(prefix);
    if ((size_t)lstrlenW(s) < n) return FALSE;
    return _wcsnicmp(s, prefix, n) == 0;
}

VclControlType VclClassifyControl(const WCHAR* className)
{
    if (!className || !className[0]) return VCL_TYPE_UNKNOWN;
    if (WStartsWithI(className, L"TForm") || WStartsWithI(className, L"Tfrm")) return VCL_TYPE_FORM;
    if (WStartsWithI(className, L"TButton") || WStartsWithI(className, L"TBitBtn") || WStartsWithI(className, L"TSpeedButton") || WStartsWithI(className, L"TClSpeedButton")) return VCL_TYPE_BUTTON;
    if (WStartsWithI(className, L"TEdit") || WStartsWithI(className, L"TMaskEdit") || WStartsWithI(className, L"TcxTextEdit") || WStartsWithI(className, L"TExEdit")) return VCL_TYPE_EDIT;
    if (WStartsWithI(className, L"TLabel")) return VCL_TYPE_LABEL;
    if (WStartsWithI(className, L"TPanel") || WStartsWithI(className, L"TExPanel")) return VCL_TYPE_PANEL;
    if (WStartsWithI(className, L"TDBGrid") || WStartsWithI(className, L"TStringGrid") || WStartsWithI(className, L"TcxGrid") || WStartsWithI(className, L"TcxDBGrid")) return VCL_TYPE_GRID;
    if (WStartsWithI(className, L"TComboBox") || WStartsWithI(className, L"TcxComboBox")) return VCL_TYPE_COMBOBOX;
    if (WStartsWithI(className, L"TListBox") || WStartsWithI(className, L"TcxListBox")) return VCL_TYPE_LISTBOX;
    if (WStartsWithI(className, L"TCheckBox") || WStartsWithI(className, L"TcxCheckBox")) return VCL_TYPE_CHECKBOX;
    if (WStartsWithI(className, L"TRadioButton") || WStartsWithI(className, L"TcxRadioButton")) return VCL_TYPE_RADIOBUTTON;
    if (WStartsWithI(className, L"TMemo") || WStartsWithI(className, L"TcxMemo")) return VCL_TYPE_MEMO;
    if (WStartsWithI(className, L"TPageControl") || WStartsWithI(className, L"TTabControl") || WStartsWithI(className, L"TcxPageControl")) return VCL_TYPE_TABCONTROL;
    if (WStartsWithI(className, L"TToolBar") || WStartsWithI(className, L"TdxBar") || WStartsWithI(className, L"TdxBarManager")) return VCL_TYPE_TOOLBAR;
    if (WStartsWithI(className, L"TMainMenu") || WStartsWithI(className, L"TPopupMenu") || WStartsWithI(className, L"TMenuItem")) return VCL_TYPE_MENU;
    if (WStartsWithI(className, L"TTreeView") || WStartsWithI(className, L"TcxTreeView") || WStartsWithI(className, L"TExTreeView")) return VCL_TYPE_TREEVIEW;
    if (WStartsWithI(className, L"TGroupBox") || WStartsWithI(className, L"TcxGroupBox")) return VCL_TYPE_GROUPBOX;
    return VCL_TYPE_UNKNOWN;
}

BOOL VclBuildLocator(void* self, WCHAR* outBuf, size_t outCch)
{
    if (!self || !outBuf || outCch == 0) return FALSE;
    outBuf[0] = 0;

    void* chain[64];
    int n = 0;
    void* cur = self;
    while (cur && n < 64)
    {
        chain[n++] = cur;
        void* p = GetVclParent(cur);
        if (!p || p == cur) break;
        cur = p;
    }
    if (n <= 0) return FALSE;

    WCHAR part[256];
    for (int i = n - 1; i >= 0; --i)
    {
        WCHAR name[256] = { 0 };
        WCHAR cls[256] = { 0 };
        TryGetComponentName(chain[i], name, 256);
        void* vmt = NULL;
        if (SafeReadPtr(chain[i], &vmt)) TryGetVclClassName(vmt, cls, 256);
        if (name[0]) lstrcpynW(part, name, 256);
        else lstrcpynW(part, cls[0] ? cls : L"Object", 256);

        if (outBuf[0])
        {
            if (lstrlenW(outBuf) + 1 < (int)outCch) wcscat_s(outBuf, outCch, L".");
        }
        if (lstrlenW(outBuf) + lstrlenW(part) + 1 < (int)outCch)
            wcscat_s(outBuf, outCch, part);
    }
    return outBuf[0] != 0;
}

BOOL VclGetFullProperties(void* self, VclFullProperties* outProps)
{
    if (!self || !outProps) return FALSE;
    ZeroMemory(outProps, sizeof(VclFullProperties));
    if (!VclIsObjectAlive(self)) return FALSE;

    outProps->parent_self = GetVclParent(self);
    outProps->owner_self = GetVclOwner(self);
    TryGetComponentName(self, outProps->name, 256);

    void* vmt = NULL;
    if (SafeReadPtr(self, &vmt))
        TryGetVclClassName(vmt, outProps->class_name, 256);

    if (TryResolveControlAtPosHelper() && g_GetControlTypeHelper)
    {
        int t = 0;
        __try { t = g_GetControlTypeHelper(self); } __except(EXCEPTION_EXECUTE_HANDLER) { t = 0; }
        outProps->control_type = (VclControlType)t;
    }
    else
    {
        outProps->control_type = VclClassifyControl(outProps->class_name);
    }
    VclBuildLocator(self, outProps->locator, 1024);

    BOOL vis = TRUE;
    if (TryReadVisible(self, &vis)) outProps->visible = vis;
    else outProps->visible = TRUE;
    outProps->enabled = TRUE;
    outProps->tab_order = -1;
    outProps->component_count = 0;

    if (TryResolveControlAtPosHelper())
    {
        if (g_IsEnabledHelper)
        {
            BOOL en = TRUE;
            __try { en = g_IsEnabledHelper(self); } __except(EXCEPTION_EXECUTE_HANDLER) { en = TRUE; }
            outProps->enabled = en;
        }
        if (g_GetTabOrderHelper)
        {
            int tab = -1;
            __try { tab = g_GetTabOrderHelper(self); } __except(EXCEPTION_EXECUTE_HANDLER) { tab = -1; }
            outProps->tab_order = tab;
        }
        if (g_GetCaptionHelper)
        {
            const char* capA = NULL;
            __try { capA = g_GetCaptionHelper(self); } __except(EXCEPTION_EXECUTE_HANDLER) { capA = NULL; }
            if (capA && capA[0])
                MultiByteToWideChar(CP_UTF8, 0, capA, -1, outProps->caption, 512);
        }
    }

    RECT rc = { 0 };
    if (TryGetVclScreenRect(self, &rc))
    {
        outProps->screen_rect = rc;
        outProps->left = rc.left;
        outProps->top = rc.top;
        outProps->width = rc.right - rc.left;
        outProps->height = rc.bottom - rc.top;
    }

    int childCount = 0;
    void** children = VclGetChildControls(self, &childCount);
    if (children) LocalFree(children);
    outProps->control_count = childCount;

    return TRUE;
}

static BOOL JsonAppend(char* buf, size_t cap, const char* s)
{
    if (!buf || !s || cap == 0) return FALSE;
    size_t cur = strlen(buf);
    size_t add = strlen(s);
    if (cur + add + 1 >= cap) return FALSE;
    strcat_s(buf, cap, s);
    return TRUE;
}

static BOOL BuildTreeRecursive(void* self, int depth, int maxDepth, int* nodeBudget, char* jsonBuf, size_t cap)
{
    if (!self || !jsonBuf || !nodeBudget) return FALSE;
    if (!VclIsObjectAlive(self)) return FALSE;
    if (*nodeBudget <= 0) return FALSE;
    (*nodeBudget)--;

    VclFullProperties fp;
    if (!VclGetFullProperties(self, &fp)) return FALSE;

    char cls[256] = "", name[256] = "", loc[1024] = "";
    WideCharToMultiByte(CP_UTF8, 0, fp.class_name, -1, cls, sizeof(cls), NULL, NULL);
    WideCharToMultiByte(CP_UTF8, 0, fp.name, -1, name, sizeof(name), NULL, NULL);
    WideCharToMultiByte(CP_UTF8, 0, fp.locator, -1, loc, sizeof(loc), NULL, NULL);

    char head[1600];
    snprintf(head, sizeof(head),
        "{\"self\":\"0x%p\",\"class\":\"%s\",\"name\":\"%s\",\"type\":%d,\"locator\":\"%s\",\"left\":%d,\"top\":%d,\"width\":%d,\"height\":%d,\"visible\":%s,\"children\":[",
        self, cls, name, (int)fp.control_type, loc, fp.left, fp.top, fp.width, fp.height, fp.visible ? "true" : "false");
    if (!JsonAppend(jsonBuf, cap, head)) return FALSE;

    if (depth < maxDepth)
    {
        int count = 0;
        void** children = VclGetChildControls(self, &count);
        if (children && count > 0)
        {
            BOOL first = TRUE;
            int limit = count > 4096 ? 4096 : count;
            for (int i = 0; i < limit; i++)
            {
                if (!VclIsObjectAlive(children[i])) continue;
                if (!first) { if (!JsonAppend(jsonBuf, cap, ",")) break; }
                if (!BuildTreeRecursive(children[i], depth + 1, maxDepth, nodeBudget, jsonBuf, cap)) { first = FALSE; continue; }
                first = FALSE;
                if (*nodeBudget <= 0) break;
            }
            LocalFree(children);
        }
    }

    return JsonAppend(jsonBuf, cap, "]}");
}

BOOL VclBuildTree(void* self, int maxDepth, char* jsonBuf, size_t cap)
{
    if (!self || !jsonBuf || cap < 64) return FALSE;
    if (maxDepth < 0) maxDepth = 0;
    if (maxDepth > 16) maxDepth = 16;
    jsonBuf[0] = 0;
    int nodeBudget = 5000;
    return BuildTreeRecursive(self, 0, maxDepth, &nodeBudget, jsonBuf, cap);
}

BOOL VclGetProperties(void* self, char* jsonBuf, size_t cap)
{
    if (!self || !jsonBuf || cap < 16) return FALSE;
    VclFullProperties fp;
    if (!VclGetFullProperties(self, &fp)) return FALSE;

    char clsA[256] = "", nameA[256] = "";
    WideCharToMultiByte(CP_UTF8, 0, fp.class_name, -1, clsA, sizeof(clsA), NULL, NULL);
    WideCharToMultiByte(CP_UTF8, 0, fp.name, -1, nameA, sizeof(nameA), NULL, NULL);
    snprintf(jsonBuf, cap, "{\"ClassName\":\"%s\",\"Name\":\"%s\"}", clsA, nameA);
    return TRUE;
}
