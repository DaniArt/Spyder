# Формат locator.json

Spyder хранит захваченные элементы в версируемом JSON файле. Цель — стабильные, явные и reviewable локаторы.

## Схема верхнего уровня

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

Поля:
- `schema_version`: версия схемы (сейчас `1`)
- `elements`: словарь `id → запись`

## Запись элемента

- `human_selector` (опционально): человекочитаемый селектор.
- `locator` (опционально): структурированный локатор.
- `selector` (опционально): legacy строка.
- `description` (опционально): заметка.
- `updated_at`: ISO timestamp.

## Structured locator

Определение схемы находится в:
- `Spy.Core/SelectorBuilder.cs` (`StructuredLocator`)

Типичные поля:
- `backendPriority`
- `process`
- `vclChain`
- `uia` / `win32` подсказки

## Пример

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
      "updated_at": "2026-03-20T03:05:22.123Z"
    }
  }
}
```
