# Formato `locator.json`

Spyder guarda elementos capturados en un archivo JSON versionado para mantener locators estables y revisables.

## Esquema superior

```json
{
  "schema_version": 1,
  "elements": {
    "id": {
      "human_selector": "...",
      "locator": {},
      "selector": "...",
      "description": "...",
      "updated_at": "..."
    }
  }
}
```

Campos:
- `schema_version`: versión del esquema (actualmente `1`)
- `elements`: diccionario `id → registro`

## Registro de elemento

- `human_selector` (opcional): selector legible.
- `locator` (opcional): locator estructurado.
- `selector` (opcional): legacy.
- `description` (opcional).
- `updated_at`: timestamp ISO.

## Locator estructurado

Definición en:
- `Spy.Core/SelectorBuilder.cs` (`StructuredLocator`)

Campos típicos:
- `backendPriority`
- `process`
- `vclChain`
- hints `uia` / `win32`

## Ejemplo

```json
{
  "schema_version": 1,
  "elements": {
    "example.login.button": {
      "human_selector": "App(\"...\").Form(\"...\").Button(\"...\")",
      "locator": {
        "backendPriority": ["vcl", "uia", "win32", "msaa"],
        "process": "ExampleApp",
        "vclChain": ["TMainForm", "pnlClient", "btnLogin"]
      },
      "updated_at": "2026-03-20T03:05:22.123Z"
    }
  }
}
```
