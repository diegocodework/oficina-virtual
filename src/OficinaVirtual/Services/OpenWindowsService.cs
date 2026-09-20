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
        var myProcessId = Process.GetCurrentProcess().Id;

        Win32.EnumWindows((hWnd, _) =>
        {
            if (!Win32.IsWindowVisible(hWnd)) return true;
            if (Win32.GetWindow(hWnd, Win32.GW_OWNER) != IntPtr.Zero) return true;

            // Ventanas "herramienta" (tooltips, paneles ocultos, etc.) no cuentan como apps reales,
            // salvo que se marquen explícitamente como ventana de aplicación.
            int exStyle = Win32.GetWindowLong(hWnd, Win32.GWL_EXSTYLE);
            bool isToolWindow = (exStyle & Win32.WS_EX_TOOLWINDOW) != 0;
            bool isAppWindow = (exStyle & Win32.WS_EX_APPWINDOW) != 0;
            if (isToolWindow && !isAppWindow) return true;

            // Ventanas "cloaked": existen pero no se ven en el escritorio actual
            // (apps UWP suspendidas o en otro escritorio virtual).
            if (Win32.DwmGetWindowAttribute(hWnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int len = Win32.GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            Win32.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            Win32.GetWindowThreadProcessId(hWnd, out int pid);
            if (pid == myProcessId) return true;

            entries.Add(new OpenWindowEntry { Handle = hWnd, Title = title });
            return true;
        }, IntPtr.Zero);

        return entries;
    }
}
