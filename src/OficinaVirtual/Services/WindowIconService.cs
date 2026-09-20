using System;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OficinaVirtual.Services;

/// <summary>
/// Obtiene el icono nativo de una ventana (el mismo que se ve en su barra de tareas), para
/// mostrarlo en el cuadrado de "modo icono".
/// </summary>
internal static class WindowIconService
{
    public static ImageSource? GetIconFor(IntPtr hwnd)
    {
        IntPtr hIcon = GetIconHandle(hwnd);
        if (hIcon == IntPtr.Zero) return null;

        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(hIcon, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr GetIconHandle(IntPtr hwnd)
    {
        // 1. Preguntar directamente a la ventana (WM_GETICON), con timeout por si la app no responde.
        Win32.SendMessageTimeout(hwnd, Win32.WM_GETICON, (IntPtr)Win32.ICON_BIG, IntPtr.Zero,
            Win32.SMTO_ABORTIFHUNG, 200, out IntPtr icon);
        if (icon != IntPtr.Zero) return icon;

        Win32.SendMessageTimeout(hwnd, Win32.WM_GETICON, (IntPtr)Win32.ICON_SMALL, IntPtr.Zero,
            Win32.SMTO_ABORTIFHUNG, 200, out icon);
        if (icon != IntPtr.Zero) return icon;

        // 2. Icono registrado en la clase de la ventana.
        icon = Win32.GetClassLongPtr(hwnd, Win32.GCL_HICON);
        if (icon != IntPtr.Zero) return icon;

        icon = Win32.GetClassLongPtr(hwnd, Win32.GCL_HICONSM);
        if (icon != IntPtr.Zero) return icon;

        // 3. Icono del propio ejecutable, como último recurso.
        return GetIconFromProcessExecutable(hwnd);
    }

    private static IntPtr GetIconFromProcessExecutable(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == 0) return IntPtr.Zero;

        IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return IntPtr.Zero;

        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (Win32.QueryFullProcessImageName(process, 0, sb, ref size) == 0) return IntPtr.Zero;

            string exePath = sb.ToString();
            if (string.IsNullOrEmpty(exePath)) return IntPtr.Zero;

            var large = new IntPtr[1];
            uint extracted = Win32.ExtractIconEx(exePath, 0, large, null, 1);
            return extracted > 0 ? large[0] : IntPtr.Zero;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }
}
