using PBIExplorer.Models;
using PBIExplorer.Services;

namespace PBIExplorer.Tests;

public class ModelDiffAnalyzerTests
{
    private static ModelSnapshot BaseModel()
    {
        var model = new ModelSnapshot();
        model.Tables.Add(new TableInfo { Name = "Ventas", IsHidden = false, Description = "Hechos de venta" });
        model.Measures.Add(new MeasureInfo { Table = "Ventas", Name = "Total", Expression = "SUM(Ventas[Monto])" });
        model.Columns.Add(new ColumnInfo { Table = "Ventas", Name = "Monto", DataType = "Número decimal" });
        model.Relationships.Add(new RelationshipInfo
        {
            FromTable = "Ventas", FromColumn = "ClienteId", ToTable = "Clientes", ToColumn = "Id",
            Cardinality = "Muchos a uno", CrossFilterBehavior = "Único sentido", IsActive = true
        });
        return model;
    }

    [Fact]
    public void Compare_IdenticalModels_ReturnsNoEntries()
    {
        var a = BaseModel();
        var b = BaseModel();
        Assert.Empty(ModelDiffAnalyzer.Compare(a, b));
    }

    [Fact]
    public void Compare_NewMeasure_ReportedAsAgregado()
    {
        var older = BaseModel();
        var newer = BaseModel();
        newer.Measures.Add(new MeasureInfo { Table = "Ventas", Name = "Margen", Expression = "SUM(Ventas[Ganancia])" });

        var diff = ModelDiffAnalyzer.Compare(older, newer);

        Assert.Contains(diff, e => e.Kind == DiffKind.Agregado && e.Category == "Medida" && e.Name == "Ventas[Margen]");
    }

    [Fact]
    public void Compare_RemovedTable_ReportedAsEliminado()
    {
        var older = BaseModel();
        older.Tables.Add(new TableInfo { Name = "Temp" });
        var newer = BaseModel();

        var diff = ModelDiffAnalyzer.Compare(older, newer);

        Assert.Contains(diff, e => e.Kind == DiffKind.Eliminado && e.Category == "Tabla" && e.Name == "Temp");
    }

    [Fact]
    public void Compare_ChangedMeasureExpression_ReportedAsModificado()
    {
        var older = BaseModel();
        var newer = BaseModel();
        newer.Measures[0].Expression = "SUM(Ventas[Monto]) * 1.21";

        var diff = ModelDiffAnalyzer.Compare(older, newer);

        var entry = Assert.Single(diff);
        Assert.Equal(DiffKind.Modificado, entry.Kind);
        Assert.Equal("Medida", entry.Category);
        Assert.Contains("DAX", entry.Detail);
    }

    [Fact]
    public void Compare_ChangedRelationshipCardinality_ReportedWithBeforeAndAfter()
    {
        var older = BaseModel();
        var newer = BaseModel();
        newer.Relationships[0].Cardinality = "Muchos a muchos";

        var diff = ModelDiffAnalyzer.Compare(older, newer);

        var entry = Assert.Single(diff);
        Assert.Equal("Relación", entry.Category);
        Assert.Contains("Muchos a uno", entry.Detail);
        Assert.Contains("Muchos a muchos", entry.Detail);
    }

    [Fact]
    public void Compare_DuplicateKeys_DoesNotThrow_KeepsFirst()
    {
        var older = new ModelSnapshot();
        older.Tables.Add(new TableInfo { Name = "Ventas", Description = "primera" });
        older.Tables.Add(new TableInfo { Name = "Ventas", Description = "duplicada" });
        var newer = new ModelSnapshot();
        newer.Tables.Add(new TableInfo { Name = "Ventas", Description = "primera" });

        var diff = ModelDiffAnalyzer.Compare(older, newer);

        Assert.Empty(diff); // se queda con la primera de cada lado, que coincide
    }
}
