using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ClipTest;

/// <summary>
/// Prototipo de la "fase 2 visual" de Oficina Virtual: comprobar si recortar una ventana nativa
/// embebida con SetWindowRgn impide que tape la barra superior y la cabecera de otro marco, sin
/// mover ni redimensionar la ventana, con Bloc de notas, Chrome y VS Code.
/// </summary>
public partial class MainWindow : Window
{
    private const double HeaderHeight = 28;
    private const double NativeWidth = 700;
    private const double NativeHeight = 450;

    private IntPtr _embedded = IntPtr.Zero;
    private int _originalStyle;
    private double _natX = 100, _natY = 120; // posición (DIP) del contenido nativo dentro del viewport
    private bool _regionExpected;
    private int _regionLost;
    private int _barClicks, _overlayClicks;

    private FrameworkElement? _dragging;
    private Point _dragOffset;

    private readonly DispatcherTimer _checkTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { RefreshWindows(); PlaceHeader(); };
        SizeChanged += (_, _) => { RepositionNative(); UpdateClip(); };
        Closing += (_, _) => ReleaseEmbedded();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ReleaseEmbedded();
        Dispatcher.UnhandledException += (_, e) =>
        {
            StatusText.Text = "ERROR: " + e.Exception.Message;
            e.Handled = true;
        };
        _checkTimer.Tick += (_, _) => CheckRegion();
        _checkTimer.Start();
    }

    // ---------- Lista de ventanas ----------

    private sealed record WindowItem(IntPtr Hwnd, string Label);

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var items = new List<WindowItem>();
        int ownPid = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            if (sb.Length == 0) return true;
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == ownPid) return true;
            string proc = "?";
            try { proc = Process.GetProcessById(pid).ProcessName; } catch { }
            items.Add(new WindowItem(hwnd, $"[{proc}] {sb}"));
            return true;
        }, IntPtr.Zero);
        WindowsCombo.ItemsSource = items;
    }

    // ---------- Embeber / soltar ----------

    private void Embed_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsCombo.SelectedItem is not WindowItem item) return;
        ReleaseEmbedded();

        IntPtr hwnd = item.Hwnd;
        if (!IsWindow(hwnd)) { RefreshWindows(); return; }
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        _originalStyle = GetWindowLong(hwnd, GWL_STYLE);
        int style = _originalStyle & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
        style |= WS_CHILD;
        SetWindowLong(hwnd, GWL_STYLE, style);

        SetParent(hwnd, new WindowInteropHelper(this).Handle);
        _embedded = hwnd;
        ShowWindow(hwnd, SW_SHOW);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE);

        RepositionNative();
        UpdateClip();
        StatusText.Text = "Embebida: " + item.Label;
    }

    private void Release_Click(object sender, RoutedEventArgs e) => ReleaseEmbedded();

    private void ReleaseEmbedded()
    {
        if (_embedded == IntPtr.Zero) return;
        IntPtr hwnd = _embedded;
        _embedded = IntPtr.Zero;
        _regionExpected = false;
        if (!IsWindow(hwnd)) return;

        SetWindowRgn(hwnd, IntPtr.Zero, true); // sin esto saldría recortada al escritorio
        SetWindowLong(hwnd, GWL_STYLE, _originalStyle);
        SetParent(hwnd, IntPtr.Zero);
        MoveWindow(hwnd, 100, 100, 1000, 700, true);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        ShowWindow(hwnd, SW_SHOW);
        StatusText.Text = "Soltada al escritorio.";
    }

    // ---------- Posición y recorte ----------

    private void PlaceHeader()
    {
        Canvas.SetLeft(FrameHeader, _natX);
        Canvas.SetTop(FrameHeader, _natY - HeaderHeight);
    }

    /// <summary>Rectángulo de un área del viewport (en DIP) convertido a píxeles de cliente de esta ventana.</summary>
    private RECT ViewportToClientPx(double x, double y, double w, double h)
    {
        IntPtr me = new WindowInteropHelper(this).Handle;
        var p0 = Viewport.PointToScreen(new Point(x, y));   // PointToScreen ya devuelve píxeles físicos
        var p1 = Viewport.PointToScreen(new Point(x + w, y + h));
        var a = new POINT { X = (int)Math.Round(p0.X), Y = (int)Math.Round(p0.Y) };
        var b = new POINT { X = (int)Math.Round(p1.X), Y = (int)Math.Round(p1.Y) };
        ScreenToClient(me, ref a);
        ScreenToClient(me, ref b);
        return new RECT { Left = a.X, Top = a.Y, Right = b.X, Bottom = b.Y };
    }

    private void RepositionNative()
    {
        if (_embedded == IntPtr.Zero || PresentationSource.FromVisual(Viewport) == null) return;
        var r = ViewportToClientPx(_natX, _natY, NativeWidth, NativeHeight);
        MoveWindow(_embedded, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, true);
    }

    /// <summary>
    /// Región visible = rectángulo propio ∩ viewport − cabecera del marco que está delante.
    /// Todo en coordenadas de la propia ventana embebida (origen = su esquina superior izquierda).
    /// </summary>
    private void UpdateClip()
    {
        if (_embedded == IntPtr.Zero || PresentationSource.FromVisual(Viewport) == null) return;

        if (ClipCheck.IsChecked != true)
        {
            SetWindowRgn(_embedded, IntPtr.Zero, true);
            _regionExpected = false;
            return;
        }

        var own = ViewportToClientPx(_natX, _natY, NativeWidth, NativeHeight);
        var view = ViewportToClientPx(0, 0, Viewport.ActualWidth, Viewport.ActualHeight);
        var ovl = ViewportToClientPx(Canvas.GetLeft(OverlayHeader), Canvas.GetTop(OverlayHeader),
                                     OverlayHeader.ActualWidth, OverlayHeader.ActualHeight);
        int w = own.Right - own.Left, h = own.Bottom - own.Top;

        IntPtr rgn = CreateRectRgn(0, 0, w, h);
        IntPtr viewRgn = CreateRectRgn(view.Left - own.Left, view.Top - own.Top, view.Right - own.Left, view.Bottom - own.Top);
        IntPtr ovlRgn = CreateRectRgn(ovl.Left - own.Left, ovl.Top - own.Top, ovl.Right - own.Left, ovl.Bottom - own.Top);
        CombineRgn(rgn, rgn, viewRgn, RGN_AND);
        CombineRgn(rgn, rgn, ovlRgn, RGN_DIFF);
        DeleteObject(viewRgn);
        DeleteObject(ovlRgn);

        // Tras un SetWindowRgn correcto la región pasa a ser del sistema: no se borra.
        if (SetWindowRgn(_embedded, rgn, true) == 0) DeleteObject(rgn);
        _regionExpected = true;
    }

    /// <summary>Detecta si la app nos quita la región por su cuenta (p.ej. al redimensionarse) y la repone.</summary>
    private void CheckRegion()
    {
        if (_embedded == IntPtr.Zero) return;
        if (!IsWindow(_embedded)) { _embedded = IntPtr.Zero; StatusText.Text = "La ventana se cerró."; return; }
        if (!_regionExpected) return;

        IntPtr tmp = CreateRectRgn(0, 0, 0, 0);
        int type = GetWindowRgn(_embedded, tmp);
        DeleteObject(tmp);
        if (type == 0) // ERROR = sin región: la app la ha quitado
        {
            _regionLost++;
            UpdateClip();
        }
        Title = $"ClipTest — región perdida y repuesta: {_regionLost} veces";
    }

    private void ClipCheck_Changed(object sender, RoutedEventArgs e) => UpdateClip();

    // ---------- Arrastre y clics de prueba ----------

    private void FrameHeader_Down(object sender, MouseButtonEventArgs e) => StartDrag(FrameHeader, e);

    private void Overlay_Down(object sender, MouseButtonEventArgs e)
    {
        _overlayClicks++;
        OverlayText.Text = $"  Cabecera de otro marco (delante) — clics: {_overlayClicks}";
        StartDrag(OverlayHeader, e);
    }

    private void StartDrag(FrameworkElement el, MouseButtonEventArgs e)
    {
        _dragging = el;
        var p = e.GetPosition(Viewport);
        _dragOffset = new Point(p.X - Canvas.GetLeft(el), p.Y - Canvas.GetTop(el));
        el.CaptureMouse();
        e.Handled = true;
    }

    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (_dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Viewport);
        double left = p.X - _dragOffset.X, top = p.Y - _dragOffset.Y;
        Canvas.SetLeft(_dragging, left);
        Canvas.SetTop(_dragging, top);
        if (_dragging == FrameHeader)
        {
            _natX = left;
            _natY = top + HeaderHeight;
            RepositionNative();
        }
        UpdateClip();
    }

    private void Drag_Up(object sender, MouseButtonEventArgs e)
    {
        _dragging?.ReleaseMouseCapture();
        _dragging = null;
    }

    private void BarTest_Click(object sender, RoutedEventArgs e)
    {
        _barClicks++;
        BarTestButton.Content = $"Clic de prueba (barra): {_barClicks}";
    }

    // ---------- Win32 ----------

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const uint GW_OWNER = 4;
    private const int SW_SHOW = 5, SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    private const int RGN_AND = 1, RGN_DIFF = 4;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr rgn);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
