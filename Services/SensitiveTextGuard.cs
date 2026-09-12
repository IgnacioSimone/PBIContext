using System.Text.RegularExpressions;

namespace PBIExplorer.Services;

/// <summary>
/// El código DAX/M de una medida puede traer valores literales embebidos
/// (un email, un DNI, un token de conexión). Esto enmascara esos patrones
/// antes de mostrar/exportar el texto, sin tocar la estructura de la fórmula.
/// </summary>
public static class SensitiveTextGuard
{
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    // Un DAX/M real usa números de 7-8 dígitos todo el tiempo como umbrales, montos o
    // años de ejercicio (ej. 1000000, 20241231) — enmascarar CUALQUIER número de ese
    // largo arruina la documentación exportada con falsos positivos. Un DNI hardcodeado
    // en una fórmula, en cambio, casi siempre aparece como valor de comparación/filtro
    // entre comillas (ej. Cliente[DNI] = "12345678"), así que solo se enmascara ahí.
    private static readonly Regex Dni = new(@"(?<=[""'])\d{7,8}(?=[""'])", RegexOptions.Compiled);
    private static readonly Regex Cuit = new(@"(?<=[""'])\d{2}-?\d{8}-?\d(?=[""'])|\b\d{2}-\d{8}-\d\b", RegexOptions.Compiled);
    private static readonly Regex ConnLike = new(@"(?i)(password|pwd|secret|apikey|token)\s*=\s*[""']?[^;""'\s]+", RegexOptions.Compiled);

    /// <summary>Las consultas de Power Query suelen traer rutas locales embebidas
    /// (origen Excel/CSV en el disco de quien armó el modelo), y esa ruta expone su
    /// nombre de usuario de Windows: C:\Users\NombreReal\... </summary>
    private static readonly Regex LocalUserPath = new(@"([A-Za-z]:\\Users\\)([^\\""']+)", RegexOptions.Compiled);

    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = ConnLike.Replace(text, m => m.Value.Split('=')[0] + "=***");
        text = LocalUserPath.Replace(text, "$1***");
        text = Email.Replace(text, "***@***");
        text = Cuit.Replace(text, "**-********-*");
        text = Dni.Replace(text, "********");
        return text;
    }
}
