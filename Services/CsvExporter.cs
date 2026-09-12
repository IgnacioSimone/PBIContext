using System.Globalization;
using System.IO;
using System.Text;

namespace PBIExplorer.Services;

public static class CsvExporter
{
    public static void Export(string path, IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(v => Escape(FormatValue(v)))));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        bool b => b ? "Sí" : "No",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>Caracteres que Excel/Sheets interpretan como inicio de fórmula si abren el CSV
    /// directamente (CSV/Formula Injection, OWASP). Un nombre de medida/tabla o una expresión
    /// DAX/M que empiece con uno de estos puede terminar ejecutándose como fórmula en la
    /// planilla de quien lo abra.</summary>
    private static readonly char[] FormulaTriggers = { '=', '+', '-', '@', '\t', '\r' };

    private static string Escape(string field)
    {
        if (field.Length > 0 && FormulaTriggers.Contains(field[0]))
            field = "'" + field;

        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }
}
