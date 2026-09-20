using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using OficinaVirtual.Services;

namespace OficinaVirtual.Views;

/// <summary>
/// Una pestaña: canvas infinito (pan/zoom) donde se colocan marcos con ventanas nativas embebidas.
/// </summary>
public partial class CanvasTabView : UserControl
{
    private readonly Dictionary<IntPtr, int> _originalStyles = new();
    private readonly EmbeddedWindowGuard _guard = new();
    private readonly GlobalMouseHook _mouseHook = new();
    private EmbeddedWindowFrame? _selectedFrame;
    private int _topZIndex;

    private readonly HashSet<EmbeddedWindowFrame> _pendingReposition = new();
    private bool _repositionScheduled;

    // --- Rejilla de colocación de iconos ---
    // Cada icono tiene una celda "hogar" (la que el usuario le dio a mano), que solo cambia si el
    // usuario lo vuelve a arrastrar. RelayoutIcons() decide, cada vez que puede haber cambiado algo,
    // dónde se ve realmente cada icono: en su hogar si está libre, o en la celda libre más cercana
    // si una ventana abierta lo tapa en ese momento — sin olvidar nunca el hogar.
    private const double IconCellSize = 104;
    private readonly Dictionary<EmbeddedWindowFrame, (int Col, int Row)> _iconHomeCells = new();

    private bool _panning;
    private Point _panStart;
    private double _translateStartX;
    private double _translateStartY;

    public CanvasTabView()
    {
        InitializeComponent();
        RefreshWindowList();

        _guard.ExternalRectChanged += OnNativeWindowExternallyMoved;
        _mouseHook.LeftButtonDown += OnGlobalLeftButtonDown;
    }

    /// <summary>
    /// Un clic ha caído literalmente sobre el contenido nativo de una ventana embebida (ese clic
    /// nunca pasa por nuestros controles WPF, así que es la única forma de enterarnos). Si es así,
    /// traemos su marco al frente junto con el contenido.
    /// </summary>
    private void OnGlobalLeftButtonDown(Point rawScreenPoint)
    {
        var hwndAtPoint = Win32.WindowFromPoint(new Win32.POINT { X = (int)rawScreenPoint.X, Y = (int)rawScreenPoint.Y });
        if (hwndAtPoint == IntPtr.Zero) return;
        if (!_guard.TryResolveClick(hwndAtPoint, out var trackedHwnd)) return;

        var frame = InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().FirstOrDefault(f => f.TargetHwnd == trackedHwnd);
        if (frame == null || ReferenceEquals(frame, _selectedFrame)) return;

        // El hook corre de forma síncrona dentro del bucle de mensajes; diferimos a un tick propio
        // para no tocar la UI/Win32 desde dentro de la pila del propio callback nativo.
        Dispatcher.BeginInvoke(new Action(() => SelectFrame(frame)));
    }

    private void OnNativeWindowExternallyMoved(IntPtr hwnd, int x, int y, int width, int height)
    {
        var frame = InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().FirstOrDefault(f => f.TargetHwnd == hwnd);
        if (frame == null || frame.CurrentMode != EmbeddedWindowFrame.WindowMode.Expanded) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            SyncFrameFromNativeRect(frame, x, y, width, height);
            RelayoutIcons();
        }));
    }

    /// <summary>
    /// Inverso de <see cref="RepositionFrame"/>: a partir de la posición/tamaño real en pantalla
    /// (píxeles físicos, tal como los da <c>GetWindowRect</c>) que la propia app se dio a sí misma,
    /// recalcula dónde debe quedar el marco WPF en el canvas para que ambos sigan pareciendo un
    /// solo bloque.
    /// </summary>
    private void SyncFrameFromNativeRect(EmbeddedWindowFrame frame, int screenX, int screenY, int width, int height)
    {
        var ownerWindow = Window.GetWindow(this);
        if (ownerWindow == null) return;

        var source = PresentationSource.FromVisual(ownerWindow);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (dpiX <= 0 || dpiY <= 0) return;

        double scale = CanvasScale.ScaleX;
        if (scale <= 0) return;

        // GetWindowRect da píxeles físicos; PointToScreen/CanvasTranslate trabajan en unidades
        // independientes de dispositivo (DIP) — se convierte antes de mezclarlas.
        double dipX = screenX / dpiX;
        double dipY = screenY / dpiY;
        var viewportScreen = ViewportBorder.PointToScreen(new Point(0, 0));

        double contentLeft = (dipX - viewportScreen.X - CanvasTranslate.X) / scale;
        double contentTop = (dipY - viewportScreen.Y - CanvasTranslate.Y) / scale;
        double contentWidth = width / (scale * dpiX);
        double contentHeight = height / (scale * dpiY);

        double frameWidth = contentWidth + 2 * EmbeddedWindowFrame.FrameBorderThickness;
        double frameHeight = contentHeight + EmbeddedWindowFrame.HeaderHeight + 2 * EmbeddedWindowFrame.FrameBorderThickness;
        if (frameWidth <= 0 || frameHeight <= 0) return;

        Canvas.SetLeft(frame, contentLeft - EmbeddedWindowFrame.FrameBorderThickness);
        Canvas.SetTop(frame, contentTop - EmbeddedWindowFrame.HeaderHeight - EmbeddedWindowFrame.FrameBorderThickness);
        frame.Width = frameWidth;
        frame.Height = frameHeight;
    }

    // --- Toolbar ---

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshWindowList();

    private void RefreshWindowList()
    {
        var previousSelection = (WindowsCombo.SelectedItem as OpenWindowEntry)?.Handle;
        var entries = OpenWindowsService.ListTopLevelWindows();
        WindowsCombo.ItemsSource = entries;

        var toReselect = entries.FirstOrDefault(x => x.Handle == previousSelection);
        WindowsCombo.SelectedItem = toReselect ?? entries.FirstOrDefault();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsCombo.SelectedItem is not OpenWindowEntry entry) return;

        var frame = new EmbeddedWindowFrame
        {
            TitleValue = entry.Title,
            TargetHwnd = entry.Handle,
            IconSource = WindowIconService.GetIconFor(entry.Handle)
        };
        frame.MoveOrResize += (_, _) =>
        {
            ScheduleReposition(frame);
            RelayoutIcons(); // mover/redimensionar una ventana puede tapar o destapar iconos
        };
        frame.CloseRequested += (_, _) => ReleaseFrame(frame);
        frame.Selected += (_, _) => SelectFrame(frame);
        frame.ExpandRequested += (_, _) => ExpandFrame(frame);
        frame.MinimizeRequested += (_, _) => CollapseFrame(frame);
        frame.IconDropped += (_, _) =>
        {
            SetHomeCell(frame, Canvas.GetLeft(frame), Canvas.GetTop(frame));
            RelayoutIcons();
        };

        InfiniteCanvas.Children.Add(frame);
        EmbedNative(frame);
        Win32.ShowWindow(frame.TargetHwnd, Win32.SW_HIDE); // arranca en modo icono: la ventana real permanece oculta

        // Coloca el nuevo icono cerca del centro de la zona visible actual del canvas.
        double dropX = (ViewportBorder.ActualWidth / 2 - CanvasTranslate.X) / CanvasScale.ScaleX;
        double dropY = (ViewportBorder.ActualHeight / 2 - CanvasTranslate.Y) / CanvasScale.ScaleY;
        SetHomeCell(frame, dropX, dropY);
        RelayoutIcons();

        SelectFrame(frame);
    }

    private void ExpandFrame(EmbeddedWindowFrame frame)
    {
        if (frame.CurrentMode == EmbeddedWindowFrame.WindowMode.Expanded) return;

        frame.SetMode(EmbeddedWindowFrame.WindowMode.Expanded);

        if (frame.TargetHwnd != IntPtr.Zero && Win32.IsWindow(frame.TargetHwnd))
            Win32.ShowWindow(frame.TargetHwnd, Win32.SW_SHOW);

        RepositionFrame(frame);
        BringToFront(frame);
        RelayoutIcons(); // esta ventana recién abierta puede tapar iconos
    }

    /// <summary>
    /// Sube el marco al frente tanto en WPF (Panel.ZIndex) como en Windows (SetWindowPos sobre su
    /// ventana nativa embebida, si la tiene), a la vez — para que la cabecera y el contenido real
    /// de una ventana nunca queden en órdenes distintos al solaparse con otra.
    /// </summary>
    private void BringToFront(EmbeddedWindowFrame frame)
    {
        Panel.SetZIndex(frame, ++_topZIndex);

        if (frame.CurrentMode == EmbeddedWindowFrame.WindowMode.Expanded &&
            frame.TargetHwnd != IntPtr.Zero && Win32.IsWindow(frame.TargetHwnd))
        {
            Win32.SetWindowPos(frame.TargetHwnd, Win32.HWND_TOP, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            RepositionFrame(frame); // reafirma la posición/tamaño exactos tras el cambio de orden
        }
    }

    private void CollapseFrame(EmbeddedWindowFrame frame)
    {
        if (frame.CurrentMode == EmbeddedWindowFrame.WindowMode.Icon) return;

        if (frame.TargetHwnd != IntPtr.Zero && Win32.IsWindow(frame.TargetHwnd))
            Win32.ShowWindow(frame.TargetHwnd, Win32.SW_HIDE);

        frame.SetMode(EmbeddedWindowFrame.WindowMode.Icon);
        RelayoutIcons(); // vuelve a su hogar, o al hueco libre más cercano si algo lo tapa
    }

    /// <summary>
    /// Asigna la celda "hogar" de un icono (solo cambia cuando el usuario lo arrastra, o al
    /// crearlo). Se elige evitando los hogares de los demás iconos.
    /// </summary>
    private void SetHomeCell(EmbeddedWindowFrame frame, double desiredLeft, double desiredTop)
    {
        int desiredCol = Math.Max(0, (int)Math.Round(desiredLeft / IconCellSize));
        int desiredRow = Math.Max(0, (int)Math.Round(desiredTop / IconCellSize));

        var otherHomes = _iconHomeCells.Where(kv => !ReferenceEquals(kv.Key, frame))
            .Select(kv => kv.Value).ToHashSet();

        var cell = FindFreeCell(desiredCol, desiredRow, (c, r) => !otherHomes.Contains((c, r)));
        _iconHomeCells[frame] = cell;
    }

    /// <summary>
    /// Recoloca todos los iconos: cada uno en su hogar si está libre, o en la celda libre más
    /// cercana si una ventana en modo Expandido lo tapa ahora mismo — sin tocar el hogar guardado,
    /// así que en cuanto deja de estar tapado vuelve solo a su sitio.
    /// </summary>
    private void RelayoutIcons()
    {
        var consumed = new HashSet<(int Col, int Row)>();

        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>())
        {
            if (frame.CurrentMode != EmbeddedWindowFrame.WindowMode.Icon) continue;
            if (!_iconHomeCells.TryGetValue(frame, out var home)) continue;

            var cell = !consumed.Contains(home) && !CellOverlapsExpandedWindow(home.Col, home.Row)
                ? home
                : FindFreeCell(home.Col, home.Row, (c, r) => !consumed.Contains((c, r)) && !CellOverlapsExpandedWindow(c, r));

            consumed.Add(cell);
            Canvas.SetLeft(frame, cell.Col * IconCellSize);
            Canvas.SetTop(frame, cell.Row * IconCellSize);
        }
    }

    private (int Col, int Row) FindFreeCell(int col, int row, Func<int, int, bool> isFree)
    {
        if (isFree(col, row)) return (col, row);

        for (int radius = 1; radius < 200; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                    int c = col + dx, r = row + dy;
                    if (c < 0 || r < 0) continue;
                    if (isFree(c, r)) return (c, r);
                }
            }
        }

        return (col, row);
    }

    private bool CellOverlapsExpandedWindow(int col, int row)
    {
        var cellRect = new Rect(col * IconCellSize, row * IconCellSize, EmbeddedWindowFrame.IconWidth, EmbeddedWindowFrame.IconHeight);

        foreach (var f in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>())
        {
            if (f.CurrentMode != EmbeddedWindowFrame.WindowMode.Expanded) continue;
            var frameRect = new Rect(Canvas.GetLeft(f), Canvas.GetTop(f), f.Width, f.Height);
            if (cellRect.IntersectsWith(frameRect)) return true;
        }

        return false;
    }

    private void ReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFrame == null) return;
        ReleaseFrame(_selectedFrame);
    }

    private void SelectFrame(EmbeddedWindowFrame frame)
    {
        _selectedFrame?.SetSelected(false);
        _selectedFrame = frame;
        _selectedFrame.SetSelected(true);
        BringToFront(frame);
    }

    private void DeselectAll()
    {
        _selectedFrame?.SetSelected(false);
        _selectedFrame = null;
    }

    // --- Pan y zoom del canvas ---

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DeselectAll();

        _panning = true;
        _panStart = e.GetPosition(ViewportBorder);
        _translateStartX = CanvasTranslate.X;
        _translateStartY = CanvasTranslate.Y;
        ViewportBorder.CaptureMouse();
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;

        var current = e.GetPosition(ViewportBorder);
        CanvasTranslate.X = _translateStartX + (current.X - _panStart.X);
        CanvasTranslate.Y = _translateStartY + (current.Y - _panStart.Y);
        RepositionAllFrames();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        ViewportBorder.ReleaseMouseCapture();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(ViewportBorder);
        double oldScale = CanvasScale.ScaleX;
        double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        double newScale = Math.Clamp(oldScale * factor, 0.2, 3.0);

        double canvasX = (pos.X - CanvasTranslate.X) / oldScale;
        double canvasY = (pos.Y - CanvasTranslate.Y) / oldScale;

        CanvasScale.ScaleX = newScale;
        CanvasScale.ScaleY = newScale;
        CanvasTranslate.X = pos.X - canvasX * newScale;
        CanvasTranslate.Y = pos.Y - canvasY * newScale;

        ZoomText.Text = $"{Math.Round(newScale * 100)}%";
        RepositionAllFrames();
    }

    // --- Embedding nativo ---

    private void EmbedNative(EmbeddedWindowFrame frame)
    {
        var ownerWindow = Window.GetWindow(this);
        if (ownerWindow == null) return;

        IntPtr hwnd = frame.TargetHwnd;
        IntPtr parentHwnd = new WindowInteropHelper(ownerWindow).Handle;

        int originalStyle = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        _originalStyles[hwnd] = originalStyle;

        int newStyle = originalStyle;
        newStyle &= ~Win32.WS_POPUP;
        newStyle &= ~Win32.WS_CAPTION;
        newStyle &= ~Win32.WS_THICKFRAME;
        newStyle &= ~Win32.WS_SYSMENU;
        newStyle |= Win32.WS_CHILD;
        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, newStyle);

        Win32.SetParent(hwnd, parentHwnd);
        Win32.ShowWindow(hwnd, Win32.SW_SHOW);

        _guard.Attach(hwnd);
    }

    private void ReleaseFrame(EmbeddedWindowFrame frame)
    {
        RestoreNativeWindow(frame.TargetHwnd);
        InfiniteCanvas.Children.Remove(frame);
        _iconHomeCells.Remove(frame);
        if (ReferenceEquals(_selectedFrame, frame)) _selectedFrame = null;
        RelayoutIcons(); // liberar una ventana expandida puede destapar iconos
    }

    private void RestoreNativeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;
        if (!_originalStyles.TryGetValue(hwnd, out int originalStyle)) return;

        _guard.Detach(hwnd);

        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, originalStyle);
        Win32.SetParent(hwnd, IntPtr.Zero);
        Win32.MoveWindow(hwnd, 100, 100, 800, 600, true);
        Win32.ShowWindow(hwnd, Win32.SW_SHOW);

        _originalStyles.Remove(hwnd);
    }

    /// <summary>
    /// Agrupa varias peticiones de reposicionamiento (p.ej. muchos eventos DragDelta seguidos al
    /// redimensionar) en una sola actualización por fotograma, para no desincronizar el marco WPF
    /// y la ventana nativa.
    /// </summary>
    private void ScheduleReposition(EmbeddedWindowFrame frame)
    {
        _pendingReposition.Add(frame);
        if (_repositionScheduled) return;

        _repositionScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _repositionScheduled = false;
            var frames = _pendingReposition.ToList();
            _pendingReposition.Clear();
            foreach (var f in frames) RepositionFrame(f);
        }));
    }

    /// <summary>
    /// Calcula la posición/tamaño en pantalla de la zona de contenido del marco de forma algebraica
    /// (Canvas.Left/Top, Width/Height, escala y traslación del canvas) en vez de leer ActualWidth/
    /// PointToScreen del árbol visual, que dependen de que WPF ya haya terminado su pasada de layout.
    /// Eso evita que MoveWindow reciba un tamaño "de un fotograma atrás" durante el arrastre.
    /// </summary>
    private void RepositionFrame(EmbeddedWindowFrame frame)
    {
        if (frame.TargetHwnd == IntPtr.Zero) return;

        var ownerWindow = Window.GetWindow(this);
        if (ownerWindow == null) return;

        double left = Canvas.GetLeft(frame);
        double top = Canvas.GetTop(frame);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        double contentLeft = left + EmbeddedWindowFrame.FrameBorderThickness;
        double contentTop = top + EmbeddedWindowFrame.HeaderHeight + EmbeddedWindowFrame.FrameBorderThickness;
        double contentWidth = frame.Width - 2 * EmbeddedWindowFrame.FrameBorderThickness;
        double contentHeight = frame.Height - EmbeddedWindowFrame.HeaderHeight - 2 * EmbeddedWindowFrame.FrameBorderThickness;
        if (contentWidth <= 0 || contentHeight <= 0) return;

        double scale = CanvasScale.ScaleX;
        var viewportScreen = ViewportBorder.PointToScreen(new Point(0, 0));

        var screenTopLeft = new Point(
            viewportScreen.X + contentLeft * scale + CanvasTranslate.X,
            viewportScreen.Y + contentTop * scale + CanvasTranslate.Y);

        var clientTopLeft = ownerWindow.PointFromScreen(screenTopLeft);

        var source = PresentationSource.FromVisual(ownerWindow);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        int x = (int)Math.Round(clientTopLeft.X * dpiX);
        int y = (int)Math.Round(clientTopLeft.Y * dpiY);
        int width = (int)Math.Round(contentWidth * scale * dpiX);
        int height = (int)Math.Round(contentHeight * scale * dpiY);
        if (width <= 0 || height <= 0) return;

        // El "known rect" se guarda en coordenadas de PANTALLA (como GetWindowRect), para poder
        // compararlo con lo que informe SetWinEventHook; MoveWindow en cambio necesita coordenadas
        // de CLIENTE del padre (x,y calculados arriba).
        int screenX = (int)Math.Round(screenTopLeft.X * dpiX);
        int screenY = (int)Math.Round(screenTopLeft.Y * dpiY);
        _guard.SetKnownRect(frame.TargetHwnd, screenX, screenY, width, height);

        Win32.MoveWindow(frame.TargetHwnd, x, y, width, height, true);
    }

    private void RepositionAllFrames()
    {
        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>())
        {
            if (frame.CurrentMode == EmbeddedWindowFrame.WindowMode.Expanded)
                RepositionFrame(frame);
        }
    }

    // --- Orquestación desde MainWindow (cambio de pestaña, mover/redimensionar la ventana host) ---

    public void OnTabActivated()
    {
        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>())
        {
            if (frame.CurrentMode == EmbeddedWindowFrame.WindowMode.Expanded &&
                frame.TargetHwnd != IntPtr.Zero && Win32.IsWindow(frame.TargetHwnd))
                Win32.ShowWindow(frame.TargetHwnd, Win32.SW_SHOW);
        }
        RepositionAllFrames();
    }

    public void OnTabDeactivated()
    {
        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>())
        {
            if (frame.TargetHwnd != IntPtr.Zero && Win32.IsWindow(frame.TargetHwnd))
                Win32.ShowWindow(frame.TargetHwnd, Win32.SW_HIDE);
        }
    }

    public void NotifyHostMovedOrResized() => RepositionAllFrames();

    public void ReleaseAllEmbeddedWindows()
    {
        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().ToList())
            RestoreNativeWindow(frame.TargetHwnd);
    }

    public void ShutdownHooks()
    {
        _mouseHook.Dispose();
        _guard.Dispose();
    }
}
