# Spyder — Обзор

Spyder — open-source инструмент для инспекции UI на Windows и подготовки локаторов. Он захватывает элемент под курсором, извлекает метаданные из нескольких бэкендов, подсвечивает цель на экране и строит селекторы/структурированные локаторы. Опциональная подсистема VCL интроспекции поддерживает Delphi/VCL приложения, включая контролы без HWND.

## Возможности

- Захват элемента по хоткею или в режиме drag-capture.
- Подсветка через прозрачный click-through overlay.
- Инспекция несколькими бэкендами:
  - Win32 (HWND, класс, текст, прямоугольник, hit-test).
  - UI Automation (свойства + цепочка родителей, best-effort).
  - MSAA/IAccessible (best-effort).
  - VCL (цепочка компонентов, свойства, hit-test, non-HWND) — при включении VCL.
- Генерация селекторов:
  - человекочитаемая строка для копирования,
  - структурированный локатор для хранения.
- Хранение локаторов в `locator.json` (версируемый формат).
- Логирование в локальный файл для диагностики.

## Типовые сценарии

- Быстро получить стабильный селектор и сохранить локатор.
- Разобрать сложную композицию UI (панели, тулбары, табы).
- Найти non-HWND контролы, которые рисуются родительским surface.
- Подготовить воспроизводимый кейс: локатор + лог.

## Как ориентироваться в репозитории

- UI и оркестрация:
  - `Spy.UI/MainWindow.xaml` и `Spy.UI/MainWindow.xaml.cs`
- Pipeline захвата и селекторы:
  - `Spy.Core/SpyCapture.cs`
  - `Spy.Core/SelectorBuilder.cs`
- VCL интроспекция:
  - `Spy.Core/Vcl/VclHookManager.cs`
  - `Spy.UI/VclHelperClient.cs`
  - `VclHook/VclHook.c` и `VclHook/VclBridge.c`
  - `VclHook/SpyderVclHelper32.dpr`

## Точки входа

- UI: `Spy.UI/App.xaml(.cs)`, `Spy.UI/MainWindow.xaml(.cs)`
- Core: `Spy.Core/SpyCapture.cs`, `Spy.Core/SelectorBuilder.cs`
- Native: `VclHook/VclHook.c:GetMsgHookProc`, `VclHook/VclHook.c:PipeThread`

## Онбординг разработчика

- Собери managed часть: `dotnet build .\Spy.UI\Spyder.csproj`
- Для VCL собери нативные x86 артефакты в `VclHook/` и положи рядом с опубликованным `Spyder.exe`.
- Включи “Log to file”, воспроизведи один захват, приложи лог к issue/PR.

## Типовой workflow

1. Запусти Spyder.
2. (Опционально) включи VCL и выбери процесс.
3. Наведение — подсветка и предпросмотр метаданных.
4. Захват — хоткей или drag.
5. Проверка селектора и свойств.
6. Сохранение/обновление записи в `locator.json`.
