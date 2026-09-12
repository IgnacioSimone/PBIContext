using System.Data;
using Microsoft.AnalysisServices.AdomdClient;
using PBIExplorer.Models;

namespace PBIExplorer.Services;

/// <summary>
/// Lee únicamente metadata del modelo (DMVs TMSCHEMA_*) vía la conexión local
/// que Power BI Desktop ya tiene abierta. Nunca ejecuta EVALUATE sobre las
/// tablas de datos: no hay forma de que esto traiga filas reales.
/// </summary>
public class AdomdModelReader
{
    private readonly string _connectionString;

    public AdomdModelReader(string server, string database)
    {
        // Power BI Desktop es quien arma estos dos valores (server/database) al
        // invocar la herramienta externa — pero si alguna vez llegara un valor con
        // ";" u otro separador de connection string, no debe poder inyectar una
        // propiedad extra en la conexión.
        if (server.Contains(';') || server.Contains('=') || database.Contains(';') || database.Contains('='))
            throw new ArgumentException("El servidor o la base recibidos tienen caracteres no esperados.");

        _connectionString = $"Data Source={server};Catalog={database}";
    }

    private static DataTable RunDmv(AdomdConnection conn, string dmvQuery)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = dmvQuery;
        using var adapter = new AdomdDataAdapter(cmd);
        var table = new DataTable();
        adapter.Fill(table);
        return table;
    }

    /// <summary>Lee una columna de forma tolerante: si el DMV de esta versión del motor no la expone, devuelve "".</summary>
    private static string GetStr(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column)) return "";
        var value = row[column];
        return value is null or DBNull ? "" : value.ToString() ?? "";
    }

    /// <summary>Los DMV devuelven los enums como Int64; esto normaliza cualquier numérico.</summary>
    private static long GetLong(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column)) return 0;
        var value = row[column];
        if (value is null or DBNull) return 0;
        try { return Convert.ToInt64(value); } catch { return 0; }
    }

    private static bool GetBool(DataRow row, string column)
    {
        if (!row.Table.Columns.Contains(column)) return false;
        var value = row[column];
        if (value is null or DBNull) return false;
        try { return Convert.ToBoolean(value); } catch { return false; }
    }

    public ModelSnapshot ReadAll()
    {
        var snapshot = new ModelSnapshot();
        using var conn = new AdomdConnection(_connectionString);
        conn.Open();

        var tableNameById = new Dictionary<string, string>();
        var autoDateTableIds = new HashSet<string>();

        // ---------- Tablas ----------
        try
        {
            var raw = RunDmv(conn, "SELECT [ID],[Name],[IsHidden],[Description] FROM $SYSTEM.TMSCHEMA_TABLES");
            foreach (DataRow row in raw.Rows)
            {
                var id = GetStr(row, "ID");
                var name = GetStr(row, "Name");
                tableNameById[id] = name;

                // Power BI crea solo una tabla de fecha oculta por cada columna de fecha/hora
                // (para el desplegable Año/Trimestre/Mes/Día de "Auto Date/Time"). No las armó
                // el usuario y no aparecen en la vista de modelo normal — no cuentan como
                // tablas/columnas/relaciones "reales" del modelo.
                if (IsAutoDateTable(name))
                {
                    autoDateTableIds.Add(id);
                    continue;
                }

                snapshot.Tables.Add(new TableInfo
                {
                    Name = name,
                    IsHidden = GetBool(row, "IsHidden"),
                    Description = GetStr(row, "Description")
                });
            }
        }
        catch (Exception ex)
        {
            snapshot.Warnings.Add($"TMSCHEMA_TABLES: {ex.Message}");
        }

        // ---------- Columnas ----------
        var columnRefById = new Dictionary<string, (string table, string name)>();
        try
        {
            var raw = RunDmv(conn,
                "SELECT [ID],[TableID],[ExplicitName],[InferredName],[ExplicitDataType],[InferredDataType]," +
                "[Type],[IsHidden],[SourceColumn],[Expression],[DisplayFolder],[Description] " +
                "FROM $SYSTEM.TMSCHEMA_COLUMNS");

            foreach (DataRow row in raw.Rows)
            {
                var id = GetStr(row, "ID");
                var tableId = GetStr(row, "TableID");
                if (autoDateTableIds.Contains(tableId)) continue;

                var table = tableNameById.TryGetValue(tableId, out var t) ? t : "?";

                var name = GetStr(row, "ExplicitName");
                if (string.IsNullOrEmpty(name)) name = GetStr(row, "InferredName");

                columnRefById[id] = (table, name);

                var columnType = GetLong(row, "Type");
                // Type 3 = RowNumber: columna interna que el motor crea sola, no es del modelo del usuario.
                if (columnType == 3) continue;

                snapshot.Columns.Add(new ColumnInfo
                {
                    Table = table,
                    Name = name,
                    DataType = MapDataType(GetLong(row, "ExplicitDataType"), GetLong(row, "InferredDataType")),
                    ColumnType = MapColumnType(columnType),
                    IsHidden = GetBool(row, "IsHidden"),
                    SourceColumn = GetStr(row, "SourceColumn"),
                    Expression = GetStr(row, "Expression"),
                    DisplayFolder = GetStr(row, "DisplayFolder"),
                    Description = GetStr(row, "Description")
                });
            }
        }
        catch (Exception ex)
        {
            snapshot.Warnings.Add($"TMSCHEMA_COLUMNS: {ex.Message}");
        }

        // ---------- Medidas ----------
        try
        {
            var raw = RunDmv(conn,
                "SELECT [TableID],[Name],[Expression],[DisplayFolder],[FormatString],[IsHidden],[Description] " +
                "FROM $SYSTEM.TMSCHEMA_MEASURES");

            foreach (DataRow row in raw.Rows)
            {
                var tableId = GetStr(row, "TableID");
                if (autoDateTableIds.Contains(tableId)) continue;

                snapshot.Measures.Add(new MeasureInfo
                {
                    Table = tableNameById.TryGetValue(tableId, out var t) ? t : "?",
                    Name = GetStr(row, "Name"),
                    Expression = GetStr(row, "Expression"),
                    DisplayFolder = GetStr(row, "DisplayFolder"),
                    FormatString = GetStr(row, "FormatString"),
                    IsHidden = GetBool(row, "IsHidden"),
                    Description = GetStr(row, "Description")
                });
            }
        }
        catch (Exception ex)
        {
            snapshot.Warnings.Add($"TMSCHEMA_MEASURES: {ex.Message}");
        }

        // ---------- Relaciones ----------
        try
        {
            var raw = RunDmv(conn,
                "SELECT [FromColumnID],[ToColumnID],[FromCardinality],[ToCardinality]," +
                "[CrossFilteringBehavior],[IsActive] FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS");

            foreach (DataRow row in raw.Rows)
            {
                var fromColId = GetStr(row, "FromColumnID");
                var toColId = GetStr(row, "ToColumnID");

                // Si cualquiera de los dos lados es una tabla de fecha automática, no es una
                // relación que el usuario haya armado — es plomería de "Auto Date/Time".
                if (!columnRefById.ContainsKey(fromColId) || !columnRefById.ContainsKey(toColId)) continue;

                var fromCol = columnRefById[fromColId];
                var toCol = columnRefById[toColId];

                snapshot.Relationships.Add(new RelationshipInfo
                {
                    FromTable = fromCol.Item1,
                    FromColumn = fromCol.Item2,
                    ToTable = toCol.Item1,
                    ToColumn = toCol.Item2,
                    Cardinality = $"{MapCardinality(GetLong(row, "FromCardinality"))} a {MapCardinality(GetLong(row, "ToCardinality")).ToLowerInvariant()}",
                    CrossFilterBehavior = MapCrossFilter(GetLong(row, "CrossFilteringBehavior")),
                    IsActive = GetBool(row, "IsActive")
                });
            }
        }
        catch (Exception ex)
        {
            snapshot.Warnings.Add($"TMSCHEMA_RELATIONSHIPS: {ex.Message}");
        }

        // Si Tablas o Columnas fallaron antes, columnRefById/tableNameById quedan vacíos
        // y CADA relación se descarta en el filtro de arriba — sin este aviso, eso se ve
        // en pantalla como "este modelo no tiene relaciones", que es información falsa.
        if (columnRefById.Count == 0 && snapshot.Tables.Count == 0 && snapshot.Relationships.Count == 0 &&
            snapshot.Warnings.Any(w => w.StartsWith("TMSCHEMA_TABLES") || w.StartsWith("TMSCHEMA_COLUMNS")))
        {
            snapshot.Warnings.Add("Relaciones: no se pudieron resolver porque Tablas y/o Columnas fallaron antes " +
                "(las relaciones dependen de esa información). No significa que el modelo no tenga relaciones.");
        }

        // ---------- Power Query (M) ----------
        try
        {
            var raw = RunDmv(conn, "SELECT [TableID],[Name],[QueryDefinition] FROM $SYSTEM.TMSCHEMA_PARTITIONS");
            foreach (DataRow row in raw.Rows)
            {
                var tableId = GetStr(row, "TableID");
                if (autoDateTableIds.Contains(tableId)) continue;

                var mCode = GetStr(row, "QueryDefinition");
                if (string.IsNullOrWhiteSpace(mCode)) continue;

                var table = tableNameById.TryGetValue(tableId, out var t) ? t : "?";
                snapshot.PowerQueries.Add(new PowerQueryInfo
                {
                    Name = table,
                    Kind = "Tabla",
                    MCode = mCode
                });
            }
        }
        catch (Exception ex)
        {
            snapshot.Warnings.Add($"TMSCHEMA_PARTITIONS: {ex.Message}");
        }

        // Parámetros y funciones compartidas de Power Query (no están atados a ninguna tabla).
        try
        {
            var raw = RunDmv(conn, "SELECT [Name],[Expression],[Description] FROM $SYSTEM.TMSCHEMA_EXPRESSIONS");
            foreach (DataRow row in raw.Rows)
            {
                snapshot.PowerQueries.Add(new PowerQueryInfo
                {
                    Name = GetStr(row, "Name"),
                    Kind = "Parámetro / función",
                    MCode = GetStr(row, "Expression"),
                    Description = GetStr(row, "Description")
                });
            }
        }
        catch (Exception ex)
        {
            snapshot.Warnings.Add($"TMSCHEMA_EXPRESSIONS: {ex.Message}");
        }

        // Conteos por tabla, para la pestaña de Tablas.
        foreach (var table in snapshot.Tables)
        {
            table.ColumnCount = snapshot.Columns.Count(c => c.Table == table.Name);
            table.MeasureCount = snapshot.Measures.Count(m => m.Table == table.Name);
        }

        return snapshot;
    }

    /// <summary>
    /// Tablas que Power BI crea solo (una por columna de fecha/hora, para el desplegable
    /// Año/Trimestre/Mes/Día de "Auto Date/Time"). Nunca las ve el usuario en la vista de
    /// modelo, así que tampoco deberían contar como tablas/columnas/relaciones del modelo.
    /// </summary>
    private static bool IsAutoDateTable(string name) =>
        name.StartsWith("LocalDateTable_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DateTableTemplate_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Enum DataType de TOM. Si el tipo explícito es Automático/Desconocido, cae al inferido.</summary>
    private static string MapDataType(long explicitType, long inferredType)
    {
        var value = explicitType;
        if (value is 0 or 1 or 19 && inferredType is not (0 or 1 or 19)) value = inferredType;

        return value switch
        {
            1 => "Automático",
            2 => "Texto",
            6 => "Número entero",
            8 => "Número decimal",
            9 => "Fecha/Hora",
            10 => "Decimal fijo",
            11 => "Booleano",
            17 => "Binario",
            19 => "Desconocido",
            20 => "Variante",
            _ => $"Tipo #{value}"
        };
    }

    /// <summary>Enum ColumnType de TOM.</summary>
    private static string MapColumnType(long value) => value switch
    {
        1 => "De datos",
        2 => "Calculada",
        3 => "RowNumber (interna)",
        4 => "De tabla calculada",
        _ => $"Tipo #{value}"
    };

    /// <summary>Enum CrossFilteringBehavior de TOM.</summary>
    private static string MapCrossFilter(long value) => value switch
    {
        1 => "Único sentido",
        2 => "Ambos sentidos",
        3 => "Automático",
        _ => $"#{value}"
    };

    /// <summary>Enum RelationshipEndCardinality de TOM.</summary>
    private static string MapCardinality(long value) => value switch
    {
        1 => "Uno",
        2 => "Muchos",
        _ => $"#{value}"
    };
}
