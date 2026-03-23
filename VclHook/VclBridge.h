#ifndef VCL_BRIDGE_H
#define VCL_BRIDGE_H

#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

// Represents a node in the VCL object tree
typedef struct _VclNode {
    void* self;           // VCL object pointer (TObject*)
    void* vmt;            // VMT pointer
    HWND hwnd;            // Associated window handle (if TWinControl), or NULL
    BOOL is_vcl;          // TRUE if this is a valid VCL object
    BOOL is_win_control;  // TRUE if TWinControl (has HWND)
    
    // Identity
    WCHAR class_name[256];
    WCHAR component_name[256];
    WCHAR caption[256];
    
    // Hierarchy
    void* parent_self;    // TControl.Parent
    void* owner_self;     // TComponent.Owner
    HWND parent_hwnd;     // Win32 parent (fallback)
    
    // State
    RECT bounds;          // Screen coordinates
    BOOL visible;
    BOOL enabled;
    int control_count;    // TWinControl.ControlCount
    int component_count;  // TComponent.ComponentCount
    
    // Diagnostics
    int confidence;       // 0..100
} VclNode;

typedef enum _VclControlType {
    VCL_TYPE_UNKNOWN = 0,
    VCL_TYPE_FORM,
    VCL_TYPE_BUTTON,
    VCL_TYPE_EDIT,
    VCL_TYPE_LABEL,
    VCL_TYPE_PANEL,
    VCL_TYPE_GRID,
    VCL_TYPE_COMBOBOX,
    VCL_TYPE_LISTBOX,
    VCL_TYPE_CHECKBOX,
    VCL_TYPE_RADIOBUTTON,
    VCL_TYPE_MEMO,
    VCL_TYPE_TABCONTROL,
    VCL_TYPE_TOOLBAR,
    VCL_TYPE_MENU,
    VCL_TYPE_TREEVIEW,
    VCL_TYPE_GROUPBOX
} VclControlType;

typedef struct _VclFullProperties {
    WCHAR class_name[256];
    WCHAR name[256];
    WCHAR caption[512];
    WCHAR hint[512];
    int left, top, width, height;
    BOOL visible, enabled;
    int tab_order;
    int control_count;
    int component_count;
    void* parent_self;
    void* owner_self;
    RECT screen_rect;
    VclControlType control_type;
    WCHAR locator[1024];
} VclFullProperties;

// --- Core Resolution ---

// Resolve VCL node from HWND (TWinControl)
BOOL VclResolveByHwnd(HWND hwnd, VclNode* outNode);

// Resolve VCL node from Self pointer (TObject*)
BOOL VclGetNodeBySelf(void* self, VclNode* outNode);

// --- Hierarchy & Traversal ---

// Get children (Controls) for a TWinControl
// Returns array of child self pointers. Caller must free using LocalFree.
// count is output parameter.
void** VclGetChildControls(void* self, int* count);

// Get owned components (Components) for a TComponent
void** VclGetComponents(void* self, int* count);

// Build path from Root Form to target Self
// Returns array of VclNode. Caller must free using LocalFree.
VclNode* VclGetPathToSelf(void* self, int* count);

BOOL VclHitTest(HWND parentHwnd, void* parentSelf, int screenX, int screenY, VclNode* outNode);

// --- Properties ---
BOOL VclGetProperties(void* self, char* jsonBuf, size_t cap);
BOOL VclGetFullProperties(void* self, VclFullProperties* outProps);
BOOL VclBuildLocator(void* self, WCHAR* outBuf, size_t outCch);
VclControlType VclClassifyControl(const WCHAR* className);
BOOL VclBuildTree(void* self, int maxDepth, char* jsonBuf, size_t cap);

// --- Diagnostics ---
BOOL IsPtrReadable(const void* p);
BOOL IsPtrRX(const void* p); // Readable & Executable

// Logging
void Log(const char* fmt, ...);
extern volatile BOOL g_isCapture;

#ifdef __cplusplus
}
#endif

#endif // VCL_BRIDGE_H
