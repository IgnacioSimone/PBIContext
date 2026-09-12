# PBI Context

Herramienta externa para Power BI Desktop que documenta un modelo (medidas DAX, tablas,
columnas, relaciones, consultas Power Query) y su reporte (qué visual usa qué campo, en
qué página), con exportación a CSV, Word y Markdown para IA.

**Este proyecto no es un producto de Microsoft ni está afiliado, patrocinado o
respaldado por Microsoft Corporation.** "Power BI" es una marca registrada de
Microsoft. Se usa aquí únicamente para describir la compatibilidad de esta
herramienta, tal como lo hacen otras herramientas externas de la comunidad
(DAX Studio, Tabular Editor, ALM Toolkit).

## Qué lee y qué no lee (privacidad)

- Se conecta al modelo que Power BI Desktop ya tiene abierto en memoria, usando el
  mecanismo oficial de [herramientas externas](https://learn.microsoft.com/power-bi/transform-model/desktop-external-tools).
- Solo consulta las vistas de metadata del motor (`$SYSTEM.TMSCHEMA_*`): nombres de
  tablas/columnas/medidas, fórmulas DAX, código M de Power Query, relaciones. **Nunca
  ejecuta una consulta `EVALUATE` ni de ningún otro tipo sobre las tablas de datos** —
  no hay forma de que esta herramienta lea una fila real de tu tablero.
- Para leer visuales y qué campo usa cada uno, abre el archivo `.pbix` como un zip y lee
  únicamente la definición del reporte (`Report/definition` o `Report/Layout`), nunca los
  datos del modelo empaquetados adentro.
- Para encontrar automáticamente el `.pbix` abierto, busca por nombre de archivo en el
  Escritorio, Documentos, OneDrive y el resto de tus discos locales. No sube nada a
  ningún servidor: toda la búsqueda y lectura ocurre en tu máquina.
- El código DAX/M que se exporta pasa por un enmascarado automático de patrones
  sensibles (emails, DNI, CUIT/CUIL, contraseñas/tokens en cadenas de conexión, rutas
  locales que revelan tu usuario de Windows) antes de escribirse en el CSV/Word/Markdown.
  Aun así, revisá el archivo exportado antes de compartirlo — ninguna heurística
  automática reemplaza una revisión humana si tu modelo tiene texto libre con datos
  personales embebidos a mano en una fórmula.
- No hay telemetría, no hay llamadas de red, no hay actualizaciones automáticas.

## Instalación

1. Descargá o compilá la app (`dotnet publish -c Release -r win-x64 --self-contained true -o publish`).
2. Ejecutá `install.ps1` (te pedirá permisos de administrador la primera vez, porque
   Power BI Desktop solo lee herramientas externas registradas en una carpeta de
   sistema). El script copia la app a tu perfil de usuario y registra el acceso directo
   en Power BI Desktop — no requiere reinstalar Power BI ni tocar nada más.
3. Abrí cualquier tablero en Power BI Desktop → pestaña **Herramientas externas** →
   **PBI Context**.

## Licencia y dependencias de terceros

Este proyecto se distribuye bajo licencia MIT (ver `LICENSE`) — sin garantía, "tal cual".

Usa dos componentes de terceros, redistribuidos dentro de la publicación self-contained:

- **DocumentFormat.OpenXml** (Microsoft, licencia MIT) — generación del documento Word.
- **Microsoft.AnalysisServices.AdomdClient** (Microsoft, EULA de redistribución del SQL
  Server Feature Pack) — cliente para conectarse al motor analítico local que expone
  Power BI Desktop. Es el mismo componente que redistribuyen otras herramientas
  públicas de la comunidad (DAX Studio, Tabular Editor) por el mismo motivo.

## Alcance

Uso previsto: analistas y desarrolladores de Power BI documentando **sus propios**
modelos. No está pensado para inspeccionar tableros ajenos sin autorización, ni para
extraer datos personales — por diseño, no puede hacer esto último.
