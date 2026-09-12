using System.IO;

namespace PBIExplorer.Services;

/// <summary>Rutas propias de la app en el perfil del usuario — nada en carpetas
/// compartidas como %TEMP%, donde otro proceso local podría curiosear qué tableros
/// se estuvieron analizando.</summary>
public static class AppPaths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PBIContext");

    public static readonly string LogPath = Path.Combine(DataDir, "debug.log");

    static AppPaths()
    {
        try { Directory.CreateDirectory(DataDir); } catch { /* el log es best-effort */ }
    }
}
