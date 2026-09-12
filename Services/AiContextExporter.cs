using System.IO;
using System.Text;
using PBIExplorer.Models;

namespace PBIExplorer.Services;

/// <summary>
/// Vuelca toda la metadata leída (modelo + reporte) a un único archivo Markdown,
/// pensado para pegarlo o adjuntarlo como contexto en un chat con Claude/ChatGPT.
/// Markdown es el formato que mejor parsean los LLM: texto plano, sin XML ni
/// binarios de por medio, con tablas y bloques de código nativos. No contiene
/// datos reales — solo estructura (tablas, DAX, relaciones, visuales y campos).
/// </summary>
public static class AiContextExporter
{
    public static void Export(
        string path,
        string sourceName,
        ModelSnapshot model,
        List<VisualFieldUsage> visuals,
        DashboardSummary summary,
        List<ObjectUsage>? usage = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Tablero de Power BI — {sourceName}");
        sb.AppendLine();
        sb.AppendLine("Este documento describe la estructura completa de un tablero de Power BI " +
                       "(tablas, medidas DAX, relaciones, y qué visual de cada página usa qué campo) " +
                       "para que una IA pueda analizarlo y responder preguntas sobre su diseño, calidad " +
                       "del modelo o cobertura de datos. **No contiene datos reales ni valores de fila " +
                       "alguna** — solo metadata (nombres, tipos, fórmulas, relaciones).");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(summary.OverviewText))
        {
            sb.AppendLine("## Resumen");
            sb.AppendLine();
            sb.AppendLine(summary.OverviewText);
            sb.AppendLine();

            if (summary.Highlights.Count > 0)
            {
                sb.AppendLine("### Puntos destacados");
                sb.AppendLine();
                foreach (var h in summary.Highlights) sb.AppendLine($"- {h}");
                sb.AppendLine();
            }
        }

        if (model.Tables.Count > 0)
        {
            sb.AppendLine("## Modelo de datos");
            sb.AppendLine();
            sb.AppendLine("### Tablas");
            sb.AppendLine();
            sb.AppendLine("| Tabla | Columnas | Medidas | Oculta | Descripción |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var t in model.Tables)
                sb.AppendLine($"| {Esc(t.Name)} | {t.ColumnCount} | {t.MeasureCount} | {(t.IsHidden ? "sí" : "no")} | {Esc(SensitiveTextGuard.Mask(t.Description))} |");
            sb.AppendLine();

            if (model.Columns.Count > 0)
            {
                sb.AppendLine("### Columnas por tabla");
                sb.AppendLine();
                foreach (var tableGroup in model.Columns.GroupBy(c => c.Table))
                {
                    sb.AppendLine($"**{tableGroup.Key}**");
                    sb.AppendLine();
                    sb.AppendLine("| Columna | Tipo de dato | Origen | Oculta |");
                    sb.AppendLine("|---|---|---|---|");
                    foreach (var c in tableGroup)
                        sb.AppendLine($"| {Esc(c.Name)} | {Esc(c.DataType)} | {Esc(c.ColumnType)} | {(c.IsHidden ? "sí" : "no")} |");
                    sb.AppendLine();

                    var calculated = tableGroup.Where(c => !string.IsNullOrWhiteSpace(c.Expression)).ToList();
                    foreach (var c in calculated)
                    {
                        sb.AppendLine($"Columna calculada `{tableGroup.Key}[{c.Name}]`:");
                        sb.AppendLine("```dax");
                        sb.AppendLine(SensitiveTextGuard.Mask(c.Expression));
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }
                }
            }

            if (model.Measures.Count > 0)
            {
                sb.AppendLine("### Medidas DAX");
                sb.AppendLine();
                foreach (var tableGroup in model.Measures.GroupBy(m => m.Table))
                {
                    sb.AppendLine($"**{tableGroup.Key}**");
                    sb.AppendLine();
                    foreach (var m in tableGroup)
                    {
                        var folder = string.IsNullOrWhiteSpace(m.DisplayFolder) ? "" : $" _(carpeta: {m.DisplayFolder})_";
                        var hidden = m.IsHidden ? " · oculta" : "";
                        sb.AppendLine($"- `{m.Name}`{folder}{hidden}");
                        sb.AppendLine("  ```dax");
                        foreach (var line in SensitiveTextGuard.Mask(m.Expression).Replace("\r\n", "\n").Split('\n'))
                            sb.AppendLine($"  {line}");
                        sb.AppendLine("  ```");
                    }
                    sb.AppendLine();
                }
            }

            if (model.Relationships.Count > 0)
            {
                sb.AppendLine("### Relaciones");
                sb.AppendLine();
                sb.AppendLine("| Desde | Hacia | Cardinalidad | Filtro cruzado | Activa |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var r in model.Relationships)
                    sb.AppendLine($"| {Esc(r.FromTable)}.{Esc(r.FromColumn)} | {Esc(r.ToTable)}.{Esc(r.ToColumn)} | {r.Cardinality} | {r.CrossFilterBehavior} | {(r.IsActive ? "sí" : "no")} |");
                sb.AppendLine();
            }

            if (model.PowerQueries.Count > 0)
            {
                sb.AppendLine("### Power Query (M)");
                sb.AppendLine();
                foreach (var pq in model.PowerQueries)
                {
                    sb.AppendLine($"**{pq.Kind}: {pq.Name}**");
                    if (!string.IsNullOrWhiteSpace(pq.Description)) sb.AppendLine($"_{Esc(SensitiveTextGuard.Mask(pq.Description))}_");
                    sb.AppendLine("```m");
                    sb.AppendLine(SensitiveTextGuard.Mask(pq.MCode));
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        if (visuals.Count > 0)
        {
            sb.AppendLine("## Estructura del reporte (páginas y visuales)");
            sb.AppendLine();
            foreach (var pageGroup in visuals.GroupBy(v => v.Page))
            {
                sb.AppendLine($"### {pageGroup.Key}");
                sb.AppendLine();

                var pageSummary = summary.Pages.FirstOrDefault(p => p.PageName == pageGroup.Key);
                if (pageSummary is not null) sb.AppendLine(pageSummary.Description).AppendLine();

                sb.AppendLine("| Visual | Título | Tipo de campo | Tabla | Campo |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var v in pageGroup)
                    sb.AppendLine($"| {Esc(v.VisualType)} | {Esc(SensitiveTextGuard.Mask(v.Title))} | {Esc(v.FieldKind)} | {Esc(v.Table)} | {Esc(v.Field)} |");
                sb.AppendLine();
            }
        }

        var unused = usage?.Where(u => u.IsUnused).ToList();
        if (unused is { Count: > 0 })
        {
            sb.AppendLine("## Objetos sin uso detectado (candidatos a limpieza)");
            sb.AppendLine();
            sb.AppendLine("Cruzando qué campo usa cada visual del reporte, qué fórmula DAX depende de qué otra, " +
                          "y qué columnas sostienen una relación, estos objetos no aparecen usados en ningún lado. " +
                          "**No sigue dependencias de Power Query entre consultas (M)** — una tabla auxiliar que solo " +
                          "alimenta a otra vía M puede figurar acá sin serlo de verdad. Antes de borrar algo, confirmá " +
                          "que no lo use tampoco una segmentación, un marcador, o Q&A en lenguaje natural.");
            sb.AppendLine();
            sb.AppendLine("| Tipo | Tabla | Nombre |");
            sb.AppendLine("|---|---|---|");
            foreach (var u in unused)
                sb.AppendLine($"| {Esc(u.Kind)} | {Esc(u.Table)} | {Esc(u.Name)} |");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Escapa el pipe para no romper las tablas Markdown.</summary>
    private static string Esc(string? value) => (value ?? "").Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
}
