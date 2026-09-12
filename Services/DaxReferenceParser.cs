using System.Text.RegularExpressions;

namespace PBIExplorer.Services;

/// <summary>
/// Extrae, de una fórmula DAX, a qué columnas y medidas hace referencia. Es una
/// heurística de texto (no un parser real de DAX), pero se apoya en una regla que sí
/// es una regla real del lenguaje: una referencia con nombre de tabla antes del
/// corchete (<c>'Tabla'[Campo]</c> o <c>Tabla[Campo]</c>) siempre es una columna —
/// una medida en DAX nunca se califica con tabla. Un corchete suelto (<c>[Campo]</c>,
/// sin nombre de tabla antes) es, en la enorme mayoría de los casos reales, una
/// referencia a una medida.
/// </summary>
public static class DaxReferenceParser
{
    private static readonly Regex QualifiedRef = new(
        @"(?:'([^']+)'|([A-Za-z_][A-Za-z0-9_]*))\[([^\[\]]+)\]", RegexOptions.Compiled);

    private static readonly Regex AnyBracket = new(@"\[([^\[\]]+)\]", RegexOptions.Compiled);

    public readonly record struct Reference(string? Table, string Field);

    public static List<Reference> ExtractReferences(string? expression)
    {
        var result = new List<Reference>();
        if (string.IsNullOrWhiteSpace(expression)) return result;

        var qualifiedSpans = new List<(int Start, int Len)>();
        foreach (Match m in QualifiedRef.Matches(expression))
        {
            var table = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            result.Add(new Reference(table, m.Groups[3].Value));
            qualifiedSpans.Add((m.Index, m.Length));
        }

        foreach (Match m in AnyBracket.Matches(expression))
        {
            if (qualifiedSpans.Any(s => m.Index >= s.Start && m.Index < s.Start + s.Len)) continue;
            result.Add(new Reference(null, m.Groups[1].Value));
        }

        return result;
    }
}
