namespace PBIExplorer.Models;

/// <summary>Todo lo que se pudo leer del modelo, más las secciones que fallaron.
/// Las colecciones llevan setter (no solo get) aunque el código siempre las mute in
/// place con .Add(...) — System.Text.Json no repuebla una propiedad de colección de
/// solo lectura al deserializar (JsonModelExporter.Load necesita esto para reconstruir
/// un snapshot guardado antes).</summary>
public class ModelSnapshot
{
    public List<TableInfo> Tables { get; set; } = new();
    public List<ColumnInfo> Columns { get; set; } = new();
    public List<MeasureInfo> Measures { get; set; } = new();
    public List<RelationshipInfo> Relationships { get; set; } = new();
    public List<PowerQueryInfo> PowerQueries { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class TableInfo
{
    public string Name { get; set; } = "";
    public bool IsHidden { get; set; }
    public string Description { get; set; } = "";
    public int ColumnCount { get; set; }
    public int MeasureCount { get; set; }
}

public class ColumnInfo
{
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string ColumnType { get; set; } = "";
    public bool IsHidden { get; set; }
    public string SourceColumn { get; set; } = "";
    public string Expression { get; set; } = "";
    public string DisplayFolder { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Una columna calculada muestra su DAX; una normal, la columna física de origen.</summary>
    public string DaxOrSource => string.IsNullOrWhiteSpace(Expression) ? SourceColumn : Expression;
}

public class MeasureInfo
{
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "";
    public string DisplayFolder { get; set; } = "";
    public string FormatString { get; set; } = "";
    public bool IsHidden { get; set; }
    public string Description { get; set; } = "";
}

public class RelationshipInfo
{
    public string FromTable { get; set; } = "";
    public string FromColumn { get; set; } = "";
    public string ToTable { get; set; } = "";
    public string ToColumn { get; set; } = "";
    public string CrossFilterBehavior { get; set; } = "";
    public bool IsActive { get; set; }
    public string Cardinality { get; set; } = "";
}

public class PowerQueryInfo
{
    /// <summary>Nombre de la tabla o del parámetro/función compartida.</summary>
    public string Name { get; set; } = "";
    /// <summary>"Tabla", "Parámetro" o "Función compartida".</summary>
    public string Kind { get; set; } = "";
    public string MCode { get; set; } = "";
    public bool IsHidden { get; set; }
    public string Description { get; set; } = "";
}

public class ObjectUsage
{
    public string Kind { get; set; } = "";   // "Medida", "Columna", "Tabla"
    public string Table { get; set; } = "";
    public string Name { get; set; } = "";
    public bool UsedInVisual { get; set; }
    public bool UsedInRelationship { get; set; }
    public bool UsedInDax { get; set; }      // otra medida/columna calculada depende de este objeto
    public List<string> DependsOn { get; } = new();  // objetos que ESTE usa en su fórmula
    public List<string> UsedBy { get; } = new();     // objetos que usan a ESTE en su fórmula

    public bool IsUnused => !UsedInVisual && !UsedInRelationship && !UsedInDax;

    public string Status => IsUnused ? "Sin uso — candidata a eliminar"
        : !UsedInVisual && (UsedInDax || UsedInRelationship) ? "Solo uso interno"
        : "En uso";

    public string DependsOnText => DependsOn.Count > 0 ? string.Join(", ", DependsOn) : "";
    public string UsedByText => UsedBy.Count > 0 ? string.Join(", ", UsedBy) : "";
}

public class VisualFieldUsage
{
    public string Page { get; set; } = "";
    public string VisualType { get; set; } = "";
    public string Title { get; set; } = "";
    public string FieldKind { get; set; } = ""; // Medida / Columna
    public string Table { get; set; } = "";
    public string Field { get; set; } = "";
}
