# Internos VCL

Este documento describe el subsistema opcional de introspección VCL: hit-test, cadenas y propiedades para controles VCL, incluyendo non-HWND.

## Por qué VCL requiere una ruta específica

En VCL pueden existir controles que:
- no tienen un HWND dedicado,
- no aparecen como elementos UIA individuales,
- requieren acceso a estructuras runtime para resolver cadenas con fiabilidad.

## UI thread only

Las operaciones VCL deben ejecutarse en el hilo UI del proceso objetivo. Spyder lo asegura mediante:
- un hook WH_GETMESSAGE en el GUI thread del objetivo,
- un mensaje de dispatch,
- ejecución dentro del message loop cuando el objetivo bombea mensajes.

## Subcomponentes

## `VclHook`
- Servidor Named Pipe dentro del proceso objetivo.
- JSON RPC + dispatch al hilo UI.
- Validación de punteros y guards de excepciones.

## `VclBridge`
- Hit-test.
- Enumeración de hijos y path.
- Extracción de propiedades.
- Serialización a JSON.

## `SpyderVclHelper32`
- Helper Delphi in-process para acceder a APIs VCL de forma segura:
  - `ControlAtPos`
  - `FindDragTarget`
  - transformaciones de coordenadas para bounds.

## Non-HWND

Los controles non-HWND se identifican por el árbol VCL (no por enumeración Win32). Bounds se calculan best-effort usando transformaciones `ClientToScreen` y tamaños del control.

## Reglas de seguridad

- Validar punteros antes de leer.
- Envolver áreas riesgosas con guards.
- Limitar profundidad y número de nodos visitados.
- Degradar a best-effort si algo falla.
