using System.IO;

namespace OficinaVirtual.Services;

/// <summary>
/// Registro de errores en %LOCALAPPDATA%\OficinaVirtual\crash.log — para no depender del Visor de
/// sucesos de Windows para saber qué ha fallado. Nunca lanza: si no puede escribir, lo ignora.
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OficinaVirtual", "crash.log");

    public static void Write(string context, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Registrar un error nunca debe provocar otro.
        }
    }
}
