# Протокол Named Pipe

Spyder использует Named Pipe RPC для связи managed ↔ in-process слоя при включенной VCL интроспекции.

## Имя pipe

Сервер внутри процесса создаёт:

`\\.\pipe\Spyder.VclHelper.<pid>`

Где `<pid>` — PID целевого процесса.

## Фрейминг

- `int32 length` (little-endian)
- `length` байт JSON (UTF-8)

Ответ — в том же формате.

## Поля

Запрос:
- `cmd`: имя команды
- дополнительные поля (`hwnd`, `self`, `x`, `y`)

Ответ:
- `ok`: bool
- payload по команде
- `error`: строка при ошибке

## Команды

## `resolve_by_hwnd`
```json
{ "cmd": "resolve_by_hwnd", "hwnd": 123456 }
```

## `hit_test` / `hit_test_overlay`
```json
{ "cmd": "hit_test_overlay", "hwnd": 123456, "self": "0x0123ABCD", "x": 100, "y": 200 }
```

## `get_children`
```json
{ "cmd": "get_children", "self": "0x0456CDEF" }
```

## `get_path`
```json
{ "cmd": "get_path", "self": "0x0456CDEF" }
```

## `get_properties`
```json
{ "cmd": "get_properties", "self": "0x0456CDEF" }
```

## `get_full_properties`
Расширенные метаданные (implementation-defined).

## `shutdown`
Запрос завершения сервера (best-effort).

## Таймауты

Клиент применяет таймауты на connect/запрос, чтобы не блокировать UI поток цели. На таймауте VCL слой считается недоступным для конкретного захвата, и pipeline продолжается по остальным бэкендам.
