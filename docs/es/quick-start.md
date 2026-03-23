# Inicio rápido

Escenario mínimo: ejecutar → capturar → copiar selector → guardar locator.

## 1) Ejecutar

Ejecuta `Spyder.exe`.

## 2) (Opcional) Activar VCL

Si necesitas inspección a nivel de componentes:
1. Activa VCL en la UI.
2. Selecciona el proceso objetivo.
3. Espera el estado de conexión.

Sin VCL, Spyder opera vía Win32/UIA/MSAA.

## 3) Hover

Mueve el cursor sobre un elemento:
- se dibuja el overlay de resaltado,
- se actualizan los paneles con metadatos.

## 4) Captura

Usa:
- hotkey (si está configurado),
- o modo drag-capture.

Tras capturar se actualiza:
- jerarquía,
- propiedades,
- selector.

## 5) Copiar selector

Usa la acción de copiar junto al campo de selector.

## 6) Guardar en `locator.json`

1. Abre el gestor de locators.
2. Selecciona la ruta del archivo.
3. Añade/actualiza la entrada.

## Logs

`%LOCALAPPDATA%\Spyder\logs\spyder.log`
