using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PBIExplorer.Models;

namespace PBIExplorer.Services;

/// <summary>
/// Un .pbix es un .zip. El reporte puede venir en dos formatos:
///   - PBIR (moderno): cada visual vive en su propio
///     Report/definition/pages/*/visuals/*/visual.json.
///   - Layout clásico: todo el reporte es un único archivo
///     Report/Layout, codificado en UTF-16LE, con un JSON por sección
///     y cada visual guardando su configuración en un campo "config"
///     que es, a su vez, un JSON serializado como string.
/// Esta clase detecta cuál de los dos hay y lee solo esa estructura
/// (qué visual usa qué medida o columna) directamente en memoria —
/// nunca toca DataModel (donde viven los datos reales) ni escribe nada a disco.
/// </summary>
public class PbixReportParser
{
    private static readonly HashSet<string> IgnoredVisualTypes = new()
    {
        "image", "textbox", "shape", "actionButton", "basicShape"
    };

    // Un solo visual.json o el Report/Layout entero jamás llegan a pesar esto en un
    // reporte real; es un tope de seguridad contra un .pbix armado a mano (zip-bomb)
    // que alguien podría elegir a través de "Abrir .pbix" — no solo el suyo propio.
    private const long MaxEntryBytes = 20_000_000;
    private const long MaxCompressionRatio = 100;

    /// <summary>Descarta una entrada si su tamaño declarado (o la relación entre
    /// tamaño comprimido/descomprimido) es la de una zip-bomb, sin necesidad de
    /// descomprimirla primero.</summary>
    private static bool IsSafeEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes) return false;
        if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio) return false;
        return true;
    }

    public List<VisualFieldUsage> ParseVisuals(string pbixPath)
    {
        using var fileStream = File.OpenRead(pbixPath);
        using var zip = new ZipArchive(fileStream, ZipArchiveMode.Read);

        var pbirResult = ParsePbir(zip);
        if (pbirResult.Count > 0) return pbirResult;

        return ParseLegacyLayout(zip);
    }

    // =====================================================================
    // Formato PBIR (moderno)
    // =====================================================================

    private static List<VisualFieldUsage> ParsePbir(ZipArchive zip)
    {
        var result = new List<VisualFieldUsage>();

        var pageEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("Report/definition/pages/", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var pageEntry in pageEntries)
        {
            var pageDir = pageEntry.FullName[..pageEntry.FullName.LastIndexOf('/')];

            // Una página con un page.json fuera de lo común no debe tirar abajo el resto
            // del reporte: si no se puede leer el nombre, se sigue con las demás páginas.
            string pageName;
            try { pageName = IsSafeEntry(pageEntry) ? ReadPbirPageName(pageEntry) : pageEntry.Name; }
            catch (JsonException) { pageName = pageEntry.Name; }

            var visualEntries = zip.Entries
                .Where(e => e.FullName.StartsWith(pageDir + "/visuals/", StringComparison.OrdinalIgnoreCase)
                            && e.FullName.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase));

            foreach (var visualEntry in visualEntries)
            {
                if (!IsSafeEntry(visualEntry)) continue;

                try
                {
                    using var visualStream = visualEntry.Open();
                    using var doc = JsonDocument.Parse(visualStream);
                    if (!doc.RootElement.TryGetProperty("visual", out var visual)) continue;

                    var visualType = visual.TryGetProperty("visualType", out var vt) ? vt.GetString() ?? "?" : "?";
                    if (IgnoredVisualTypes.Contains(visualType)) continue;

                    var title = ReadPbirTitle(visual);

                    var fields = new List<(string kind, string table, string field)>();
                    if (visual.TryGetProperty("query", out var query))
                    {
                        CollectFields(query, fields, null);
                    }

                    AddVisualRows(result, pageName, visualType, title, fields);
                }
                catch (JsonException)
                {
                    // Un visual.json con un esquema fuera de lo común (ej. un visual
                    // personalizado poco frecuente) no debe perder los demás visuales
                    // de páginas que sí se leen bien.
                }
            }
        }

        return result;
    }

    private static string ReadPbirPageName(ZipArchiveEntry pageEntry)
    {
        using var pageStream = pageEntry.Open();
        using var doc = JsonDocument.Parse(pageStream);
        if (doc.RootElement.TryGetProperty("displayName", out var dn)) return dn.GetString() ?? pageEntry.Name;
        if (doc.RootElement.TryGetProperty("name", out var nm)) return nm.GetString() ?? pageEntry.Name;
        return pageEntry.Name;
    }

    private static string? ReadPbirTitle(JsonElement visual)
    {
        if (!visual.TryGetProperty("visualContainerObjects", out var vco) ||
            !vco.TryGetProperty("title", out var titleArr) ||
            titleArr.ValueKind != JsonValueKind.Array) return null;

        string? title = null;
        foreach (var t in titleArr.EnumerateArray())
        {
            if (t.TryGetProperty("properties", out var props) &&
                props.TryGetProperty("text", out var text) &&
                text.TryGetProperty("expr", out var expr) &&
                expr.TryGetProperty("Literal", out var lit) &&
                lit.TryGetProperty("Value", out var val))
            {
                title = val.GetString()?.Trim('\'');
            }
        }
        return title;
    }

    // =====================================================================
    // Formato Layout clásico (Report/Layout, UTF-16LE, config anidado como string)
    // =====================================================================

    private static List<VisualFieldUsage> ParseLegacyLayout(ZipArchive zip)
    {
        var result = new List<VisualFieldUsage>();

        var layoutEntry = zip.GetEntry("Report/Layout");
        if (layoutEntry is null || !IsSafeEntry(layoutEntry)) return result;

        using var stream = layoutEntry.Open();
        using var reader = new StreamReader(stream, Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
        var json = reader.ReadToEnd();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("sections", out var sections)) return result;

        foreach (var section in sections.EnumerateArray())
        {
            var pageName = section.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "?"
                : section.TryGetProperty("name", out var nm) ? nm.GetString() ?? "?" : "?";

            if (!section.TryGetProperty("visualContainers", out var containers)) continue;

            foreach (var container in containers.EnumerateArray())
            {
                if (!container.TryGetProperty("config", out var configRaw) || configRaw.ValueKind != JsonValueKind.String)
                    continue;

                var configText = configRaw.GetString();
                if (string.IsNullOrEmpty(configText)) continue;

                try
                {
                    using var configDoc = JsonDocument.Parse(configText);
                    var config = configDoc.RootElement;

                    if (!config.TryGetProperty("singleVisual", out var singleVisual)) continue; // grupo, no un visual real

                    var visualType = singleVisual.TryGetProperty("visualType", out var vt) ? vt.GetString() ?? "?" : "?";
                    if (IgnoredVisualTypes.Contains(visualType)) continue;

                    var title = ReadLegacyTitle(singleVisual);
                    var aliasMap = BuildAliasMap(singleVisual);

                    var fields = new List<(string kind, string table, string field)>();
                    if (singleVisual.TryGetProperty("prototypeQuery", out var prototypeQuery) &&
                        prototypeQuery.TryGetProperty("Select", out var select))
                    {
                        CollectFields(select, fields, aliasMap);
                    }

                    AddVisualRows(result, pageName, visualType, title, fields);
                }
                catch (JsonException)
                {
                    // Un "config" con un esquema fuera de lo común no debe perder los
                    // demás visuales de esta misma página o de las otras páginas.
                }
            }
        }

        return result;
    }

    private static string? ReadLegacyTitle(JsonElement singleVisual)
    {
        if (!singleVisual.TryGetProperty("vcObjects", out var vco) &&
            !singleVisual.TryGetProperty("objects", out vco)) return null;

        if (!vco.TryGetProperty("title", out var titleArr) || titleArr.ValueKind != JsonValueKind.Array) return null;

        foreach (var t in titleArr.EnumerateArray())
        {
            if (t.TryGetProperty("properties", out var props) &&
                props.TryGetProperty("text", out var text) &&
                text.TryGetProperty("expr", out var expr) &&
                expr.TryGetProperty("Literal", out var lit) &&
                lit.TryGetProperty("Value", out var val))
            {
                return val.GetString()?.Trim('\'');
            }
        }
        return null;
    }

    /// <summary>El Layout clásico referencia tablas por un alias corto ("a","b"...) definido en prototypeQuery.From.</summary>
    private static Dictionary<string, string> BuildAliasMap(JsonElement singleVisual)
    {
        var map = new Dictionary<string, string>();
        if (!singleVisual.TryGetProperty("prototypeQuery", out var prototypeQuery) ||
            !prototypeQuery.TryGetProperty("From", out var from)) return map;

        foreach (var item in from.EnumerateArray())
        {
            var alias = item.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var entity = item.TryGetProperty("Entity", out var e) ? e.GetString() : null;
            if (alias is not null && entity is not null) map[alias] = entity;
        }
        return map;
    }

    // =====================================================================
    // Compartido
    // =====================================================================

    private static void AddVisualRows(List<VisualFieldUsage> result, string pageName, string visualType,
        string? title, List<(string kind, string table, string field)> fields)
    {
        if (fields.Count == 0)
        {
            result.Add(new VisualFieldUsage
            {
                Page = pageName, VisualType = visualType, Title = title ?? "",
                FieldKind = "", Table = "", Field = "(sin campos de datos)"
            });
            return;
        }

        foreach (var (kind, table, field) in fields.Distinct())
        {
            result.Add(new VisualFieldUsage
            {
                Page = pageName, VisualType = visualType, Title = title ?? "",
                FieldKind = kind, Table = table, Field = field
            });
        }
    }

    private static void CollectFields(JsonElement element, List<(string kind, string table, string field)> output,
        Dictionary<string, string>? aliasMap)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("Measure", out var measure))
                    output.Add(("Medida", ExtractTable(measure, aliasMap), ExtractProperty(measure)));
                if (element.TryGetProperty("Column", out var column))
                    output.Add(("Columna", ExtractTable(column, aliasMap), ExtractProperty(column)));
                foreach (var prop in element.EnumerateObject())
                    CollectFields(prop.Value, output, aliasMap);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectFields(item, output, aliasMap);
                break;
        }
    }

    private static string ExtractTable(JsonElement fieldObj, Dictionary<string, string>? aliasMap)
    {
        if (!fieldObj.TryGetProperty("Expression", out var expr) ||
            !expr.TryGetProperty("SourceRef", out var sourceRef)) return "?";

        // PBIR: {"SourceRef":{"Entity":"Tabla"}}
        if (sourceRef.TryGetProperty("Entity", out var entity)) return entity.GetString() ?? "?";

        // Layout clásico: {"SourceRef":{"Source":"a"}} — "a" se resuelve contra prototypeQuery.From
        if (sourceRef.TryGetProperty("Source", out var source))
        {
            var alias = source.GetString() ?? "?";
            return aliasMap is not null && aliasMap.TryGetValue(alias, out var table) ? table : alias;
        }

        return "?";
    }

    private static string ExtractProperty(JsonElement fieldObj) =>
        fieldObj.TryGetProperty("Property", out var propEl) ? propEl.GetString() ?? "?" : "?";
}
