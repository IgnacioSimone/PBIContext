using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PBIExplorer.Models;

namespace PBIExplorer.Services;

/// <summary>Todo lo que se exporta a JSON, con timestamp — sirve tanto para ingestión
/// por otra herramienta como para guardar un snapshot y compararlo después con
/// ModelDiffAnalyzer.</summary>
public class JsonSnapshotEnvelope
{
    public DateTime ExportedAtUtc { get; set; }
    public string SourceName { get; set; } = "";
    public ModelSnapshot Model { get; set; } = new();
    public List<VisualFieldUsage> Visuals { get; set; } = new();
    public List<ObjectUsage> Usage { get; set; } = new();
}

/// <summary>
/// Vuelca el modelo + visuales a un único .json, enmascarando el mismo texto sensible
/// que el resto de los exports (DAX, M, descripciones). A diferencia del Markdown
/// (pensado para pegarse en un chat), este formato es fácil de parsear por otra
/// herramienta sin heurísticas de texto — y sirve como snapshot para comparar contra
/// una versión posterior del mismo modelo.
/// </summary>
public static class JsonModelExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Export(string path, string sourceName, ModelSnapshot model,
        List<VisualFieldUsage> visuals, List<ObjectUsage>? usage = null)
    {
        var payload = new JsonSnapshotEnvelope
        {
            ExportedAtUtc = DateTime.UtcNow,
            SourceName = sourceName,
            Model = MaskModel(model),
            Visuals = visuals.Select(MaskVisual).ToList(),
            Usage = usage ?? new List<ObjectUsage>()
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, Options), new UTF8Encoding(false));
    }

    /// <summary>Lee un snapshot exportado antes, para compararlo con el modelo actual.</summary>
    public static JsonSnapshotEnvelope Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<JsonSnapshotEnvelope>(json, Options)
               ?? throw new InvalidDataException("El archivo no tiene el formato esperado de snapshot de PBI Context.");
    }

    private static ModelSnapshot MaskModel(ModelSnapshot model)
    {
        var masked = new ModelSnapshot();

        foreach (var t in model.Tables)
            masked.Tables.Add(new TableInfo
            {
                Name = t.Name, IsHidden = t.IsHidden, Description = SensitiveTextGuard.Mask(t.Description),
                ColumnCount = t.ColumnCount, MeasureCount = t.MeasureCount
            });

        foreach (var c in model.Columns)
            masked.Columns.Add(new ColumnInfo
            {
                Table = c.Table, Name = c.Name, DataType = c.DataType, ColumnType = c.ColumnType,
                IsHidden = c.IsHidden, SourceColumn = c.SourceColumn,
                Expression = SensitiveTextGuard.Mask(c.Expression), DisplayFolder = c.DisplayFolder,
                Description = SensitiveTextGuard.Mask(c.Description)
            });

        foreach (var m in model.Measures)
            masked.Measures.Add(new MeasureInfo
            {
                Table = m.Table, Name = m.Name, Expression = SensitiveTextGuard.Mask(m.Expression),
                DisplayFolder = m.DisplayFolder, FormatString = m.FormatString, IsHidden = m.IsHidden,
                Description = SensitiveTextGuard.Mask(m.Description)
            });

        foreach (var r in model.Relationships)
            masked.Relationships.Add(r); // sin texto libre sensible

        foreach (var pq in model.PowerQueries)
            masked.PowerQueries.Add(new PowerQueryInfo
            {
                Name = pq.Name, Kind = pq.Kind, MCode = SensitiveTextGuard.Mask(pq.MCode),
                IsHidden = pq.IsHidden, Description = SensitiveTextGuard.Mask(pq.Description)
            });

        return masked;
    }

    private static VisualFieldUsage MaskVisual(VisualFieldUsage v) => new()
    {
        Page = v.Page, VisualType = v.VisualType, Title = SensitiveTextGuard.Mask(v.Title),
        FieldKind = v.FieldKind, Table = v.Table, Field = v.Field
    };
}
