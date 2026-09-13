using PBIExplorer.Models;

namespace PBIExplorer.Services;

public enum DiffKind { Agregado, Eliminado, Modificado }

public class ModelDiffEntry
{
    public DiffKind Kind { get; set; }
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";

    public string KindText => Kind switch
    {
        DiffKind.Agregado => "Agregado",
        DiffKind.Eliminado => "Eliminado",
        _ => "Modificado"
    };
}

/// <summary>
/// Compara dos snapshots del mismo modelo (por ejemplo, un .json exportado hace un
/// tiempo vs. el modelo conectado ahora) para ver qué cambió entre versiones —
/// pensado para auditar ediciones de un tablero que ya conocés, no para diffear dos
/// modelos distintos entre sí (los nombres de tabla/medida/columna son la clave de
/// comparación, así que un simple renombre se ve como "eliminado" + "agregado").
/// </summary>
public static class ModelDiffAnalyzer
{
    public static List<ModelDiffEntry> Compare(ModelSnapshot older, ModelSnapshot newer)
    {
        var entries = new List<ModelDiffEntry>();

        CompareByKey(entries, "Tabla",
            ByKeyFirstWins(older.Tables, t => t.Name),
            ByKeyFirstWins(newer.Tables, t => t.Name),
            (a, b) =>
            {
                var changes = new List<string>();
                if (a.IsHidden != b.IsHidden) changes.Add(b.IsHidden ? "ahora oculta" : "ahora visible");
                if (a.Description != b.Description) changes.Add("cambió la descripción");
                return changes;
            });

        CompareByKey(entries, "Medida",
            ByKeyFirstWins(older.Measures, m => $"{m.Table}[{m.Name}]"),
            ByKeyFirstWins(newer.Measures, m => $"{m.Table}[{m.Name}]"),
            (a, b) =>
            {
                var changes = new List<string>();
                if (a.Expression != b.Expression) changes.Add("cambió el DAX");
                if (a.FormatString != b.FormatString) changes.Add($"formato \"{a.FormatString}\" → \"{b.FormatString}\"");
                if (a.IsHidden != b.IsHidden) changes.Add(b.IsHidden ? "ahora oculta" : "ahora visible");
                if (a.DisplayFolder != b.DisplayFolder) changes.Add($"carpeta \"{a.DisplayFolder}\" → \"{b.DisplayFolder}\"");
                return changes;
            });

        CompareByKey(entries, "Columna",
            ByKeyFirstWins(older.Columns, c => $"{c.Table}[{c.Name}]"),
            ByKeyFirstWins(newer.Columns, c => $"{c.Table}[{c.Name}]"),
            (a, b) =>
            {
                var changes = new List<string>();
                if (a.DataType != b.DataType) changes.Add($"tipo de dato \"{a.DataType}\" → \"{b.DataType}\"");
                if (a.ColumnType != b.ColumnType) changes.Add($"tipo de columna \"{a.ColumnType}\" → \"{b.ColumnType}\"");
                if (a.Expression != b.Expression) changes.Add("cambió el DAX de la columna calculada");
                if (a.IsHidden != b.IsHidden) changes.Add(b.IsHidden ? "ahora oculta" : "ahora visible");
                return changes;
            });

        CompareByKey(entries, "Relación",
            ByKeyFirstWins(older.Relationships, r => $"{r.FromTable}[{r.FromColumn}]→{r.ToTable}[{r.ToColumn}]"),
            ByKeyFirstWins(newer.Relationships, r => $"{r.FromTable}[{r.FromColumn}]→{r.ToTable}[{r.ToColumn}]"),
            (a, b) =>
            {
                var changes = new List<string>();
                if (a.Cardinality != b.Cardinality) changes.Add($"cardinalidad \"{a.Cardinality}\" → \"{b.Cardinality}\"");
                if (a.CrossFilterBehavior != b.CrossFilterBehavior) changes.Add($"filtro cruzado \"{a.CrossFilterBehavior}\" → \"{b.CrossFilterBehavior}\"");
                if (a.IsActive != b.IsActive) changes.Add(b.IsActive ? "ahora activa" : "ahora inactiva");
                return changes;
            });

        CompareByKey(entries, "Power Query",
            ByKeyFirstWins(older.PowerQueries, p => p.Name),
            ByKeyFirstWins(newer.PowerQueries, p => p.Name),
            (a, b) =>
            {
                var changes = new List<string>();
                if (a.MCode != b.MCode) changes.Add("cambió el código M");
                return changes;
            });

        return entries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Category)
            .ThenBy(e => e.Name)
            .ToList();
    }

    /// <summary>Como Dictionary.ToDictionary, pero si hay una clave repetida (no debería
    /// pasar en un modelo válido, pero esta herramienta no controla eso) se queda con la
    /// primera en vez de tirar una excepción y arruinar toda la comparación.</summary>
    private static Dictionary<string, T> ByKeyFirstWins<T>(IEnumerable<T> items, Func<T, string> keySelector) =>
        items.GroupBy(keySelector).ToDictionary(g => g.Key, g => g.First());

    private static void CompareByKey<T>(
        List<ModelDiffEntry> entries, string category,
        Dictionary<string, T> older, Dictionary<string, T> newer,
        Func<T, T, List<string>> diffFields)
    {
        foreach (var key in newer.Keys.Except(older.Keys))
            entries.Add(new ModelDiffEntry { Kind = DiffKind.Agregado, Category = category, Name = key, Detail = "nuevo en esta versión" });

        foreach (var key in older.Keys.Except(newer.Keys))
            entries.Add(new ModelDiffEntry { Kind = DiffKind.Eliminado, Category = category, Name = key, Detail = "ya no existe en esta versión" });

        foreach (var key in newer.Keys.Intersect(older.Keys))
        {
            var changes = diffFields(older[key], newer[key]);
            if (changes.Count > 0)
                entries.Add(new ModelDiffEntry { Kind = DiffKind.Modificado, Category = category, Name = key, Detail = string.Join("; ", changes) });
        }
    }
}
