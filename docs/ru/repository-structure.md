# Структура репозитория

Этот документ — верхнеуровневая карта репозитория и назначение модулей.

## Корень

- `.gitignore` — исключает `bin/`, `obj/` и локальные логи из контроля версий.
- `Spyder.slnx` — solution descriptor для managed проектов.
- `build-dist.ps1` — скрипт публикации/сборки дистрибутива.
- `PROJECT_CONTEXT.md` — заметки и ограничения по разработке.

## Managed проекты

## `Spy.UI/` (WPF приложение)

Назначение:
- UX, режимы захвата, overlay, отображение метаданных, запись `locator.json`.

Ключевые файлы:
- `App.xaml(.cs)` — вход WPF.
- `MainWindow.xaml(.cs)` — основной workflow.
- `OverlayWindow.xaml(.cs)` — подсветка.
- `VclHelperClient.cs` — IPC клиент для VCL.
- `LocatorStorage.cs` — формат и I/O для `locator.json`.

## `Spy.Core/` (core библиотека)

Назначение:
- capture pipeline, модели, селекторы/локаторы, lifecycle VCL-хука.

Ключевые файлы:
- `SpyCapture.cs`
- `SelectorBuilder.cs`
- `Hierarchy.cs`
- `Vcl/VclHookManager.cs`
- `Vcl/VclLocatorEngine.cs`

## Native

## `VclHook/`

Назначение:
- нативный hook и VCL bridge для работы внутри целевого процесса.

Ключевые файлы:
- `VclHook.c`
- `VclBridge.c/.h`
- `VclHookLoader.c`
- `SpyderVclHelper32.dpr`
- `Build*.bat`

## `docs/`

Мультиязычная документация:

```text
docs/
  en/
  ru/
  es/
```
