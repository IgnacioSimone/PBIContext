using System.Text;
using System.Text.RegularExpressions;
using PBIExplorer.Models;

namespace PBIExplorer.Services;

/// <summary>
/// Arma una descripción en español de qué muestra el tablero, basada 100% en
/// reglas sobre la metadata ya leída (nombres de páginas, visuales, medidas,
/// columnas y relaciones). No usa IA ni sale a internet: es puro análisis
/// heurístico de texto y conteos, igual que el resto de la app.
/// </summary>
public static class SummaryBuilder
{
    private static readonly Regex CustomVisualGuidSuffix = new(@"[0-9A-Fa-f]{16,}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> FriendlyVisualNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["card"] = "tarjeta", ["cardVisual"] = "tarjeta", ["multiRowCard"] = "tarjetas múltiples",
        ["slicer"] = "segmentador", ["tableEx"] = "tabla", ["pivotTable"] = "tabla dinámica",
        ["matrix"] = "matriz", ["columnChart"] = "gráfico de columnas", ["clusteredColumnChart"] = "gráfico de columnas",
        ["barChart"] = "gráfico de barras", ["clusteredBarChart"] = "gráfico de barras",
        ["lineChart"] = "gráfico de líneas", ["areaChart"] = "gráfico de área",
        ["pieChart"] = "gráfico circular", ["donutChart"] = "gráfico de dona",
        ["funnel"] = "embudo", ["gauge"] = "medidor", ["scatterChart"] = "dispersión",
        ["treemap"] = "árbol jerárquico", ["map"] = "mapa", ["filledMap"] = "mapa coroplético",
        ["pageNavigator"] = "navegador de páginas", ["bookmarkNavigator"] = "navegador de marcadores",
        ["textbox"] = "texto", ["image"] = "imagen", ["shape"] = "forma", ["actionButton"] = "botón",
        ["waterfallChart"] = "cascada", ["ribbonChart"] = "cinta", ["keyDriversVisual"] = "impulsores clave",
        ["decompositionTreeVisual"] = "árbol de descomposición", ["qnaVisual"] = "preguntas y respuestas"
    };

    public static DashboardSummary Build(ModelSnapshot model, List<VisualFieldUsage> visuals)
    {
        var summary = new DashboardSummary();

        BuildOverview(model, visuals, summary);
        BuildHighlights(model, summary);
        BuildPages(visuals, summary);

        return summary;
    }

    private static void BuildOverview(ModelSnapshot model, List<VisualFieldUsage> visuals, DashboardSummary summary)
    {
        var sb = new StringBuilder();
        var hasModel = model.Tables.Count > 0 || model.Measures.Count > 0;
        var hasVisuals = visuals.Count > 0;

        if (hasModel)
        {
            var visibleTables = model.Tables.Count(t => !t.IsHidden);
            sb.Append($"Este modelo tiene {model.Tables.Count} tablas ({visibleTables} visibles), ");
            sb.Append($"{model.Measures.Count} medidas y {model.Relationships.Count} relaciones entre tablas. ");
        }

        if (hasVisuals)
        {
            var pages = visuals.Select(v => v.Page).Distinct().Count();
            var visualCount = visuals.Select(v => (v.Page, v.VisualType, v.Title)).Distinct().Count();
            sb.Append($"El reporte tiene {pages} página{Plural(pages)} con, en total, aproximadamente {visualCount} visuales.");
        }

        if (!hasModel && !hasVisuals)
        {
            sb.Append("Todavía no hay nada cargado — conectate a un modelo desde Power BI Desktop y/o abrí el archivo .pbix.");
        }

        summary.OverviewText = sb.ToString().Trim();
    }

    private static void BuildHighlights(ModelSnapshot model, DashboardSummary summary)
    {
        if (model.Tables.Count == 0) return;

        var busiest = model.Tables.OrderByDescending(t => t.MeasureCount).FirstOrDefault(t => t.MeasureCount > 0);
        if (busiest is not null)
            summary.Highlights.Add($"La tabla con más medidas es \"{busiest.Name}\" ({busiest.MeasureCount} medidas) — probablemente el centro de la lógica de negocio del modelo.");

        var richest = model.Tables.OrderByDescending(t => t.ColumnCount).FirstOrDefault(t => t.ColumnCount > 0);
        if (richest is not null && richest.Name != busiest?.Name)
            summary.Highlights.Add($"La tabla con más columnas es \"{richest.Name}\" ({richest.ColumnCount} columnas).");

        if (model.Relationships.Count > 0)
        {
            var connections = model.Relationships
                .SelectMany(r => new[] { r.FromTable, r.ToTable })
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (connections is not null)
                summary.Highlights.Add($"\"{connections.Key}\" es la tabla más conectada del modelo ({connections.Count()} relaciones) — parece ser la tabla central/hechos.");

            var referencedTables = model.Relationships.SelectMany(r => new[] { r.FromTable, r.ToTable }).ToHashSet();
            var orphanCount = model.Tables.Count(t => !t.IsHidden && !referencedTables.Contains(t.Name));
            if (orphanCount > 0)
                summary.Highlights.Add($"{orphanCount} tabla{Plural(orphanCount)} visible{Plural(orphanCount)} no tiene{Plural(orphanCount, "n")} ninguna relación — puede{Plural(orphanCount, "n")} ser tablas de parámetros, desconectadas a propósito, o resabios sin usar.");
        }

        var hiddenMeasures = model.Measures.Count(m => m.IsHidden);
        if (hiddenMeasures > 0)
            summary.Highlights.Add($"{hiddenMeasures} medida{Plural(hiddenMeasures)} está{Plural(hiddenMeasures, "n")} oculta{Plural(hiddenMeasures)} — normalmente son cálculos intermedios usados por otras medidas, no pensados para mostrarse directamente.");
    }

    private static void BuildPages(List<VisualFieldUsage> visuals, DashboardSummary summary)
    {
        foreach (var group in visuals.GroupBy(v => v.Page))
        {
            var rows = group.ToList();
            var distinctVisuals = rows.Select(r => (r.VisualType, r.Title)).Distinct().ToList();

            var page = new PageSummary
            {
                PageName = group.Key,
                VisualCount = distinctVisuals.Count
            };

            var measures = rows.Where(r => r.FieldKind == "Medida").Select(r => r.Field).Distinct().Take(6).ToList();
            var slicerColumns = rows.Where(r => r.FieldKind == "Columna" && r.VisualType.Equals("slicer", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Field).Distinct().Take(8).ToList();
            var tables = rows.Select(r => r.Table).Where(t => t != "?" && t != "").Distinct().Take(6).ToList();

            page.KeyMeasures.AddRange(measures);
            page.FilterColumns.AddRange(slicerColumns);
            page.SourceTables.AddRange(tables);

            var typeBreakdown = distinctVisuals
                .Select(v => FriendlyName(v.VisualType))
                .GroupBy(n => n)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Count() > 1 ? $"{g.Count()} {g.Key}s" : $"{g.Count()} {g.Key}")
                .ToList();
            page.VisualTypeBreakdown = string.Join(", ", typeBreakdown);

            page.Description = BuildPageSentence(page, typeBreakdown);

            summary.Pages.Add(page);
        }
    }

    private static string BuildPageSentence(PageSummary page, List<string> typeBreakdown)
    {
        var sb = new StringBuilder();
        sb.Append($"Tiene {page.VisualCount} visual{Plural(page.VisualCount)}");
        if (typeBreakdown.Count > 0) sb.Append($" ({string.Join(", ", typeBreakdown)})");
        sb.Append('.');

        if (page.KeyMeasures.Count > 0)
            sb.Append($" Muestra principalmente: {string.Join(", ", page.KeyMeasures)}.");

        if (page.FilterColumns.Count > 0)
            sb.Append($" Se puede filtrar por: {string.Join(", ", page.FilterColumns)}.");

        if (page.SourceTables.Count > 0)
            sb.Append($" Usa datos de: {string.Join(", ", page.SourceTables)}.");

        return sb.ToString();
    }

    private static string FriendlyName(string visualType)
    {
        if (FriendlyVisualNames.TryGetValue(visualType, out var friendly)) return friendly;

        // Los visuales custom de Power BI traen un GUID pegado al nombre (ej. sccWorkspaceBarChart1F7D...).
        var stripped = CustomVisualGuidSuffix.Replace(visualType, "");
        return string.IsNullOrWhiteSpace(stripped) ? "visual personalizado" : $"visual personalizado \"{stripped}\"";
    }

    private static string Plural(int count, string suffix = "s") => count == 1 ? "" : suffix;
}
