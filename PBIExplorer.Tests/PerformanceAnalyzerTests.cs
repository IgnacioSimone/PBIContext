using PBIExplorer.Models;
using PBIExplorer.Services;

namespace PBIExplorer.Tests;

public class PerformanceAnalyzerTests
{
    [Theory]
    [InlineData("SUM(Ventas[Monto])", 0)]
    [InlineData("CALCULATE(SUM(Ventas[Monto]), Ventas[Anio] = 2024)", 1)]
    [InlineData("CALCULATE(CALCULATE(SUM(Ventas[Monto]), Ventas[Anio] = 2024), Ventas[Mes] = 1)", 2)]
    [InlineData("CALCULATE(SUM(Ventas[Monto])) + CALCULATE(SUM(Ventas[Costo]))", 1)] // secuenciales, no anidados
    [InlineData("CALCULATETABLE(Ventas, CALCULATE(FILTER(Clientes, Clientes[Activo])))", 2)]
    [InlineData("", 0)]
    public void MaxNestedCalculateDepth_CountsNestingCorrectly(string expression, int expectedDepth)
    {
        Assert.Equal(expectedDepth, PerformanceAnalyzer.MaxNestedCalculateDepth(expression));
    }

    [Fact]
    public void MaxNestedCalculateDepth_DoesNotThrow_OnUnbalancedParens()
    {
        var depth = PerformanceAnalyzer.MaxNestedCalculateDepth("CALCULATE(SUM(Ventas[Monto])");
        Assert.True(depth >= 1);
    }

    [Fact]
    public void Analyze_FlagsBidirectionalRelationship()
    {
        var model = new ModelSnapshot();
        model.Relationships.Add(new RelationshipInfo
        {
            FromTable = "Ventas", FromColumn = "ClienteId", ToTable = "Clientes", ToColumn = "Id",
            CrossFilterBehavior = "Ambos sentidos", Cardinality = "Muchos a uno", IsActive = true
        });

        var warnings = PerformanceAnalyzer.Analyze(model);

        Assert.Contains(warnings, w => w.Category == "Relación bidireccional" && w.Severity == PerformanceSeverity.Alta);
    }

    [Fact]
    public void Analyze_FlagsManyToManyRelationship()
    {
        var model = new ModelSnapshot();
        model.Relationships.Add(new RelationshipInfo
        {
            FromTable = "A", FromColumn = "Id", ToTable = "B", ToColumn = "Id",
            CrossFilterBehavior = "Único sentido", Cardinality = "Muchos a muchos", IsActive = true
        });

        var warnings = PerformanceAnalyzer.Analyze(model);

        Assert.Contains(warnings, w => w.Category == "Relación muchos a muchos");
    }

    [Fact]
    public void Analyze_DoesNotFlagNormalOneToManyRelationship()
    {
        var model = new ModelSnapshot();
        model.Relationships.Add(new RelationshipInfo
        {
            FromTable = "Ventas", FromColumn = "ClienteId", ToTable = "Clientes", ToColumn = "Id",
            CrossFilterBehavior = "Único sentido", Cardinality = "Muchos a uno", IsActive = true
        });

        Assert.Empty(PerformanceAnalyzer.Analyze(model));
    }

    [Fact]
    public void Analyze_FlagsTableWithManyCalculatedColumns_ButNotJustOneOrTwo()
    {
        var model = new ModelSnapshot();
        for (var i = 0; i < 2; i++)
            model.Columns.Add(new ColumnInfo { Table = "Ventas", Name = $"Calc{i}", ColumnType = "Calculada", Expression = "1+1" });
        Assert.DoesNotContain(PerformanceAnalyzer.Analyze(model), w => w.Category == "Columnas calculadas");

        model.Columns.Add(new ColumnInfo { Table = "Ventas", Name = "Calc2", ColumnType = "Calculada", Expression = "1+1" });
        Assert.Contains(PerformanceAnalyzer.Analyze(model), w => w.Category == "Columnas calculadas" && w.Table == "Ventas");
    }

    [Fact]
    public void Analyze_FlagsMeasureWithDeeplyNestedCalculate()
    {
        var model = new ModelSnapshot();
        model.Measures.Add(new MeasureInfo
        {
            Table = "Ventas", Name = "Total complejo",
            Expression = "CALCULATE(CALCULATE(CALCULATE(SUM(Ventas[Monto]))))"
        });

        Assert.Contains(PerformanceAnalyzer.Analyze(model), w => w.Category == "CALCULATE anidado" && w.Subject == "Total complejo");
    }
}
