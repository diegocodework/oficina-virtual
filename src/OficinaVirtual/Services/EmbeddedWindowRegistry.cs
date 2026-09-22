namespace OficinaVirtual.Services;

/// <summary>
/// Registro global (todas las pestañas) de las ventanas ajenas que tenemos embebidas, con su estilo
/// original. Una ventana embebida es hija de la nuestra: si nuestro proceso muere, Windows la
/// destruye con él. Este registro permite soltarlas todas — minimizadas en la barra de tareas,
/// nunca cerradas — tanto al salir de verdad como en una emergencia (error fatal), desde cualquier
/// hilo.
/// </summary>
internal static class EmbeddedWindowRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, int> OriginalStyles = new();

    public static void Register(IntPtr hwnd, int originalStyle)
    {
        lock (Gate) OriginalStyles[hwnd] = originalStyle;
    }

    public static void Unregister(IntPtr hwnd)
    {
        lock (Gate) OriginalStyles.Remove(hwnd);
    }

    /// <summary>
    /// Devuelve la ventana a ser independiente (estilo original, sin padre) y la deja minimizada en
    /// la barra de tareas: no se cierra, no pide guardar nada y no aparece en medio del escritorio.
    /// </summary>
    public static void ReleaseMinimized(IntPtr hwnd, int originalStyle)
    {
        if (!DetachHidden(hwnd, originalStyle)) return;
        Win32.ShowWindow(hwnd, Win32.SW_SHOWMINNOACTIVE);
    }

    /// <summary>
    /// Para apps que al pulsar su X en realidad se esconden en su propia bandeja (Spotify,
    /// Discord...): vuelve a ser una ventana normal e independiente, pero se queda oculta como la
    /// app quería — se recupera desde el icono de bandeja de la propia app.
    /// </summary>
    public static void ReleaseHidden(IntPtr hwnd, int originalStyle) => DetachHidden(hwnd, originalStyle);

    /// <summary>
    /// "Salir de verdad" / cerrar una pestaña: la herramienta se cierra como si se pulsara su propia
    /// X. Primero deja de ser hija nuestra (su cierre y su posible aviso de "guardar cambios" no
    /// dependen de nuestra ventana) sin llegar a mostrarse en el escritorio.
    /// </summary>
    public static void BeginClose(IntPtr hwnd, int originalStyle)
    {
        if (!DetachHidden(hwnd, originalStyle)) return;
        Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Espera a que las herramientas a las que se pidió cerrar terminen. Las que sigan abiertas pasado
    /// el margen (están preguntando si guardar, o el usuario canceló) se muestran normalmente — nunca
    /// quedan vivas pero invisibles e inalcanzables.
    /// </summary>
    public static async Task RevealSurvivorsAsync(IReadOnlyCollection<IntPtr> hwnds, TimeSpan grace)
    {
        if (hwnds.Count == 0) return;

        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline && hwnds.Any(Win32.IsWindow))
            await Task.Delay(100);

        foreach (var hwnd in hwnds)
        {
            if (Win32.IsWindow(hwnd))
                Win32.ShowWindow(hwnd, Win32.SW_SHOWNORMAL);
        }
    }

    /// <summary>
    /// Oculta, devuelve el estilo original y quita el padre, en ese orden: ocultar primero evita que
    /// la ventana aparezca un instante en el escritorio en una posición rara al dejar de ser hija.
    /// </summary>
    private static bool DetachHidden(IntPtr hwnd, int originalStyle)
    {
        Unregister(hwnd);
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;

        Win32.ShowWindow(hwnd, Win32.SW_HIDE);
        Win32.SetWindowRgn(hwnd, IntPtr.Zero, false); // sin el recorte de la oficina: si no, saldría cortada
        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, originalStyle);
        Win32.SetParent(hwnd, IntPtr.Zero);
        // Posición "normal" razonable para cuando vuelva a mostrarse (como hija, sus coordenadas
        // eran relativas a nuestra ventana, no a la pantalla).
        Win32.MoveWindow(hwnd, 100, 100, 1000, 700, false);
        return true;
    }

    /// <summary>Suelta todas las ventanas registradas. Pensado para errores fatales: no toca WPF.</summary>
    public static void EmergencyReleaseAll()
    {
        KeyValuePair<IntPtr, int>[] snapshot;
        lock (Gate) snapshot = OriginalStyles.ToArray();

        foreach (var (hwnd, style) in snapshot)
        {
            try
            {
                ReleaseMinimized(hwnd, style);
            }
            catch
            {
                // Seguir con las demás pase lo que pase.
            }
        }
    }
}
