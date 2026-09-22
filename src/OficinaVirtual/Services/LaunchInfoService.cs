using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OficinaVirtual.Services;

/// <summary>
/// Averigua qué documento o carpeta tiene abierto una herramienta, para relanzarla con él tras
/// "Salir de verdad" o reiniciar el PC. Genérico para cualquier app, y siempre conservador: solo se
/// recuerda un archivo/carpeta que existe y cuyo nombre se ve en el título actual de la ventana — si
/// hay dudas, no se recuerda nada (la app se reabre en blanco, nunca con un documento equivocado).
/// </summary>
internal static class LaunchInfoService
{
    private const int MaxRecentShortcutsToScan = 300;

    /// <summary>Valor de LaunchShortcut que indica "relanzar el ejecutable directamente, sin acceso directo".</summary>
    public const string DirectLaunch = ":direct";

    public static string CaptureLaunchArgs(IntPtr hwnd, string executablePath, string previousArgs)
    {
        try
        {
            string title = GetCurrentTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return previousArgs;

            // a) Lo que se le pasó al abrirla (doble clic en un archivo, "code C:\proyecto"...).
            var fromCommandLine = PathsStillShown(GetCommandLineArgs(hwnd), title);
            if (fromCommandLine.Count > 0) return JoinQuoted(fromCommandLine);

            // b) Un documento guardado/abierto después con el diálogo estándar: Windows lo anota en
            //    sus documentos recientes.
            if (FindRecentDocumentForTitle(executablePath, title) is { } recent) return JoinQuoted(new[] { recent });

            // c) Lo que ya recordábamos (p.ej. de una restauración anterior), si sigue en el título.
            var previous = PathsStillShown(SplitArgs(previousArgs), title);
            return previous.Count > 0 ? JoinQuoted(previous) : "";
        }
        catch (Exception ex)
        {
            CrashLog.Write("LaunchInfoService.CaptureLaunchArgs", ex);
            return previousArgs;
        }
    }

    private static string GetCurrentTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        Win32.SendMessageTimeoutText(hwnd, Win32.WM_GETTEXT, (IntPtr)sb.Capacity, sb, Win32.SMTO_ABORTIFHUNG, 300, out _);
        return sb.ToString();
    }

    // --- a) Línea de comandos del proceso ---

    private static List<string> GetCommandLineArgs(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == 0) return new List<string>();

        IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return new List<string>();

        IntPtr buffer = IntPtr.Zero;
        try
        {
            // Primera llamada solo para saber el tamaño necesario.
            Win32.NtQueryInformationProcess(process, Win32.ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
            if (needed <= 0 || needed > 64 * 1024) return new List<string>();

            buffer = Marshal.AllocHGlobal(needed);
            if (Win32.NtQueryInformationProcess(process, Win32.ProcessCommandLineInformation, buffer, needed, out _) != 0)
                return new List<string>();

            // UNICODE_STRING { ushort Length; ushort MaximumLength; IntPtr Buffer; }
            int lengthBytes = (ushort)Marshal.ReadInt16(buffer);
            IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
            if (lengthBytes == 0 || text == IntPtr.Zero) return new List<string>();

            var args = SplitArgs(Marshal.PtrToStringUni(text, lengthBytes / 2));
            return args.Skip(1).ToList(); // el primero es el propio ejecutable
        }
        catch
        {
            return new List<string>();
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            Win32.CloseHandle(process);
        }
    }

    private static List<string> SplitArgs(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;

        IntPtr argv = Win32.CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) return result;
        try
        {
            for (int i = 0; i < count; i++)
                result.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "");
        }
        finally
        {
            Win32.LocalFree(argv);
        }
        return result;
    }

    /// <summary>Solo rutas absolutas que existen y cuyo nombre aparece en el título actual.</summary>
    private static List<string> PathsStillShown(IEnumerable<string> args, string title)
    {
        var kept = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || !Path.IsPathFullyQualified(arg)) continue;
            if (!File.Exists(arg) && !Directory.Exists(arg)) continue;
            if (TitleMentions(title, arg)) kept.Add(arg);
        }
        return kept;
    }

    private static bool TitleMentions(string title, string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(name);
        if (nameWithoutExtension.Length < 2) return false;
        return title.Contains(name, StringComparison.OrdinalIgnoreCase) ||
               title.Contains(nameWithoutExtension, StringComparison.OrdinalIgnoreCase);
    }

    // --- b) Documentos recientes de Windows ---

    private static string? FindRecentDocumentForTitle(string executablePath, string title)
    {
        string cleanTitle = title.TrimStart('*', ' ');
        var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrEmpty(recentDir) || !Directory.Exists(recentDir)) return null;

        var candidates = new DirectoryInfo(recentDir).EnumerateFiles("*.lnk")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(MaxRecentShortcutsToScan);

        foreach (var shortcut in candidates)
        {
            // El acceso reciente se llama como el documento ("nota.txt.lnk"): se filtra por el
            // título antes de gastar tiempo en resolverlo.
            string linkedName = Path.GetFileNameWithoutExtension(shortcut.Name);
            if (!TitleStartsWith(cleanTitle, linkedName) &&
                !TitleStartsWith(cleanTitle, Path.GetFileNameWithoutExtension(linkedName)))
                continue;

            string? target = ShortcutFinder.ResolveTarget(shortcut.FullName);
            if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;

            // Solo si ese tipo de archivo se abre por defecto con ESTA app: así un navegador nunca
            // "reabre" un .txt por coincidir el nombre en el título.
            if (IsDefaultAppFor(executablePath, target)) return target;
        }

        return null;
    }

    private static bool TitleStartsWith(string title, string name)
    {
        if (name.Length < 2 || !title.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        return title.Length == name.Length || !char.IsLetterOrDigit(title[name.Length]);
    }

    private static bool IsDefaultAppFor(string executablePath, string documentPath)
    {
        string extension = Path.GetExtension(documentPath);
        if (string.IsNullOrEmpty(extension)) return false;

        // Se pregunta por la acción "abrir"; en algunos equipos el tipo de archivo no tiene acción
        // predeterminada declarada y la consulta sin acción responde "sin asociación".
        string? associated = QueryOpenExecutable(extension, "open") ?? QueryOpenExecutable(extension, null);
        if (associated == null) return false;

        associated = ShortcutFinder.Normalize(associated);
        string running = ShortcutFinder.Normalize(executablePath);
        // Windows tiene a veces dos copias del mismo programa (p.ej. C:\Windows\notepad.exe y
        // System32\notepad.exe): basta con que sea el mismo ejecutable por nombre.
        return string.Equals(associated, running, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileName(associated), Path.GetFileName(running), StringComparison.OrdinalIgnoreCase);
    }

    private static string? QueryOpenExecutable(string extension, string? verb)
    {
        uint size = 1024;
        var sb = new StringBuilder((int)size);
        int hr = Win32.AssocQueryString(Win32.ASSOCF_NOTRUNCATE, Win32.ASSOCSTR_EXECUTABLE, extension, verb, sb, ref size);
        return hr == 0 && sb.Length > 0 ? sb.ToString() : null;
    }

    // --- Con qué acceso directo se abrió (colores y fuente de una consola, carpeta de inicio) ---

    /// <summary>
    /// Una consola abierta desde un acceso directo toma como título el nombre de ese acceso directo
    /// ("Win PowerShell", "Windows PowerShell"...) y de él saca sus colores y su fuente. Si el título
    /// actual coincide con un acceso directo a este mismo ejecutable, se recuerda ese acceso directo;
    /// si no, se conserva el que ya se recordaba.
    /// </summary>
    public static string CaptureLaunchShortcut(IntPtr hwnd, string executablePath, string previousShortcut)
    {
        try
        {
            string title = GetCurrentTitle(hwnd);
            foreach (var prefix in new[] { "Administrador: ", "Administrator: " })
            {
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    title = title[prefix.Length..];
            }

            title = title.Trim();
            if (ShortcutFinder.FindShortcutNamed(executablePath, title) is { } named) return named;

            // Abierta sin acceso directo (Ejecutar, escribiendo su nombre...): la consola toma como
            // título la ruta del propio ejecutable, y su aspecto viene de la configuración de consola
            // del usuario. Hay que relanzarla igual: el ejecutable a secas.
            if (title.EndsWith(Path.GetFileName(executablePath), StringComparison.OrdinalIgnoreCase))
                return DirectLaunch;

            return previousShortcut;
        }
        catch (Exception ex)
        {
            CrashLog.Write("LaunchInfoService.CaptureLaunchShortcut", ex);
            return previousShortcut;
        }
    }

    private static string JoinQuoted(IEnumerable<string> paths) =>
        string.Join(" ", paths.Select(p => "\"" + p + "\""));
}
