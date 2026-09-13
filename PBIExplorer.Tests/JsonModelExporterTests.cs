using PBIExplorer.Models;
using PBIExplorer.Services;

namespace PBIExplorer.Tests;

public class JsonModelExporterTests
{
    private static ModelSnapshot SampleModel()
    {
        var model = new ModelSnapshot();
        model.Tables.Add(new TableInfo { Name = "Ventas", Description = "Contacto: nacho@empresa.com" });
        model.Measures.Add(new MeasureInfo { Table = "Ventas", Name = "Total", Expression = "SUM(Ventas[Monto])" });
        model.Columns.Add(new ColumnInfo { Table = "Ventas", Name = "Monto", DataType = "Número decimal" });
        model.Relationships.Add(new RelationshipInfo
        {
            FromTable = "Ventas", FromColumn = "ClienteId", ToTable = "Clientes", ToColumn = "Id",
            Cardinality = "Muchos a uno", CrossFilterBehavior = "Único sentido", IsActive = true
        });
        model.PowerQueries.Add(new PowerQueryInfo { Name = "Ventas", Kind = "Tabla", MCode = "password=abc123;" });
        return model;
    }

    private static string TempJsonPath() => Path.Combine(Path.GetTempPath(), $"pbicontext_test_{Guid.NewGuid():N}.json");

    [Fact]
    public void ExportThenLoad_RoundTripsStructure_AndMasksSensitiveText()
    {
        var path = TempJsonPath();
        try
        {
            var model = SampleModel();
            JsonModelExporter.Export(path, "MiTablero", model, new List<VisualFieldUsage>());

            var loaded = JsonModelExporter.Load(path);

            Assert.Equal("MiTablero", loaded.SourceName);
            Assert.Single(loaded.Model.Tables);
            Assert.Single(loaded.Model.Measures);
            Assert.Single(loaded.Model.Relationships);

            // El email y la password no deben llegar en texto plano al archivo.
            Assert.DoesNotContain("nacho@empresa.com", loaded.Model.Tables[0].Description);
            Assert.DoesNotContain("abc123", loaded.Model.PowerQueries[0].MCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportThenLoad_ThenCompareAgainstSelf_HasNoDiff()
    {
        var path = TempJsonPath();
        try
        {
            var model = SampleModel();
            JsonModelExporter.Export(path, "MiTablero", model, new List<VisualFieldUsage>());
            var loaded = JsonModelExporter.Load(path);

            // Comparar el snapshot cargado contra sí mismo debe dar cero diferencias.
            var diff = ModelDiffAnalyzer.Compare(loaded.Model, loaded.Model);
            Assert.Empty(diff);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportThenLoad_ThenCompareAgainstChangedModel_ReportsChange()
    {
        var olderPath = TempJsonPath();
        var newerPath = TempJsonPath();
        try
        {
            var model = SampleModel();
            JsonModelExporter.Export(olderPath, "MiTablero", model, new List<VisualFieldUsage>());
            var older = JsonModelExporter.Load(olderPath);

            var newer = SampleModel();
            newer.Measures[0].Expression = "SUM(Ventas[Monto]) * 1.21";
            JsonModelExporter.Export(newerPath, "MiTablero", newer, new List<VisualFieldUsage>());
            var maskedNewer = JsonModelExporter.Load(newerPath);

            var diff = ModelDiffAnalyzer.Compare(older.Model, maskedNewer.Model);

            Assert.Contains(diff, e => e.Kind == DiffKind.Modificado && e.Category == "Medida");
        }
        finally
        {
            File.Delete(olderPath);
            File.Delete(newerPath);
        }
    }
}
