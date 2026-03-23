# Архитектура

Spyder — слоистая система: managed приложение (WPF + .NET) + опциональная нативная подсистема VCL интроспекции внутри целевого процесса. Архитектурный принцип — best-effort: при отказе одного бэкенда захват продолжается по оставшимся.

## Слои

## UI Layer (Spy.UI)

Отвечает за:
- WPF UI и сценарии захвата (hover/drag/hotkey).
- Отрисовку overlay-подсветки.
- Отображение метаданных по бэкендам и селекторов.
- Управление `locator.json` и пользовательскими действиями.
- Управление локальным логом.

Ключевые файлы:
- `Spy.UI/MainWindow.xaml(.cs)`
- `Spy.UI/OverlayWindow.xaml(.cs)`
- `Spy.UI/GlobalMouseHook.cs`
- `Spy.UI/LocatorStorage.cs`

## Core Layer (Spy.Core)

Отвечает за:
- Pipeline захвата и базовые модели.
- Построение селектора и структурированного локатора.
- Таймауты и устойчивость бэкендов.
- Установку/остановку VCL хука.

Ключевые файлы:
- `Spy.Core/SpyCapture.cs`
- `Spy.Core/SelectorBuilder.cs`
- `Spy.Core/Hierarchy.cs`
- `Spy.Core/Vcl/VclHookManager.cs`
- `Spy.Core/Vcl/VclLocatorEngine.cs`

## Hook Layer (VclHook)

Отвечает за:
- работу внутри целевого процесса,
- Named Pipe сервер (RPC),
- выполнение VCL операций строго в UI-потоке цели,
- hit-test, обход дерева, свойства,
- интеграцию Delphi helper DLL.

Ключевые файлы:
- `VclHook/VclHook.c`
- `VclHook/VclBridge.c` / `VclHook/VclBridge.h`
- `VclHook/SpyderVclHelper32.dpr`

## Схема взаимодействия (упрощенно)

```text
Spy.UI → Spy.Core → (опционально) VclHookManager
  └→ VclHelperClient (pipe) ↔ VclHook (in-process server) → VclBridge/Helper (UI thread)
```

## Поток данных

## Hover (быстро)
1. UI читает позицию курсора.
2. Win32 снимок дает HWND и прямоугольник.
3. Overlay рисует подсветку.
4. При активном VCL — облегченный VCL hit-test может уточнить bounds.

## Capture (полный)
1. Пользователь инициирует захват.
2. `SpyCapture` собирает:
   - Win32 (всегда),
   - UIA (с таймаутом),
   - MSAA (с таймаутом).
3. Если VCL подключен:
   - строится VCL цепочка по точке,
   - (опционально) подтягиваются свойства leaf.
4. UI строит селектор/локатор и показывает свойства.
5. При необходимости запись сохраняется в `locator.json`.

## Потоки

- UI поток Spyder: WPF и рендер.
- Фоновые задачи Spyder: UIA/MSAA (таймауты).
- UI поток цели: единственное безопасное место для VCL вызовов (через dispatch).
