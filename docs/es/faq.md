# FAQ

## No encuentra el elemento en hover

Posibles causas:
- UI compuesto (surface) sin hijos visibles en UIA/MSAA.
- Hit-test Win32 devuelve solo un contenedor HWND.
- Elemento transitorio durante hover.

Recomendaciones:
- Usa captura (hotkey/drag), no solo hover.
- Activa VCL si aplica.
- Habilita logs y revisa qué comando falla.

## El control no tiene HWND

Algunos controles son non-HWND y los pinta el contenedor. Para ellos se requiere introspección específica (por ejemplo VCL cuando aplica).

## Cadena VCL incompleta

Razones comunes:
- El hilo UI del objetivo no bombea mensajes (dispatch no corre).
- El árbol cambia durante la resolución.
- Se aplicaron límites de seguridad.

## Dónde están los logs

- `%LOCALAPPDATA%\Spyder\logs\spyder.log`

## Publish “Access denied”

El ejecutable puede estar en uso. Detén el proceso, limpia publish y repite (ver `docs/es/installation.md`).
