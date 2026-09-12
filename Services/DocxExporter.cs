using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PBIExplorer.Models;

namespace PBIExplorer.Services;

public static class DocxExporter
{
    public static void ExportFullDocumentation(
        string path,
        string sourceName,
        List<TableInfo>? tables,
        List<ColumnInfo>? columns,
        List<MeasureInfo>? measures,
        List<RelationshipInfo>? relationships,
        List<PowerQueryInfo>? powerQueries,
        List<VisualFieldUsage>? visuals,
        DashboardSummary? summary = null,
        List<ObjectUsage>? usage = null)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        AddHeading(body, $"Documentación del modelo — {sourceName}", 0);
        AddParagraph(body, $"Generado el {DateTime.Now:dd/MM/yyyy HH:mm} con PBI Context.");

        if (summary is not null && !string.IsNullOrWhiteSpace(summary.OverviewText))
        {
            AddHeading(body, "Resumen", 1);
            AddParagraph(body, summary.OverviewText);

            foreach (var h in summary.Highlights)
                AddParagraph(body, "• " + h);

            if (summary.Pages.Count > 0)
            {
                AddHeading(body, "Qué muestra cada página", 2);
                foreach (var page in summary.Pages)
                {
                    AddHeading(body, page.PageName, 3);
                    AddParagraph(body, page.Description);
                }
            }
        }

        if (tables is { Count: > 0 })
        {
            AddHeading(body, "Tablas", 1);
            AddTable(body, new[] { "Tabla", "Columnas", "Medidas", "Oculta", "Descripción" },
                tables.Select(t => new[]
                {
                    t.Name, t.ColumnCount.ToString(), t.MeasureCount.ToString(),
                    t.IsHidden ? "Sí" : "No", SensitiveTextGuard.Mask(t.Description)
                }));
        }

        if (columns is { Count: > 0 })
        {
            AddHeading(body, "Columnas", 1);
            AddTable(body, new[] { "Tabla", "Columna", "Tipo de dato", "Origen", "Oculta" },
                columns.Select(c => new[]
                {
                    c.Table, c.Name, c.DataType, c.ColumnType, c.IsHidden ? "Sí" : "No"
                }));

            var calculated = columns.Where(c => !string.IsNullOrWhiteSpace(c.Expression)).ToList();
            if (calculated.Count > 0)
            {
                AddHeading(body, "Columnas calculadas (DAX)", 1);
                foreach (var c in calculated)
                {
                    AddHeading(body, $"{c.Table}[{c.Name}]", 2);
                    AddCodeBlock(body, SensitiveTextGuard.Mask(c.Expression));
                }
            }
        }

        if (measures is { Count: > 0 })
        {
            AddHeading(body, "Medidas DAX", 1);
            foreach (var m in measures)
            {
                AddHeading(body, $"{m.Table}.{m.Name}", 2);
                if (!string.IsNullOrWhiteSpace(m.DisplayFolder))
                    AddParagraph(body, $"Carpeta: {m.DisplayFolder}", italic: true);
                AddCodeBlock(body, SensitiveTextGuard.Mask(m.Expression));
            }
        }

        if (relationships is { Count: > 0 })
        {
            AddHeading(body, "Relaciones", 1);
            AddTable(body, new[] { "Desde", "Hacia", "Cardinalidad", "Filtro cruzado", "Activa" },
                relationships.Select(r => new[]
                {
                    $"{r.FromTable}.{r.FromColumn}", $"{r.ToTable}.{r.ToColumn}",
                    r.Cardinality, r.CrossFilterBehavior, r.IsActive ? "Sí" : "No"
                }));
        }

        if (powerQueries is { Count: > 0 })
        {
            AddHeading(body, "Power Query (M)", 1);
            foreach (var pq in powerQueries)
            {
                AddHeading(body, $"{pq.Kind}: {pq.Name}", 2);
                if (!string.IsNullOrWhiteSpace(pq.Description))
                    AddParagraph(body, SensitiveTextGuard.Mask(pq.Description), italic: true);
                AddCodeBlock(body, SensitiveTextGuard.Mask(pq.MCode));
            }
        }

        var unused = usage?.Where(u => u.IsUnused).ToList();
        if (unused is { Count: > 0 })
        {
            AddHeading(body, "Objetos sin uso detectado (candidatos a limpieza)", 1);
            AddParagraph(body, "No aparecen usados en ningún visual, ninguna fórmula DAX ni ninguna relación. " +
                                "No sigue dependencias de Power Query entre consultas (M) — una tabla auxiliar que " +
                                "solo alimenta a otra vía M puede figurar acá sin serlo de verdad. Confirmá antes de " +
                                "borrar que no lo use tampoco una segmentación, un marcador, o Q&A en lenguaje natural.");
            AddTable(body, new[] { "Tipo", "Tabla", "Nombre" },
                unused.Select(u => new[] { u.Kind, u.Table, u.Name }));
        }

        if (visuals is { Count: > 0 })
        {
            AddHeading(body, "Visuales y campos usados", 1);
            var byPage = visuals.GroupBy(v => v.Page);
            foreach (var pageGroup in byPage)
            {
                AddHeading(body, pageGroup.Key, 2);
                AddTable(body, new[] { "Visual", "Título", "Tipo de campo", "Tabla", "Campo" },
                    pageGroup.Select(v => new[] { v.VisualType, SensitiveTextGuard.Mask(v.Title), v.FieldKind, v.Table, v.Field }));
            }
        }

        mainPart.Document.Save();
    }

    /// <summary>El XML de Word no acepta ciertos caracteres de control (0x00-0x08, 0x0B,
    /// 0x0C, 0x0E-0x1F). Un comentario DAX pegado desde otro editor o una descripción con
    /// basura invisible puede traer alguno y tirar abajo TODO el documento al guardar —
    /// mejor perder el carácter que perder el export entero.</summary>
    private static string SanitizeXml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        Span<char> buffer = text.Length <= 1024 ? stackalloc char[text.Length] : new char[text.Length];
        var written = 0;
        foreach (var c in text)
        {
            var isValidXmlChar = c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF);
            buffer[written] = isValidXmlChar ? c : ' ';
            written++;
        }
        return new string(buffer[..written]);
    }

    private static void AddHeading(Body body, string text, int level)
    {
        var style = level switch { 0 => "Title", 1 => "Heading1", 2 => "Heading2", _ => "Heading3" };
        var para = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = style }),
            new Run(new Text(SanitizeXml(text))));
        body.AppendChild(para);
    }

    private static void AddParagraph(Body body, string text, bool italic = false)
    {
        var runProps = new RunProperties();
        if (italic) runProps.Append(new Italic());
        var run = new Run(runProps, new Text(SanitizeXml(text)));
        body.AppendChild(new Paragraph(run));
    }

    private static void AddCodeBlock(Body body, string text)
    {
        var lines = SanitizeXml(text).Replace("\r\n", "\n").Split('\n');
        var runs = new List<OpenXmlElement>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) runs.Add(new Run(new Break()));
            runs.Add(new Run(new RunProperties(new RunFonts { Ascii = "Consolas" }), new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve }));
        }
        var para = new Paragraph(new ParagraphProperties(new Shading { Fill = "F2F2F2" }));
        foreach (var r in runs) para.AppendChild(r);
        body.AppendChild(para);
    }

    private static void AddTable(Body body, string[] headers, IEnumerable<string[]> rows)
    {
        var table = new DocumentFormat.OpenXml.Wordprocessing.Table();
        var tblProps = new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }));
        table.AppendChild(tblProps);

        var headerRow = new TableRow();
        foreach (var h in headers)
        {
            headerRow.AppendChild(new TableCell(
                new TableCellProperties(new Shading { Fill = "D9E2D9" }),
                new Paragraph(new Run(new RunProperties(new Bold()), new Text(SanitizeXml(h))))));
        }
        table.AppendChild(headerRow);

        foreach (var row in rows)
        {
            var tr = new TableRow();
            foreach (var cellText in row)
            {
                tr.AppendChild(new TableCell(new Paragraph(new Run(new Text(SanitizeXml(cellText))))));
            }
            table.AppendChild(tr);
        }

        body.AppendChild(table);
        body.AppendChild(new Paragraph());
    }
}
