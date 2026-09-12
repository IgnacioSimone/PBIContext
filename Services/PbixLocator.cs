using System.Diagnostics;
using System.IO;

namespace PBIExplorer.Services;

/// <summary>
/// Averigua qué archivo .pbix tiene abierto Power BI Desktop, para no obligar
/// al usuario a elegirlo a mano. Power BI Desktop no le pasa la ruta a las
/// herramientas externas (solo servidor y base), así que hay que deducirla.
/// </summary>
public static class PbixLocator
{
    private const string DesktopProcessName = "PBIDesktop";
    private const string TitleSuffix = " - Power BI Desktop";

    /// <summary>Nombre del reporte abierto según el título de la ventana, o null.</summary>
    public static string? GetOpenReportName()
    {
        foreach (var process in Process.GetProcessesByName(DesktopProcessName))
        {
            string title;
            try
            {
                // Si el usuario cierra Power BI Desktop justo entre que lanza esta
                // herramienta y que corre este código, el proceso ya no existe y
                // MainWindowTitle tira InvalidOperationException — no debe voltear
                // la app en la primerísima pantalla que ve alguien.
                title = process.MainWindowTitle;
            }
            catch (InvalidOperationException) { continue; }

            if (string.IsNullOrWhiteSpace(title)) continue;

            // "Simulación de Exámenes - Power BI Desktop"
            var idx = title.IndexOf(TitleSuffix, StringComparison.OrdinalIgnoreCase);
            var name = idx > 0 ? title[..idx] : title;

            // Power BI marca los cambios sin guardar con un asterisco al final.
            name = name.TrimEnd('*', ' ');

            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return null;
    }

    /// <summary>
    /// Busca en disco el .pbix que corresponde al reporte abierto. Devuelve null si
    /// no lo encuentra o si hay ambigüedad que no se puede resolver sola.
    /// </summary>
    public static string? FindOpenPbix(TimeSpan budget)
    {
        var reportName = GetOpenReportName();
        if (string.IsNullOrWhiteSpace(reportName)) return null;

        var targetFile = reportName + ".pbix";
        var deadline = DateTime.UtcNow + budget;

        var matches = new List<string>();
        foreach (var root in SearchRoots())
        {
            SearchDirectory(root, targetFile, matches, deadline, depth: 0, maxDepth: 4);
            if (matches.Count > 0 && DateTime.UtcNow > deadline) break;
        }

        if (matches.Count == 0) return null;

        // Si hay varias copias con el mismo nombre, la más reciente es la apuesta razonable.
        return matches
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .First()
            .FullName;
    }

    private static IEnumerable<string> SearchRoots()
    {
        var roots = new List<string>();

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
                !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add(Environment.GetEnvironmentVariable("OneDrive"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                Add(drive.RootDirectory.FullName);
        }

        return roots;
    }

    private static void SearchDirectory(string dir, string targetFile, List<string> matches,
        DateTime deadline, int depth, int maxDepth)
    {
        if (depth > maxDepth || DateTime.UtcNow > deadline) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.pbix"))
            {
                if (string.Equals(Path.GetFileName(file), targetFile, StringComparison.OrdinalIgnoreCase))
                    matches.Add(file);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var sub in subdirs)
        {
            if (DateTime.UtcNow > deadline) return;
            if (ShouldSkip(sub)) continue;
            SearchDirectory(sub, targetFile, matches, deadline, depth + 1, maxDepth);
        }
    }

    /// <summary>Carpetas que nunca contienen tableros del usuario y solo hacen lenta la búsqueda.</summary>
    private static bool ShouldSkip(string dir)
    {
        var name = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(name)) return true;
        if (name.StartsWith('$')) return true;

        return name.Equals("Windows", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AppData", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".git", StringComparison.OrdinalIgnoreCase);
    }
}
