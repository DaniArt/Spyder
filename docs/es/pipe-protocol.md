# Protocolo Named Pipe

Spyder utiliza Named Pipe RPC para comunicación managed ↔ in-process cuando la introspección VCL está habilitada.

## Pipe

`\\.\pipe\Spyder.VclHelper.<pid>`

## Framing

- `int32 length` (little-endian)
- JSON UTF-8 con `length` bytes

## Campos

Request:
- `cmd`
- campos específicos (`hwnd`, `self`, `x`, `y`)

Response:
- `ok`
- payload
- `error` en caso de fallo

## Comandos comunes

- `resolve_by_hwnd`
- `hit_test`, `hit_test_overlay`
- `get_children`
- `get_path`
- `get_properties`
- `get_full_properties`
- `shutdown`

## Timeouts

El cliente aplica timeouts para evitar bloqueos del hilo UI del objetivo. En timeout, la capa VCL se trata como best-effort no disponible para esa captura.
