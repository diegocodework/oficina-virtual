using System.IO;
using System.Runtime.InteropServices;

namespace OficinaVirtual.Services;

/// <summary>
/// Encuentra el acceso directo (.lnk) con el que el usuario abre normalmente una app — barra de
/// tareas, menú Inicio o escritorio —, para relanzarla igual: con los colores y la fuente de su
/// consola, su carpeta de inicio, etc. (lanzar el .exe a secas usa los valores por defecto).
/// Solo se aceptan accesos directos sin argumentos, para no lanzar variantes especiales.
/// </summary>
internal static class ShortcutFinder
{
    // Ejecutable → sus accesos directos sin argumentos, en orden de preferencia.
    private static Dictionary<string, List<string>>? _shortcutsByTarget;

    /// <summary>El acceso directo más probable con el que el usuario abre esta app.</summary>
    public static string? FindShortcutFor(string executablePath) =>
        ShortcutsFor(executablePath).FirstOrDefault(File.Exists);

    /// <summary>Un acceso directo a esta app con exactamente ese nombre (sin ".lnk"), si existe.</summary>
    public static string? FindShortcutNamed(string executablePath, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return ShortcutsFor(executablePath).FirstOrDefault(s =>
            File.Exists(s) && string.Equals(Path.GetFileNameWithoutExtension(s), name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ShortcutsFor(string executablePath)
    {
        if (_shortcutsByTarget == null)
        {
            try
            {
                _shortcutsByTarget = BuildMap();
            }
            catch (Exception ex)
            {
                CrashLog.Write("ShortcutFinder.BuildMap", ex);
                _shortcutsByTarget = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        return _shortcutsByTarget.TryGetValue(Normalize(executablePath), out var shortcuts)
            ? shortcuts
            : Enumerable.Empty<string>();
    }

    /// <summary>Ruta de destino de un .lnk (null si no se puede leer o no apunta a un archivo).</summary>
    public static string? ResolveTarget(string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            return ResolveWith(shell!, shortcutPath).Target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static Dictionary<string, List<string>> BuildMap()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return map;

        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            // El orden importa: primero lo más probable (barra de tareas, menú Inicio, escritorio).
            foreach (var dir in SearchDirectories())
            {
                foreach (var shortcut in EnumerateShortcuts(dir))
                {
                    var (target, arguments) = ResolveWith(shell, shortcut);
                    if (string.IsNullOrWhiteSpace(target) || !string.IsNullOrWhiteSpace(arguments)) continue;

                    var key = Normalize(target);
                    if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
                    list.Add(shortcut);
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        return map;
    }

    private static (string? Target, string? Arguments) ResolveWith(object shell, string shortcutPath)
    {
        try
        {
            dynamic link = ((dynamic)shell).CreateShortcut(shortcutPath);
            try
            {
                return ((string)link.TargetPath, (string)link.Arguments);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        catch
        {
            return (null, null);
        }
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static IEnumerable<string> EnumerateShortcuts(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateFiles(directory, "*.lnk",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch
        {
            return path;
        }
    }
}
