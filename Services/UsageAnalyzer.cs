using PBIExplorer.Models;

namespace PBIExplorer.Services;

/// <summary>
/// Cruza tres fuentes que ninguna otra herramienta gratuita junta en un solo lugar:
/// qué campo usa cada visual del reporte, qué fórmula DAX depende de qué otra
/// (medida-a-medida, medida-a-columna, columna calculada-a-columna), y qué columnas
/// sostienen una relación. El resultado es, por cada medida/columna/tabla, un
/// veredicto: en uso, solo uso interno (la usa otra fórmula pero no aparece en ningún
/// visual) o candidata a eliminar (no la usa nada, en ningún lado).
/// </summary>
public static class UsageAnalyzer
{
    public static List<ObjectUsage> Analyze(ModelSnapshot model, List<VisualFieldUsage> visuals)
    {
        var result = new List<ObjectUsage>();

        var visualMeasureNames = visuals.Where(v => v.FieldKind == "Medida").Select(v => v.Field).ToHashSet();
        var visualColumnKeys = visuals.Where(v => v.FieldKind == "Columna").Select(v => (v.Table, v.Field)).ToHashSet();
        var visualTables = visuals.Select(v => v.Table).ToHashSet();

        var relationshipColumnKeys = model.Relationships
            .SelectMany(r => new[] { (r.FromTable, r.FromColumn), (r.ToTable, r.ToColumn) })
            .ToHashSet();
        var relationshipTables = model.Relationships
            .SelectMany(r => new[] { r.FromTable, r.ToTable })
            .ToHashSet();

        // Todo lo que puede aparecer del lado derecho de una dependencia: medida por
        // nombre (único en el modelo) o columna por (tabla, nombre).
        var measureUsedBy = new Dictionary<string, List<string>>();
        var columnUsedBy = new Dictionary<(string Table, string Name), List<string>>();

        void RegisterDependency(string sourceLabel, DaxReferenceParser.Reference reference)
        {
            if (reference.Table is null)
            {
                if (!measureUsedBy.TryGetValue(reference.Field, out var list))
                    measureUsedBy[reference.Field] = list = new List<string>();
                if (!list.Contains(sourceLabel)) list.Add(sourceLabel);
            }
            else
            {
                var key = (reference.Table, reference.Field);
                if (!columnUsedBy.TryGetValue(key, out var list))
                    columnUsedBy[key] = list = new List<string>();
                if (!list.Contains(sourceLabel)) list.Add(sourceLabel);
            }
        }

        var measureDependsOn = new Dictionary<string, List<string>>();
        foreach (var m in model.Measures)
        {
            var label = $"[{m.Name}]";
            var refs = DaxReferenceParser.ExtractReferences(m.Expression);
            measureDependsOn[m.Name] = refs.Select(r => r.Table is null ? $"[{r.Field}]" : $"{r.Table}[{r.Field}]").Distinct().ToList();
            foreach (var r in refs) RegisterDependency(label, r);
        }

        var columnDependsOn = new Dictionary<(string Table, string Name), List<string>>();
        foreach (var c in model.Columns.Where(c => !string.IsNullOrWhiteSpace(c.Expression)))
        {
            var label = $"{c.Table}[{c.Name}]";
            var refs = DaxReferenceParser.ExtractReferences(c.Expression);
            columnDependsOn[(c.Table, c.Name)] = refs.Select(r => r.Table is null ? $"[{r.Field}]" : $"{r.Table}[{r.Field}]").Distinct().ToList();
            foreach (var r in refs) RegisterDependency(label, r);
        }

        foreach (var m in model.Measures)
        {
            var usage = new ObjectUsage { Kind = "Medida", Table = m.Table, Name = m.Name };
            usage.UsedInVisual = visualMeasureNames.Contains(m.Name);
            if (measureUsedBy.TryGetValue(m.Name, out var usedBy)) usage.UsedBy.AddRange(usedBy);
            usage.UsedInDax = usage.UsedBy.Count > 0;
            if (measureDependsOn.TryGetValue(m.Name, out var dependsOn)) usage.DependsOn.AddRange(dependsOn);
            result.Add(usage);
        }

        foreach (var c in model.Columns)
        {
            var usage = new ObjectUsage { Kind = "Columna", Table = c.Table, Name = c.Name };
            usage.UsedInVisual = visualColumnKeys.Contains((c.Table, c.Name));
            usage.UsedInRelationship = relationshipColumnKeys.Contains((c.Table, c.Name));
            if (columnUsedBy.TryGetValue((c.Table, c.Name), out var usedBy)) usage.UsedBy.AddRange(usedBy);
            usage.UsedInDax = usage.UsedBy.Count > 0;
            if (columnDependsOn.TryGetValue((c.Table, c.Name), out var dependsOn)) usage.DependsOn.AddRange(dependsOn);
            result.Add(usage);
        }

        foreach (var t in model.Tables)
        {
            var ownObjectsUsed = result.Any(o => o.Table == t.Name && !o.IsUnused);
            var usage = new ObjectUsage
            {
                Kind = "Tabla",
                Table = t.Name,
                Name = t.Name,
                UsedInVisual = visualTables.Contains(t.Name),
                UsedInRelationship = relationshipTables.Contains(t.Name),
                UsedInDax = ownObjectsUsed
            };
            result.Add(usage);
        }

        return result;
    }
}
