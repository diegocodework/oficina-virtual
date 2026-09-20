using System;
using System.Collections.Generic;

namespace OficinaVirtual.Services;

/// <summary>
/// Vigila las ventanas nativas embebidas usando las APIs de Windows pensadas para observar
/// ventanas de OTROS procesos (SetWinEventHook), en vez de "subclassing" cruzado de procesos
/// (SetWindowLongPtr con GWLP_WNDPROC) — esa técnica no es fiable entre procesos distintos: el
/// puntero de función que le pasamos pertenece a nuestro proceso y no tiene sentido en el espacio
/// de memoria de la app embebida, así que sus mensajes nunca nos llegaban de verdad.
///
/// Expone <see cref="ExternalRectChanged"/>: la ventana cambió de posición/tamaño (la moviéramos
/// nosotros o la propia app) — se usa para que el marco WPF la siga y ambos se vean como un bloque.
/// El "clic sobre una ventana tapada" se resuelve aparte, con <see cref="TryResolveClick"/>, a
/// partir del hook de ratón global (ver <see cref="GlobalMouseHook"/>).
/// </summary>
internal sealed class EmbeddedWindowGuard : IDisposable
{
    private readonly HashSet<IntPtr> _trackedHwnds = new();
    private readonly Dictionary<IntPtr, (int X, int Y, int Width, int Height)> _lastKnownRects = new();

    private readonly Win32.WinEventDelegate _winEventProc; // mantiene viva la referencia
    private IntPtr _winEventHook;

    public event Action<IntPtr, int, int, int, int>? ExternalRectChanged;

    public EmbeddedWindowGuard()
    {
        _winEventProc = OnWinEvent;
        _winEventHook = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
    }

    public void Attach(IntPtr hwnd) => _trackedHwnds.Add(hwnd);

    public void Detach(IntPtr hwnd)
    {
        _trackedHwnds.Remove(hwnd);
        _lastKnownRects.Remove(hwnd);
    }

    /// <summary>Registra la posición/tamaño que NOSOTROS acabamos de pedir (justo antes de MoveWindow),
    /// para no reaccionar a nuestro propio cambio como si fuera una "sorpresa" de la app.</summary>
    public void SetKnownRect(IntPtr hwnd, int x, int y, int width, int height) =>
        _lastKnownRects[hwnd] = (x, y, width, height);

    /// <summary>
    /// Dado el hwnd que hay literalmente bajo el cursor (de <c>WindowFromPoint</c>), busca hacia
    /// arriba en la cadena de padres hasta dar con una de nuestras ventanas embebidas (el clic
    /// puede haber caído en un control interno de la app, hijo a su vez del hwnd que embebimos).
    /// </summary>
    public bool TryResolveClick(IntPtr hwndAtPoint, out IntPtr trackedHwnd)
    {
        var current = hwndAtPoint;
        while (current != IntPtr.Zero)
        {
            if (_trackedHwnds.Contains(current))
            {
                trackedHwnd = current;
                return true;
            }
            current = Win32.GetParent(current);
        }

        trackedHwnd = IntPtr.Zero;
        return false;
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint idEventTime)
    {
        if (idObject != Win32.OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        if (!_trackedHwnds.Contains(hwnd)) return;
        if (!Win32.GetWindowRect(hwnd, out var rect)) return;

        var actual = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (_lastKnownRects.TryGetValue(hwnd, out var known) && known == actual) return; // eco de un cambio nuestro

        _lastKnownRects[hwnd] = actual;
        ExternalRectChanged?.Invoke(hwnd, actual.Item1, actual.Item2, actual.Item3, actual.Item4);
    }

    public void Dispose()
    {
        if (_winEventHook == IntPtr.Zero) return;
        Win32.UnhookWinEvent(_winEventHook);
        _winEventHook = IntPtr.Zero;
    }
}
