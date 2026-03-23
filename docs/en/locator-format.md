# Locator File Format (`locator.json`)

Spyder stores captured elements in a versioned JSON file. The goal is to keep locators stable, explicit, and reviewable in source control.

## Location and Lifecycle

- The locator file path is chosen in the UI.
- Entries are added/updated based on captured elements.
- The schema is versioned to support forward evolution.

## Top-level Schema

```json
{
  "schema_version": 1,
  "elements": {
    "id": {
      "human_selector": "...",
      "locator": { },
      "selector": "...",
      "description": "...",
      "updated_at": "..."
    }
  }
}
```

Fields:
- `schema_version` (int): schema version (currently `1`).
- `elements` (object): dictionary of element records keyed by an ID.

## Element Record

Common fields:
- `human_selector` (string, optional): a readable selector for quick copy/paste.
- `locator` (object, optional): structured locator used by automation code.
- `selector` (string, optional): legacy string selector for compatibility.
- `description` (string, optional): human notes.
- `updated_at` (string): ISO 8601 timestamp.

## Structured Locator

The structured locator schema is defined in:
- `Spy.Core/SelectorBuilder.cs` (`StructuredLocator`)

Typical fields include:
- `backendPriority`: ordered backend preference (e.g., `vcl`, `uia`, `win32`, `msaa`).
- `process`: target process name.
- Backend-specific hints (`uia`, `win32`, `vclChain`) used to improve stability.

## Example

```json
{
  "schema_version": 1,
  "elements": {
    "example.login.button": {
      "human_selector": "App(\"...\").Form(\"...\").Button(\"...\")",
      "locator": {
        "backendPriority": ["vcl", "uia", "win32", "msaa"],
        "process": "ExampleApp",
        "vclChain": ["TMainForm", "pnlClient", "btnLogin"],
        "uia": { "controlType": "Button", "name": "Login" },
        "win32": { "className": "Button", "text": "Login" }
      },
      "description": "Login button",
      "updated_at": "2026-03-20T03:05:22.123Z"
    }
  }
}
```
