using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace OficinaVirtual.Services;

internal sealed class OpenWindowEntry
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public override string ToString() => Title;
}

/// <summary>
/// Enumera las ventanas de nivel superior abiertas en el escritorio, para poder elegir cuál embeber.
/// </summary>
internal static class OpenWindowsService
{
    public static List<OpenWindowEntry> ListTopLevelWindows()
    {
        var entries = new List<OpenWindowEntry>();

        Win32.EnumWindows((hWnd, _) =>
        {
            if (TryGetEmbeddableTitle(hWnd, out var title))
                entries.Add(new OpenWindowEntry { Handle = hWnd, Title = title });
            return true;
        }, IntPtr.Zero);

        return entries;
    }

    /// <summary>
    /// Comprueba si un hwnd concreto es "embebible" (ventana de escritorio real, no una herramienta
    /// ni una ventana oculta/nuestra) y, si lo es, da su título. Misma lógica de filtrado que usa
    /// <see cref="ListTopLevelWindows"/>, extraída para poder validar un hwnd suelto — p.ej. el que
    /// hay bajo el cursor al detectar un posible arrastre desde el escritorio.
    /// </summary>
    public static bool TryGetEmbeddableTitle(IntPtr hWnd, out string title)
    {
        title = string.Empty;

        if (!Win32.IsWindowVisible(hWnd)) return false;
        if (Win32.GetWindow(hWnd, Win32.GW_OWNER) != IntPtr.Zero) return false;

        // Ventanas "herramienta" (tooltips, paneles ocultos, etc.) no cuentan como apps reales,
        // salvo que se marquen explícitamente como ventana de aplicación.
        int exStyle = Win32.GetWindowLong(hWnd, Win32.GWL_EXSTYLE);
        bool isToolWindow = (exStyle & Win32.WS_EX_TOOLWINDOW) != 0;
        bool isAppWindow = (exStyle & Win32.WS_EX_APPWINDOW) != 0;
        if (isToolWindow && !isAppWindow) return false;

        // Ventanas "cloaked": existen pero no se ven en el escritorio actual
        // (apps UWP suspendidas o en otro escritorio virtual).
        if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        int len = Win32.GetWindowTextLength(hWnd);
        if (len == 0) return false;

        var sb = new StringBuilder(len + 1);
        Win32.GetWindowText(hWnd, sb, sb.Capacity);
        string text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text)) return false;

        Win32.GetWindowThreadProcessId(hWnd, out int pid);
        if (pid == Process.GetCurrentProcess().Id) return false;

        title = text;
        return true;
    }

    /// <summary>
    /// Ruta del ejecutable dueño de una ventana — se usa tanto como último recurso para el icono
    /// (<see cref="WindowIconService"/>) como para saber qué relanzar al restaurar un workspace
    /// guardado (<see cref="WorkspaceService"/>).
    /// </summary>
    public static string? GetExecutablePath(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == 0) return null;

        IntPtr process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (Win32.QueryFullProcessImageName(process, 0, sb, ref size) == 0) return null;

            string path = sb.ToString();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }
}
