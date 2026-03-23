# Глубокая структура репозитория

Документ описывает ключевые файлы и их роль. Вспомогательные утилиты группируются, фокус — на entry points и основных интеграциях.

## Root

## `Spyder.slnx`
- Назначение: solution descriptor managed проектов.
- Используется: разработчиками для открытия репозитория в IDE.

## `build-dist.ps1`
- Назначение: helper-скрипт для publish/дистрибутива.
- Взаимодействует с: `Spy.UI/Spyder.csproj`.

## `PROJECT_CONTEXT.md`
- Назначение: заметки и ограничения по разработке.

## Spy.Core

## `Spy.Core/SpyCapture.cs`
- Назначение: pipeline захвата, формирует `SpyElement` из курсора/точки.
- Используется: `Spy.UI/MainWindow.xaml.cs`.
- Взаимодействует:
  - Win32 (всегда),
  - UIA (таймаут),
  - MSAA (таймаут),
  - VCL backend (опционально).

## `Spy.Core/SelectorBuilder.cs`
- Назначение:
  - формирование строки селектора,
  - определение схемы структурированного локатора (`StructuredLocator`) для `locator.json`.
- Используется: UI workflow.
- Взаимодействует: с моделью `SpyElement`.

## `Spy.Core/Hierarchy.cs`
- Назначение: модель иерархии для UI панелей.
- Используется: `Spy.UI`.

## `Spy.Core/Locator.cs`
- Назначение: модель локатора и форматирование.

## `Spy.Core/Vcl/VclHookManager.cs`
- Назначение: установка и lifecycle VCL хука.
- Используется: `Spy.UI`.
- Взаимодействует:
  - загрузка нативной DLL,
  - `SetWindowsHookEx(WH_GETMESSAGE)` в GUI thread цели,
  - проверка битности и ошибки.

## `Spy.Core/Vcl/VclLocatorEngine.cs`
- Назначение: устойчивое разрешение VCL цепочек и кеш дерева.
- Используется: в VCL workflow managed клиента.

## `Spy.Core/Logging/*` (группа)
- Назначение: утилиты логирования для managed части.

## Spy.UI

## `Spy.UI/App.xaml(.cs)`
- Назначение: вход WPF.

## `Spy.UI/MainWindow.xaml(.cs)`
- Назначение: основной UX:
  - старт/стоп spy mode,
  - триггеры захвата,
  - обновление панелей и селектора,
  - координация VCL подсистемы,
  - запись `locator.json`.
- Взаимодействует:
  - `Spy.Core.SpyCapture`
  - `Spy.Core.SelectorBuilder`
  - `Spy.Core.Vcl.VclHookManager`
  - `Spy.UI.VclHelperClient`
  - overlay окно.

## `Spy.UI/VclHelperClient.cs`
- Назначение: Named Pipe клиент и VCL операции.
- Команды: resolve/hit/get_children/get_path/get_properties.

## `Spy.UI/LocatorStorage.cs`
- Назначение: схема `locator.json` и I/O.

## `Spy.UI/OverlayWindow.xaml(.cs)`
- Назначение: click-through подсветка.

## `Spy.UI/*Window.xaml(.cs)` (группа)
- Назначение: вспомогательные окна (выбор процесса, менеджер локаторов, диалоги).

## VclHook

## `VclHook/VclHook.c`
- Назначение:
  - экспорт `GetMsgHookProc` (WH_GETMESSAGE),
  - pipe server,
  - dispatch команд в UI поток цели.

## `VclHook/VclBridge.c/.h`
- Назначение:
  - безопасные проверки и чтение,
  - VCL hit-test,
  - дети/путь/свойства,
  - логирование.

## `VclHook/SpyderVclHelper32.dpr`
- Назначение: Delphi helper DLL для in-process VCL API.

## `VclHook/Build*.bat` (группа)
- Назначение: entry points сборки нативных артефактов.
