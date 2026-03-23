# Named Pipe Protocol

Spyder uses a Named Pipe RPC protocol for managed ↔ in-process communication when VCL introspection is enabled.

## Pipe Name

The in-process server creates:

`\\.\pipe\Spyder.VclHelper.<pid>`

Where `<pid>` is the target process ID.

## Framing

Messages are framed as:
- `int32 length` (little-endian)
- `length` bytes of UTF-8 JSON payload

Responses use the same framing.

## Common Fields

Requests are JSON objects with:
- `cmd` (string): command name
- command-specific fields (e.g., `hwnd`, `self`, `x`, `y`)

Responses typically include:
- `ok` (boolean)
- command-specific fields
- `error` (string) on failure

## Commands

### `resolve_by_hwnd`
Maps an HWND to a VCL root control (best-effort).

Request:
```json
{ "cmd": "resolve_by_hwnd", "hwnd": 123456 }
```

Response:
```json
{ "ok": true, "is_vcl": true, "vcl_self": "0x0123ABCD", "vcl_class": "TForm", "vcl_name": "MainForm" }
```

### `hit_test` and `hit_test_overlay`
Returns the VCL control at a screen point and its bounds.

Request:
```json
{ "cmd": "hit_test_overlay", "hwnd": 123456, "self": "0x0123ABCD", "x": 100, "y": 200 }
```

Response:
```json
{
  "ok": true,
  "hit": true,
  "self": "0x0456CDEF",
  "class": "TControlClass",
  "name": "ComponentName",
  "left": 10, "top": 20, "right": 110, "bottom": 70
}
```

### `get_children`
Returns direct child items for a given VCL object.

Request:
```json
{ "cmd": "get_children", "self": "0x0456CDEF" }
```

Response:
```json
{ "ok": true, "children": [ { "self": "0x...", "class": "T...", "name": "..." } ] }
```

### `get_path`
Returns a parent chain for a VCL object.

Request:
```json
{ "cmd": "get_path", "self": "0x0456CDEF" }
```

Response:
```json
{ "ok": true, "path": [ { "class": "TForm", "name": "MainForm", "self": "0x..." } ] }
```

### `get_properties`
Returns a best-effort property map suitable for UI display.

Request:
```json
{ "cmd": "get_properties", "self": "0x0456CDEF" }
```

Response:
```json
{ "ok": true, "properties": { "ClassName": "T...", "Name": "..." } }
```

### `get_full_properties`
Returns extended metadata (implementation-defined). Used for advanced heuristics.

### `shutdown`
Requests server shutdown (best-effort).

## Timeouts

The client enforces timeouts:
- Connect timeout for pipe readiness.
- Request timeout to avoid UI thread stalls.

On timeout, the client treats VCL introspection as unavailable for that capture and continues with other backends.
