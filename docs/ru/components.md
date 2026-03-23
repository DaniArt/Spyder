# Компоненты

## Spy.UI (WPF UI)

Отвечает за:
- пользовательские сценарии захвата (hover/drag/hotkey),
- overlay подсветку,
- панели метаданных по бэкендам (Win32/UIA/MSAA/VCL),
- генерацию/копирование селектора,
- работу с `locator.json`,
- управление файловым логом.

Интеграции:
- `Spy.Core.SpyCapture` — основной capture pipeline.
- `Spy.Core.Vcl.VclHookManager` — установка и lifecycle VCL-хука.
- `Spy.UI.VclHelperClient` — Named Pipe клиент для VCL операций.

Ключевые файлы:
- `Spy.UI/MainWindow.xaml(.cs)`
- `Spy.UI/OverlayWindow.xaml(.cs)`
- `Spy.UI/GlobalMouseHook.cs`
- `Spy.UI/LocatorStorage.cs`
- `Spy.UI/VclHelperClient.cs`

## Spy.Core (Core)

Отвечает за:
- pipeline захвата и модель `SpyElement`,
- селектор и структурированный локатор,
- best-effort таймауты и fallbacks,
- управление VCL hook.

Ключевые файлы:
- `Spy.Core/SpyCapture.cs`
- `Spy.Core/SelectorBuilder.cs`
- `Spy.Core/Hierarchy.cs`
- `Spy.Core/Vcl/VclHookManager.cs`
- `Spy.Core/Vcl/VclLocatorEngine.cs`

## VclHook (Hook Layer)

Отвечает за:
- работу внутри целевого процесса,
- Named Pipe сервер,
- выполнение VCL операций в UI-потоке цели,
- hit-test/children/path/properties.

Ключевые файлы:
- `VclHook/VclHook.c`
- `VclHook/VclBridge.c/.h`
- `VclHook/SpyderVclHelper32.dpr`

## IPC (Named Pipe)

- Клиент: `Spy.UI/VclHelperClient.cs`
- Сервер: `VclHook/VclHook.c`
- Формат: length-prefixed JSON

См. также:
- `docs/ru/pipe-protocol.md`

## Locator system

- Структурированный локатор: `Spy.Core/SelectorBuilder.cs`
- Хранение и схема: `Spy.UI/LocatorStorage.cs`

См. также:
- `docs/ru/locator-format.md`
