using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
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
    private const double IconAutoCollapseZoomThreshold = 0.4;
    private const double MinZoom = 0.2;
    private const double MaxZoom = 2.0; // por encima, las ventanas nativas se vuelven enormes y más propensas a fallos
    private const double ContentZoomForwardStep = 0.08;
    private double _lastForwardedZoomScale = 1.0;
    private readonly Dictionary<EmbeddedWindowFrame, (int Col, int Row)> _iconHomeCells = new();

    private bool _panning;
    private Point _panStart;
    private double _translateStartX;
    private double _translateStartY;

    // --- Arrastrar una ventana real del escritorio (por su barra de título) hasta el canvas ---
    private const double DragCandidateThresholdPx = 10;
    private IntPtr _dragCandidateHwnd = IntPtr.Zero;
    private Point _dragCandidateStartScreen;

    // --- Cables tipo ComfyUI entre nodos (puramente visual/organizativo por ahora) ---
    private sealed class NodeConnection
    {
        public required EmbeddedWindowFrame From { get; init; }
        public required EmbeddedWindowFrame.ConnectorSide FromSide { get; init; }
        public required EmbeddedWindowFrame To { get; init; }
        public required EmbeddedWindowFrame.ConnectorSide ToSide { get; init; }
        public required Path Visual { get; init; }
    }

    private readonly List<NodeConnection> _connections = new();
    private EmbeddedWindowFrame? _connectDragSource;
    private EmbeddedWindowFrame.ConnectorSide _connectDragSourceSide;
    private Path? _connectDragPreview;

    // --- Vigilante de apps que se cierran o se esconden por su cuenta ---
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<EmbeddedWindowFrame, int> _hiddenByAppTicks = new();

    public CanvasTabView()
    {
        InitializeComponent();
        RefreshWindowList();

        _guard.ExternalRectChanged += OnNativeWindowExternallyMoved;
        _mouseHook.LeftButtonDown += OnGlobalLeftButtonDown;
        _mouseHook.LeftButtonUp += OnGlobalLeftButtonUp;

        _watchdog.Tick += (_, _) =>
        {
            CheckEmbeddedWindowsAlive();
            ReapplyLostClipRegions();
        };
        _watchdog.Start();

        // El canvas es un lienzo de 200.000x200.000 (ver Paso 10); sin esto, la vista arrancaría en
        // su esquina superior izquierda en vez de en el centro. Se hace en Loaded porque hasta
        // entonces ViewportBorder aún no tiene su tamaño real (ActualWidth/Height a 0).
        Loaded += (_, _) => CenterCanvasView();
    }

    private bool _canvasCentered;

    private void CenterCanvasView()
    {
        // Guarda defensiva: si algo dispara Loaded más de una vez, esto no debe volver a recentrar
        // el canvas — eso desplazaba de golpe tanto los iconos como las ventanas ya expandidas hacia
        // el nuevo centro, perdiendo la posición que el usuario ya había colocado.
        if (_canvasCentered) return;
        _canvasCentered = true;

        double scale = CanvasScale.ScaleX;
        CanvasTranslate.X = ViewportBorder.ActualWidth / 2 - InfiniteCanvas.Width / 2 * scale;
        CanvasTranslate.Y = ViewportBorder.ActualHeight / 2 - InfiniteCanvas.Height / 2 * scale;
    }

    /// <summary>
    /// Un clic ha caído literalmente sobre el contenido nativo de una ventana embebida (ese clic
    /// nunca pasa por nuestros controles WPF, así que es la única forma de enterarnos). Si es así,
    /// traemos su marco al frente junto con el contenido.
    /// </summary>
    /// <summary>
    /// Los hooks globales siguen activos aunque la oficina esté oculta en la bandeja, minimizada, o
    /// esta pestaña no sea la activa. En esos casos no debe reaccionar a nada — si no, arrastrar
    /// cualquier ventana por la zona donde "estaría" el canvas podría embeberla sin querer.
    /// (UserControl.IsVisible ya es false si la pestaña no es la seleccionada o la ventana está oculta.)
    /// </summary>
    private bool IsHostInteractive()
    {
        if (!IsVisible) return false;
        var window = Window.GetWindow(this);
        return window != null && window.IsVisible && window.WindowState != WindowState.Minimized;
    }

    private void OnGlobalLeftButtonDown(Point rawScreenPoint)
    {
        if (!IsHostInteractive()) return;

        var hwndAtPoint = Win32.WindowFromPoint(new Win32.POINT { X = (int)rawScreenPoint.X, Y = (int)rawScreenPoint.Y });
        if (hwndAtPoint == IntPtr.Zero) return;

        if (_guard.TryResolveClick(hwndAtPoint, out var trackedHwnd))
        {
            var frame = InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().FirstOrDefault(f => f.TargetHwnd == trackedHwnd);
            if (frame != null && !ReferenceEquals(frame, _selectedFrame))
            {
                // El hook corre de forma síncrona dentro del bucle de mensajes; diferimos a un tick
                // propio para no tocar la UI/Win32 desde dentro de la pila del propio callback nativo.
                Dispatcher.BeginInvoke(new Action(() => SelectFrame(frame)));
            }
            if (frame != null) _ = GiveKeyboardFocusSoonAsync(trackedHwnd);
            return;
        }

        // No es un clic sobre algo ya embebido — comprobamos si es el inicio de un posible arrastre
        // de OTRA ventana del escritorio por su barra de título (para poder soltarla sobre el
        // canvas más adelante). Ya trackeada = no (evita "recapturar" algo que ya es nuestro).
        if (_originalStyles.ContainsKey(hwndAtPoint)) return;

        Win32.SendMessageTimeout(hwndAtPoint, Win32.WM_NCHITTEST, IntPtr.Zero,
            Win32.MakeLParam((int)rawScreenPoint.X, (int)rawScreenPoint.Y),
            Win32.SMTO_ABORTIFHUNG, 200, out var hitTestResult);
        if ((int)hitTestResult != Win32.HTCAPTION) return;

        if (!OpenWindowsService.TryGetEmbeddableTitle(hwndAtPoint, out _)) return;

        _dragCandidateHwnd = hwndAtPoint;
        _dragCandidateStartScreen = rawScreenPoint;
    }

    private void OnGlobalLeftButtonUp(Point rawScreenPoint)
    {
        var hwnd = _dragCandidateHwnd;
        _dragCandidateHwnd = IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        double dx = rawScreenPoint.X - _dragCandidateStartScreen.X;
        double dy = rawScreenPoint.Y - _dragCandidateStartScreen.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < DragCandidateThresholdPx) return; // fue un clic, no un arrastre

        if (!OpenWindowsService.TryGetEmbeddableTitle(hwnd, out var title)) return; // pudo cerrarse mientras se arrastraba

        Dispatcher.BeginInvoke(new Action(() => TryDropOnCanvas(hwnd, title, rawScreenPoint)));
    }

    /// <summary>
    /// Si el punto donde se soltó cae dentro del área visible del canvas (en pantalla), embebe la
    /// ventana ahí mismo, en modo icono. No se usa WindowFromPoint para esto: la ventana que se
    /// estaba arrastrando sigue tapando ese punto, así que solo la geometría del viewport sirve.
    /// </summary>
    private void TryDropOnCanvas(IntPtr hwnd, string title, Point rawScreenPoint)
    {
        // El hook global de ratón sigue vivo aunque esta vista ya no esté en el árbol visual (p.ej.
        // se cambió de pestaña, o esta vista quedó huérfana un instante antes de que sus hooks se
        // paren del todo) — PointToScreen lanza si se llama sin PresentationSource. Comprobar antes
        // evita el crash (visto en el Visor de sucesos: "Este Visual no está conectado a
        // PresentationSource"), en vez de reaccionar a un arrastre que ya no nos corresponde.
        var source = PresentationSource.FromVisual(this);
        if (source == null || !IsHostInteractive()) return;

        var viewportScreen = ViewportBorder.PointToScreen(new Point(0, 0));
        double dpiX = source.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (dpiX <= 0 || dpiY <= 0) return;

        double dipX = rawScreenPoint.X / dpiX;
        double dipY = rawScreenPoint.Y / dpiY;

        var viewportRect = new Rect(viewportScreen.X, viewportScreen.Y, ViewportBorder.ActualWidth, ViewportBorder.ActualHeight);
        if (!viewportRect.Contains(dipX, dipY)) return;

        var canvasPoint = ViewportPointToCanvasPoint(new Point(dipX - viewportScreen.X, dipY - viewportScreen.Y));
        EmbedWindowAsIcon(hwnd, title, canvasPoint);
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
    /// Inverso de <see cref="RepositionFrame"/> para la posición: si la propia app se ha movido
    /// (p.ej. arrastrándola por su propia barra de título), el marco WPF la sigue para que ambos
    /// sigan pareciendo un solo bloque. El tamaño nunca se copia: lo decide el marco.
    /// </summary>
    private void SyncFrameFromNativeRect(EmbeddedWindowFrame frame, int screenX, int screenY, int width, int height)
    {
        var ownerWindow = Window.GetWindow(this);
        if (ownerWindow == null || PresentationSource.FromVisual(ViewportBorder) == null) return;

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

        // Si la posición es la que le dimos (±2 px), la app solo ha ajustado su tamaño: nada que seguir.
        // (Recalcular la posición a partir de la suya introduciría redondeos que se acumulan.)
        double frameLeft = Canvas.GetLeft(frame);
        double frameTop = Canvas.GetTop(frame);
        if (!double.IsNaN(frameLeft) && !double.IsNaN(frameTop))
        {
            double expectedX = (viewportScreen.X + (frameLeft + EmbeddedWindowFrame.FrameBorderThickness) * scale + CanvasTranslate.X) * dpiX;
            double expectedY = (viewportScreen.Y + (frameTop + EmbeddedWindowFrame.HeaderHeight + EmbeddedWindowFrame.FrameBorderThickness) * scale + CanvasTranslate.Y) * dpiY;
            if (Math.Abs(screenX - expectedX) <= 2 && Math.Abs(screenY - expectedY) <= 2) return;
        }

        double contentLeft = (dipX - viewportScreen.X - CanvasTranslate.X) / scale;
        double contentTop = (dipY - viewportScreen.Y - CanvasTranslate.Y) / scale;

        // Solo se sigue la POSICIÓN (p.ej. arrastrar VS Code por su propia barra de título). El
        // tamaño lo decide el usuario con los tiradores del marco: algunas apps ajustan solas el
        // tamaño que les damos (una consola lo redondea a su cuadrícula de caracteres) y copiar ese
        // ajuste al marco lo deformaba y desplazaba en cada zoom. Tampoco se vuelve a forzar el
        // tamaño: con esas apps entraría en bucle.
        Canvas.SetLeft(frame, contentLeft - EmbeddedWindowFrame.FrameBorderThickness);
        Canvas.SetTop(frame, contentTop - EmbeddedWindowFrame.HeaderHeight - EmbeddedWindowFrame.FrameBorderThickness);
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
        EmbedWindowAsIcon(entry.Handle, entry.Title, dropCanvasPoint: null);
    }

    /// <summary>
    /// Crea el marco, embebe la ventana nativa y la deja en modo icono — usado tanto por el botón
    /// "+ Pinchar" (sin punto de destino: se coloca cerca del centro de la vista actual) como por el
    /// arrastre de una ventana real del escritorio soltada sobre el canvas (con el punto exacto
    /// donde se soltó, ya convertido a coordenadas de canvas).
    /// </summary>
    private EmbeddedWindowFrame EmbedWindowAsIcon(IntPtr hwnd, string title, Point? dropCanvasPoint)
    {
        var frame = new EmbeddedWindowFrame
        {
            TitleValue = title,
            TargetHwnd = hwnd,
            IconSource = WindowIconService.GetIconFor(hwnd)
        };
        frame.MoveOrResize += (_, _) =>
        {
            ScheduleReposition(frame);
            RelayoutIcons(); // mover/redimensionar una ventana puede tapar o destapar iconos
        };
        frame.Selected += (_, _) => SelectFrame(frame);
        frame.ExpandRequested += (_, _) => ExpandFrame(frame);
        frame.MinimizeRequested += (_, _) => CollapseFrame(frame);
        frame.IconDropped += (_, _) =>
        {
            SetHomeCell(frame, Canvas.GetLeft(frame), Canvas.GetTop(frame));
            RelayoutIcons();
        };
        frame.ConnectorDragStarted += (_, side) => StartConnectionDrag(frame, side);

        InfiniteCanvas.Children.Add(frame);
        EmbedNative(frame);
        Win32.ShowWindow(frame.TargetHwnd, Win32.SW_HIDE); // arranca en modo icono: la ventana real permanece oculta

        double dropX, dropY;
        if (dropCanvasPoint is { } point)
        {
            (dropX, dropY) = (point.X, point.Y);
        }
        else
        {
            // Sin punto de destino: cerca del centro de la zona visible actual del canvas.
            var center = ViewportPointToCanvasPoint(new Point(ViewportBorder.ActualWidth / 2, ViewportBorder.ActualHeight / 2));
            (dropX, dropY) = (center.X, center.Y);
        }
        SetHomeCell(frame, dropX, dropY);
        RelayoutIcons();

        SelectFrame(frame);
        return frame;
    }

    private void ExpandFrame(EmbeddedWindowFrame frame)
    {
        if (frame.CurrentMode == EmbeddedWindowFrame.WindowMode.Expanded) return;

        frame.SetMode(EmbeddedWindowFrame.WindowMode.Expanded);

        if (frame.TargetHwnd != IntPtr.Zero && Win32.IsWindow(frame.TargetHwnd))
            Win32.ShowWindow(frame.TargetHwnd, Win32.SW_SHOW);

        RepositionFrame(frame);
        BringToFront(frame);
        ForceRepaint(frame.TargetHwnd); // paso Icono → Expandido: el que más se repite, y donde más se ha visto el problema
        RelayoutIcons(); // esta ventana recién abierta puede tapar iconos
        _ = GiveKeyboardFocusSoonAsync(frame.TargetHwnd); // se puede escribir en ella sin clic extra
    }

    /// <summary>
    /// Da el teclado a una ventana embebida tras un breve margen: así la app procesa antes su propio
    /// clic (y la oficina termina el suyo), y si ella misma ya colocó el foco en un control concreto,
    /// <see cref="GiveKeyboardFocus"/> lo respeta.
    /// </summary>
    private async Task GiveKeyboardFocusSoonAsync(IntPtr hwnd)
    {
        try
        {
            await Task.Delay(50);
            GiveKeyboardFocus(hwnd);
        }
        catch (Exception ex)
        {
            CrashLog.Write("GiveKeyboardFocus", ex);
        }
    }

    /// <summary>
    /// Una ventana embebida ya no recibe la "activación" que normalmente le da el foco del teclado
    /// (la recibe la oficina). Las apps con controles que toman el foco al hacer clic (el texto del
    /// Bloc de notas) no lo notan; una consola sí: sin esto, lo que se escribía iba a la oficina.
    /// Si el foco ya está en esa ventana o en uno de sus controles, no se toca.
    /// </summary>
    private static void GiveKeyboardFocus(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;

        int targetThread = Win32.GetWindowThreadProcessId(hwnd, out _);
        if (targetThread == 0 || HasKeyboardFocusWithin(hwnd, targetThread)) return;

        // Al ser hija de la oficina, su entrada de teclado ya está enlazada con la nuestra.
        Win32.SetFocus(hwnd);
        if (HasKeyboardFocusWithin(hwnd, targetThread)) return;

        // Si aun así no lo ha tomado, se enlazan un instante las entradas, se da el foco y se desenlaza.
        int ourThread = Win32.GetCurrentThreadId();
        if (!Win32.AttachThreadInput(ourThread, targetThread, true)) return;
        try
        {
            Win32.SetFocus(hwnd);
        }
        finally
        {
            Win32.AttachThreadInput(ourThread, targetThread, false);
        }
    }

    private static bool HasKeyboardFocusWithin(IntPtr hwnd, int threadId)
    {
        var info = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (!Win32.GetGUIThreadInfo(threadId, ref info)) return false;
        return info.hwndFocus == hwnd || (info.hwndFocus != IntPtr.Zero && Win32.IsChild(hwnd, info.hwndFocus));
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
        UpdateAllClipRegions(); // cambia quién tapa a quién
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

        var cell = FindFreeCell(desiredCol, desiredRow, (c, r) => !otherHomes.Contains((c, r))) ?? (desiredCol, desiredRow);
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

            // Radio acotado: si no hay hueco libre cerca, mejor dejarlo en su hogar (aunque quede
            // momentáneamente superpuesto con una ventana) que mandarlo lejísimos de la vista.
            var cell = !consumed.Contains(home) && !CellOverlapsExpandedWindow(home.Col, home.Row)
                ? home
                : FindFreeCell(home.Col, home.Row, (c, r) => !consumed.Contains((c, r)) && !CellOverlapsExpandedWindow(c, r), maxRadius: 20) ?? home;

            consumed.Add(cell);
            Canvas.SetLeft(frame, cell.Col * IconCellSize);
            Canvas.SetTop(frame, cell.Row * IconCellSize);
        }

        RedrawAllConnections();
        UpdateAllClipRegions(); // los iconos también tapan a las ventanas que quedan detrás
    }

    private (int Col, int Row)? FindFreeCell(int col, int row, Func<int, int, bool> isFree, int maxRadius = 200)
    {
        if (isFree(col, row)) return (col, row);

        for (int radius = 1; radius <= maxRadius; radius++)
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

        return null;
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
        if (_connectDragSource != null)
        {
            var canvasPoint = ViewportPointToCanvasPoint(e.GetPosition(ViewportBorder));
            var from = GetConnectorCanvasPoint(_connectDragSource, _connectDragSourceSide);
            // El extremo suelto (bajo el cursor) todavía no pertenece a ningún nodo — se asume el
            // lado contrario al de origen, el caso típico (salida derecha → entra por la izquierda).
            var previewToSide = _connectDragSourceSide == EmbeddedWindowFrame.ConnectorSide.Right
                ? EmbeddedWindowFrame.ConnectorSide.Left
                : EmbeddedWindowFrame.ConnectorSide.Right;
            _connectDragPreview!.Data = BuildConnectionGeometry(from, _connectDragSourceSide, canvasPoint, previewToSide);
            return;
        }

        if (!_panning) return;

        var current = e.GetPosition(ViewportBorder);
        CanvasTranslate.X = _translateStartX + (current.X - _panStart.X);
        CanvasTranslate.Y = _translateStartY + (current.Y - _panStart.Y);
        RepositionAllFrames();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_connectDragSource != null)
        {
            FinishConnectionDrag(e.GetPosition(ViewportBorder));
            return;
        }

        _panning = false;
        ViewportBorder.ReleaseMouseCapture();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(ViewportBorder);
        double oldScale = CanvasScale.ScaleX;
        double factor = e.Delta > 0 ? 1.025 : 1 / 1.025;
        double newScale = Math.Clamp(oldScale * factor, MinZoom, MaxZoom);

        double canvasX = (pos.X - CanvasTranslate.X) / oldScale;
        double canvasY = (pos.Y - CanvasTranslate.Y) / oldScale;

        CanvasScale.ScaleX = newScale;
        CanvasScale.ScaleY = newScale;
        CanvasTranslate.X = pos.X - canvasX * newScale;
        CanvasTranslate.Y = pos.Y - canvasY * newScale;

        ZoomText.Text = $"{Math.Round(newScale * 100)}%";

        // Por debajo de cierto zoom, las ventanas expandidas pasan a icono (los iconos sí escalan
        // bien con el zoom, al ser WPF puro) — evita pelear con la limitación de las ventanas
        // nativas cuando solo se quiere ver el conjunto. No se reabren solas al volver a acercar.
        if (newScale < IconAutoCollapseZoomThreshold)
        {
            foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>()
                         .Where(f => f.CurrentMode == EmbeddedWindowFrame.WindowMode.Expanded).ToList())
            {
                CollapseFrame(frame);
            }
        }

        RepositionAllFrames();

        // El zoom propio de cada app avanza en saltos grandes/discretos (p.ej. Chrome ~10% por
        // Ctrl+Scroll), mucho más que nuestro 2,5% por muesca — reenviarlo en cada muesca hacía que
        // las ventanas expandidas parecieran acelerarse frente a los iconos. Se reenvía solo cuando
        // el zoom del canvas se ha movido lo suficiente desde el último reenvío, para que ambos
        // avancen a un ritmo parecido.
        if (Math.Abs(newScale / _lastForwardedZoomScale - 1) >= ContentZoomForwardStep)
        {
            ForwardZoomToEmbeddedApps(e.Delta, ViewportBorder.PointToScreen(pos));
            _lastForwardedZoomScale = newScale;
        }
    }

    /// <summary>
    /// Reenvía un Ctrl+Scroll sintético a cada ventana Expandida, como si el usuario lo hubiera
    /// pulsado ahí — muchas apps (Chromium/Electron, Explorador...) tienen su propio zoom de
    /// contenido con Ctrl+Scroll; así su contenido también se reescala de verdad al hacer zoom del
    /// canvas, en vez de solo recortarse/reorganizarse al cambiar de tamaño. No tiene efecto en
    /// apps sin ese zoom propio (limitación aceptada: no hay forma de forzarlo desde fuera).
    /// </summary>
    private void ForwardZoomToEmbeddedApps(int wheelDelta, Point cursorScreenDip)
    {
        var ownerWindow = Window.GetWindow(this);
        if (ownerWindow == null) return;

        var source = PresentationSource.FromVisual(ownerWindow);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        int screenX = (int)Math.Round(cursorScreenDip.X * dpiX);
        int screenY = (int)Math.Round(cursorScreenDip.Y * dpiY);
        IntPtr lParam = Win32.MakeLParam(screenX, screenY);
        IntPtr wParam = (IntPtr)((wheelDelta << 16) | Win32.MK_CONTROL);

        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>())
        {
            if (frame.CurrentMode != EmbeddedWindowFrame.WindowMode.Expanded) continue;
            if (frame.TargetHwnd == IntPtr.Zero || !Win32.IsWindow(frame.TargetHwnd)) continue;
            if (IsTerminalWindow(frame.TargetHwnd)) continue;
            Win32.PostMessage(frame.TargetHwnd, Win32.WM_MOUSEWHEEL, wParam, lParam);
        }
    }

    /// <summary>
    /// En una terminal, Ctrl+rueda cambia el tamaño de la letra (no hace zoom de contenido como en
    /// un navegador): no se le reenvía. Consola clásica y Windows Terminal.
    /// </summary>
    private static bool IsTerminalWindow(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        string className = sb.ToString();
        return className == "ConsoleWindowClass" || className == "CASCADIA_HOSTING_WINDOW_CLASS";
    }

    // --- Cables tipo ComfyUI entre nodos ---

    /// <summary>Convierte un punto local del viewport (DIP, como da e.GetPosition) a coordenadas de
    /// canvas — el mismo cálculo que ya se usaba disperso en AddButton_Click/TryDropOnCanvas.</summary>
    private Point ViewportPointToCanvasPoint(Point viewportLocalPoint) => new(
        (viewportLocalPoint.X - CanvasTranslate.X) / CanvasScale.ScaleX,
        (viewportLocalPoint.Y - CanvasTranslate.Y) / CanvasScale.ScaleY);

    /// <summary>
    /// Punto del conector (izquierdo/derecho) de un marco, en coordenadas de CANVAS — algebraico, a
    /// partir de Canvas.Left/Top y Width/Height (igual que RepositionFrame), así que vale tanto en
    /// modo Icono como Expandido sin distinguir casos.
    /// </summary>
    private static Point GetConnectorCanvasPoint(EmbeddedWindowFrame frame, EmbeddedWindowFrame.ConnectorSide side)
    {
        double left = Canvas.GetLeft(frame);
        double top = Canvas.GetTop(frame);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        double x = side == EmbeddedWindowFrame.ConnectorSide.Left ? left : left + frame.Width;
        return new Point(x, top + frame.Height / 2);
    }

    /// <summary>
    /// Curva Bezier horizontal estilo ComfyUI: el punto de control de cada extremo se desplaza
    /// HACIA FUERA del nodo — a la derecha si es un conector derecho, a la izquierda si es
    /// izquierdo — para que el cable siempre salga/entre por el lado correcto en vez de dar un
    /// rodeo que lo hace parecer que cruza por dentro del nodo.
    /// </summary>
    private static PathGeometry BuildConnectionGeometry(
        Point from, EmbeddedWindowFrame.ConnectorSide fromSide, Point to, EmbeddedWindowFrame.ConnectorSide toSide)
    {
        double controlOffset = Math.Max(50, Math.Abs(to.X - from.X) / 2);
        double fromDir = fromSide == EmbeddedWindowFrame.ConnectorSide.Right ? 1 : -1;
        double toDir = toSide == EmbeddedWindowFrame.ConnectorSide.Right ? 1 : -1;

        var figure = new PathFigure { StartPoint = from };
        figure.Segments.Add(new BezierSegment(
            new Point(from.X + controlOffset * fromDir, from.Y),
            new Point(to.X + controlOffset * toDir, to.Y),
            to, isStroked: true));

        return new PathGeometry(new[] { figure });
    }

    private void RedrawAllConnections()
    {
        foreach (var connection in _connections)
        {
            var from = GetConnectorCanvasPoint(connection.From, connection.FromSide);
            var to = GetConnectorCanvasPoint(connection.To, connection.ToSide);
            connection.Visual.Data = BuildConnectionGeometry(from, connection.FromSide, to, connection.ToSide);
        }
    }

    private void RemoveConnectionsFor(EmbeddedWindowFrame frame)
    {
        var toRemove = _connections.Where(c => ReferenceEquals(c.From, frame) || ReferenceEquals(c.To, frame)).ToList();
        foreach (var connection in toRemove)
        {
            InfiniteCanvas.Children.Remove(connection.Visual);
            _connections.Remove(connection);
        }
    }

    private void StartConnectionDrag(EmbeddedWindowFrame frame, EmbeddedWindowFrame.ConnectorSide side)
    {
        _connectDragSource = frame;
        _connectDragSourceSide = side;

        _connectDragPreview = new Path
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 4,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_connectDragPreview, int.MaxValue);
        InfiniteCanvas.Children.Add(_connectDragPreview);

        ViewportBorder.CaptureMouse();
    }

    private void FinishConnectionDrag(Point viewportLocalPoint)
    {
        var source = _connectDragSource;
        _connectDragSource = null;
        ViewportBorder.ReleaseMouseCapture();

        if (_connectDragPreview != null)
        {
            InfiniteCanvas.Children.Remove(_connectDragPreview);
            _connectDragPreview = null;
        }

        if (source == null) return;

        var dropPoint = ViewportPointToCanvasPoint(viewportLocalPoint);
        var target = InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>()
            .FirstOrDefault(f => !ReferenceEquals(f, source) &&
                new Rect(Canvas.GetLeft(f), Canvas.GetTop(f), f.Width, f.Height).Contains(dropPoint));
        if (target == null) return;

        // Sin restricciones: se permiten tantos cables como se quiera entre el mismo par de nodos,
        // desde cualquier conector a cualquier otro. El lado de destino se elige, de los dos
        // conectores del nodo soltado, el más cercano al origen (menos cruces visuales).
        var sourcePoint = GetConnectorCanvasPoint(source, _connectDragSourceSide);
        var targetLeft = GetConnectorCanvasPoint(target, EmbeddedWindowFrame.ConnectorSide.Left);
        var targetRight = GetConnectorCanvasPoint(target, EmbeddedWindowFrame.ConnectorSide.Right);
        var targetSide = (targetLeft - sourcePoint).LengthSquared <= (targetRight - sourcePoint).LengthSquared
            ? EmbeddedWindowFrame.ConnectorSide.Left
            : EmbeddedWindowFrame.ConnectorSide.Right;

        CreateConnection(source, _connectDragSourceSide, target, targetSide);
    }

    /// <summary>
    /// Crea un cable definitivo entre dos nodos — usado tanto por el arrastre manual (ver arriba)
    /// como al restaurar un workspace guardado (ver ImportStateAsync).
    /// </summary>
    private void CreateConnection(
        EmbeddedWindowFrame from, EmbeddedWindowFrame.ConnectorSide fromSide,
        EmbeddedWindowFrame to, EmbeddedWindowFrame.ConnectorSide toSide)
    {
        var path = new Path { Stroke = Brushes.SteelBlue, StrokeThickness = 4 };
        path.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var connection = _connections.FirstOrDefault(c => ReferenceEquals(c.Visual, path));
            if (connection == null) return;
            InfiniteCanvas.Children.Remove(connection.Visual);
            _connections.Remove(connection);
        };
        Panel.SetZIndex(path, -1); // siempre detrás de los marcos

        InfiniteCanvas.Children.Add(path);
        _connections.Add(new NodeConnection { From = from, FromSide = fromSide, To = to, ToSide = toSide, Visual = path });
        RedrawAllConnections();
    }

    // --- Embedding nativo ---

    private void EmbedNative(EmbeddedWindowFrame frame)
    {
        var ownerWindow = Window.GetWindow(this);
        if (ownerWindow == null) return;

        IntPtr hwnd = frame.TargetHwnd;
        IntPtr parentHwnd = new WindowInteropHelper(ownerWindow).Handle;

        // El menú clásico (Archivo, Edición...) hay que leerlo ahora: en cuanto la ventana sea hija,
        // Windows deja de dibujarlo y GetMenu deja de ser fiable. Se replica en la cabecera del marco.
        IntPtr nativeMenu = Win32.GetMenu(hwnd);
        if (nativeMenu != IntPtr.Zero && Win32.IsMenu(nativeMenu))
            frame.AttachNativeMenu(hwnd, nativeMenu);

        int originalStyle = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        _originalStyles[hwnd] = originalStyle;
        EmbeddedWindowRegistry.Register(hwnd, originalStyle);

        int newStyle = originalStyle;
        newStyle &= ~Win32.WS_POPUP;
        newStyle &= ~Win32.WS_CAPTION;
        newStyle &= ~Win32.WS_THICKFRAME;
        newStyle &= ~Win32.WS_SYSMENU;
        newStyle |= Win32.WS_CHILD;
        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, newStyle);

        Win32.SetParent(hwnd, parentHwnd);
        Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        ForceRepaint(hwnd);

        _guard.Attach(hwnd);
    }

    /// <summary>
    /// Fuerza a una ventana a repintarse de verdad. Las apps con render por GPU (Chromium/Electron:
    /// Chrome, Spotify, VS Code...) pueden quedarse en negro tras un SetParent o un simple
    /// ShowWindow — su superficie de dibujo (DirectComposition/swapchain) se queda "huérfana" y un
    /// invalidate normal no basta para que la recreen. Un cambio real de tamaño de ida y vuelta es
    /// lo que de verdad suele forzarlas a recrearla.
    ///
    /// Ese cambio toca SOLO el tamaño (SWP_NOMOVE), nunca la posición: la versión anterior leía la
    /// posición con GetWindowRect (coordenadas de pantalla) y se la pasaba a MoveWindow, que para
    /// una ventana hija espera coordenadas relativas a su padre — desplazaba la ventana cada vez
    /// (al expandir, al maximizar/restaurar la oficina...), el guard lo tomaba por un movimiento de
    /// la app y el marco acababa "yéndose" a otro sitio. Ver informe de problemas.
    /// </summary>
    private static void ForceRepaint(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;

        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        Win32.RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
            Win32.RDW_INVALIDATE | Win32.RDW_ERASE | Win32.RDW_ALLCHILDREN | Win32.RDW_UPDATENOW | Win32.RDW_FRAME);

        if (!Win32.GetWindowRect(hwnd, out var rect)) return;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        const uint sizeOnly = Win32.SWP_NOMOVE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width + 1, height, sizeOnly);
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, width, height, sizeOnly);
    }

    private void ReleaseFrame(EmbeddedWindowFrame frame)
    {
        RestoreNativeWindow(frame.TargetHwnd);
        RemoveFrameFromOffice(frame);
    }

    /// <summary>
    /// Quita un marco de la oficina sin dejar restos: canvas, celda hogar, cables, selección,
    /// vigilante y registro global. No toca la ventana nativa (eso lo decide quien llama).
    /// </summary>
    private void RemoveFrameFromOffice(EmbeddedWindowFrame frame)
    {
        var hwnd = frame.TargetHwnd;
        _guard.Detach(hwnd);
        EmbeddedWindowRegistry.Unregister(hwnd);
        _originalStyles.Remove(hwnd);
        _clipSignatures.Remove(hwnd);

        if (ReferenceEquals(_connectDragSource, frame))
        {
            _connectDragSource = null;
            ViewportBorder.ReleaseMouseCapture();
            if (_connectDragPreview != null)
            {
                InfiniteCanvas.Children.Remove(_connectDragPreview);
                _connectDragPreview = null;
            }
        }

        InfiniteCanvas.Children.Remove(frame);
        _iconHomeCells.Remove(frame);
        _pendingReposition.Remove(frame);
        _hiddenByAppTicks.Remove(frame);
        if (ReferenceEquals(_selectedFrame, frame)) _selectedFrame = null;
        RemoveConnectionsFor(frame);
        RelayoutIcons(); // quitar una ventana expandida puede destapar iconos
    }

    // --- Vigilante: apps que se cierran (o se esconden) por su cuenta ---

    /// <summary>
    /// Cada segundo: si la ventana de una herramienta ya no existe (su propia X, Archivo → Salir,
    /// se colgó...), su marco se quita de la oficina con sus cables. Y si una ventana expandida deja
    /// de ser visible por decisión de la propia app (apps que al "cerrar" se esconden en su bandeja,
    /// como Spotify o Discord), se suelta tal cual — oculta, como ella quería — y se quita el marco.
    /// Esto último solo se comprueba con la pestaña activa y la oficina visible: en esos estados
    /// nosotros nunca ocultamos una ventana expandida, así que si está oculta ha sido la app.
    /// </summary>
    private void CheckEmbeddedWindowsAlive()
    {
        bool interactive = IsHostInteractive();

        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().ToList())
        {
            var hwnd = frame.TargetHwnd;
            if (hwnd == IntPtr.Zero) continue;

            if (!Win32.IsWindow(hwnd))
            {
                RemoveFrameFromOffice(frame);
                continue;
            }

            if (interactive && frame.CurrentMode == EmbeddedWindowFrame.WindowMode.Expanded && !Win32.IsWindowVisible(hwnd))
            {
                // Dos comprobaciones seguidas, para no confundir un parpadeo momentáneo con un cierre.
                int ticks = _hiddenByAppTicks.GetValueOrDefault(frame) + 1;
                _hiddenByAppTicks[frame] = ticks;
                if (ticks < 2) continue;

                if (_originalStyles.TryGetValue(hwnd, out int originalStyle))
                    EmbeddedWindowRegistry.ReleaseHidden(hwnd, originalStyle);
                RemoveFrameFromOffice(frame);
            }
            else
            {
                _hiddenByAppTicks.Remove(frame);
            }
        }
    }

    public int ToolCount => InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().Count();

    /// <summary>
    /// "Salir de verdad" o cerrar esta pestaña: cada herramienta se cierra como si se pulsara su
    /// propia X (con su aviso de guardar cambios si lo necesita). Devuelve sus ventanas para que
    /// quien llama pueda mostrar las que no se cierren (ver EmbeddedWindowRegistry.RevealSurvivorsAsync).
    /// </summary>
    public List<IntPtr> CloseAllEmbeddedWindows()
    {
        _watchdog.Stop();
        var closing = new List<IntPtr>();

        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().ToList())
        {
            var hwnd = frame.TargetHwnd;
            if (!_originalStyles.TryGetValue(hwnd, out int originalStyle)) continue;

            _guard.Detach(hwnd);
            EmbeddedWindowRegistry.BeginClose(hwnd, originalStyle);
            _originalStyles.Remove(hwnd);
            closing.Add(hwnd);
        }

        return closing;
    }

    private void RestoreNativeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;
        if (!_originalStyles.TryGetValue(hwnd, out int originalStyle)) return;

        _guard.Detach(hwnd);
        EmbeddedWindowRegistry.Unregister(hwnd);

        Win32.SetWindowRgn(hwnd, IntPtr.Zero, false); // sin el recorte de la oficina: si no, saldría cortada
        _clipSignatures.Remove(hwnd);
        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, originalStyle);
        Win32.SetParent(hwnd, IntPtr.Zero);
        Win32.MoveWindow(hwnd, 100, 100, 800, 600, true);
        Win32.ShowWindow(hwnd, Win32.SW_SHOW);
        ForceRepaint(hwnd);

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
            UpdateAllClipRegions();
        }));
    }

    /// <summary>
    /// Calcula la posición/tamaño en pantalla de la zona de contenido del marco de forma algebraica
    /// (Canvas.Left/Top, Width/Height, escala y traslación del canvas) en vez de leer ActualWidth/
    /// PointToScreen del árbol visual, que dependen de que WPF ya haya terminado su pasada de layout.
    /// Eso evita que MoveWindow reciba un tamaño "de un fotograma atrás" durante el arrastre.
    /// Tanto la posición como el tamaño siguen el zoom/pan del canvas.
    /// </summary>
    private void RepositionFrame(EmbeddedWindowFrame frame)
    {
        if (frame.TargetHwnd == IntPtr.Zero) return;

        var ownerWindow = Window.GetWindow(this);
        if (ownerWindow == null) return;
        // Justo al cambiar de pestaña, la nueva todavía no está en pantalla: PointToScreen lanzaría
        // (visto en crash.log). OnTabActivated vuelve a recolocar cuando ya lo está.
        if (PresentationSource.FromVisual(ViewportBorder) == null) return;

        double left = Canvas.GetLeft(frame);
        double top = Canvas.GetTop(frame);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        double scale = CanvasScale.ScaleX;
        var viewportScreen = ViewportBorder.PointToScreen(new Point(0, 0));

        double contentLeft = left + EmbeddedWindowFrame.FrameBorderThickness;
        double contentTop = top + EmbeddedWindowFrame.HeaderHeight + EmbeddedWindowFrame.FrameBorderThickness;

        // No se empuja el contenido para evitar que tape la barra de herramientas: se probó (ver
        // informe de problemas #13) con varias variantes de clamp y todas resultaban peor que el
        // problema — o se hundía progresivamente, o quedaba "pegado" al borde arrastrando la
        // cabecera de forma molesta mientras se hacía zoom con el cursor sobre la ventana. Se acepta
        // que, en zoom extremo con la ventana muy arriba, el contenido nativo pueda superponerse a
        // la barra (misma limitación estructural ya aceptada en el informe #8).

        double contentWidth = frame.Width - 2 * EmbeddedWindowFrame.FrameBorderThickness;
        double contentHeight = frame.Height - EmbeddedWindowFrame.HeaderHeight - 2 * EmbeddedWindowFrame.FrameBorderThickness;
        if (contentWidth <= 0 || contentHeight <= 0) return;

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
        UpdateAllClipRegions();
    }

    // --- Recorte: que el contenido nativo no tape la barra ni las cabeceras de otros marcos ---

    // Última región aplicada a cada ventana ("" = sin región). Evita llamar a SetWindowRgn (que
    // repinta) en cada movimiento del ratón al hacer pan si el recorte no ha cambiado.
    private readonly Dictionary<IntPtr, string> _clipSignatures = new();

    /// <summary>
    /// Una ventana nativa embebida se pinta siempre por encima de lo WPF: sin esto, al hacer zoom o
    /// subirla, su contenido tapaba la barra de la oficina, y la de atrás tapaba la cabecera de la de
    /// delante. No se mueve ni se redimensiona nada (los intentos de "empujarla" salieron mal, ver
    /// informe #13): solo se le dice a Windows qué parte se ve y recibe clics (SetWindowRgn).
    /// Parte visible = su contenido ∩ el viewport − los marcos que están delante. Se probó antes en
    /// experiments/ClipTest con Bloc de notas, Chrome y VS Code.
    /// </summary>
    private void UpdateAllClipRegions()
    {
        if (PresentationSource.FromVisual(ViewportBorder) == null) return;

        var frames = InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().ToList();
        foreach (var frame in frames)
        {
            if (frame.CurrentMode != EmbeddedWindowFrame.WindowMode.Expanded) continue;
            if (frame.TargetHwnd == IntPtr.Zero || !Win32.IsWindow(frame.TargetHwnd)) continue;
            UpdateClipRegion(frame, frames);
        }
    }

    private void UpdateClipRegion(EmbeddedWindowFrame frame, List<EmbeddedWindowFrame> allFrames)
    {
        var ownerWindow = Window.GetWindow(this);
        if (ownerWindow == null) return;
        var source = PresentationSource.FromVisual(ownerWindow);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        double scale = CanvasScale.ScaleX;
        if (scale <= 0 || dpiX <= 0 || dpiY <= 0) return;

        // Todo en coordenadas de CANVAS y luego relativo al contenido, con la misma conversión que
        // usa RepositionFrame para el tamaño: así el recorte encaja con la ventana al píxel.
        double left = Canvas.GetLeft(frame), top = Canvas.GetTop(frame);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        var content = new Rect(
            left + EmbeddedWindowFrame.FrameBorderThickness,
            top + EmbeddedWindowFrame.HeaderHeight + EmbeddedWindowFrame.FrameBorderThickness,
            frame.Width - 2 * EmbeddedWindowFrame.FrameBorderThickness,
            frame.Height - EmbeddedWindowFrame.HeaderHeight - 2 * EmbeddedWindowFrame.FrameBorderThickness);
        if (content.Width <= 0 || content.Height <= 0) return;

        int width = (int)Math.Round(content.Width * scale * dpiX);
        int height = (int)Math.Round(content.Height * scale * dpiY);

        // Rectángulo de canvas → píxeles relativos a la esquina de la propia ventana.
        (int L, int T, int R, int B) ToLocal(Rect r) => (
            (int)Math.Floor((r.Left - content.Left) * scale * dpiX),
            (int)Math.Floor((r.Top - content.Top) * scale * dpiY),
            (int)Math.Ceiling((r.Right - content.Left) * scale * dpiX),
            (int)Math.Ceiling((r.Bottom - content.Top) * scale * dpiY));

        var viewTopLeft = ViewportPointToCanvasPoint(new Point(0, 0));
        var viewBottomRight = ViewportPointToCanvasPoint(new Point(ViewportBorder.ActualWidth, ViewportBorder.ActualHeight));
        var view = ToLocal(new Rect(viewTopLeft, viewBottomRight));

        var covering = new List<(int L, int T, int R, int B)>();
        foreach (var other in allFrames)
        {
            if (ReferenceEquals(other, frame) || !IsInFrontOf(other, frame)) continue;
            double oLeft = Canvas.GetLeft(other), oTop = Canvas.GetTop(other);
            if (double.IsNaN(oLeft) || double.IsNaN(oTop)) continue;
            var otherRect = new Rect(oLeft, oTop, other.Width, other.Height);
            if (!otherRect.IntersectsWith(content)) continue;
            covering.Add(ToLocal(otherRect));
        }

        bool fullyVisible = view.L <= 0 && view.T <= 0 && view.R >= width && view.B >= height && covering.Count == 0;
        string signature = fullyVisible ? "" : $"{width}x{height}|{view}|{string.Join(";", covering)}";

        if (_clipSignatures.TryGetValue(frame.TargetHwnd, out var previous) && previous == signature) return;
        if (!_clipSignatures.ContainsKey(frame.TargetHwnd) && fullyVisible)
        {
            _clipSignatures[frame.TargetHwnd] = ""; // nunca tuvo región: nada que quitar
            return;
        }

        if (fullyVisible)
        {
            Win32.SetWindowRgn(frame.TargetHwnd, IntPtr.Zero, true);
        }
        else
        {
            IntPtr region = Win32.CreateRectRgn(0, 0, width, height);
            IntPtr viewRegion = Win32.CreateRectRgn(view.L, view.T, view.R, view.B);
            Win32.CombineRgn(region, region, viewRegion, Win32.RGN_AND);
            Win32.DeleteObject(viewRegion);
            foreach (var c in covering)
            {
                IntPtr coverRegion = Win32.CreateRectRgn(c.L, c.T, c.R, c.B);
                Win32.CombineRgn(region, region, coverRegion, Win32.RGN_DIFF);
                Win32.DeleteObject(coverRegion);
            }
            if (Win32.SetWindowRgn(frame.TargetHwnd, region, true) == 0)
                Win32.DeleteObject(region);
        }
        _clipSignatures[frame.TargetHwnd] = signature;
    }

    /// <summary>Mismo criterio que WPF para pintar: ZIndex mayor, o igual y añadido después.</summary>
    private bool IsInFrontOf(EmbeddedWindowFrame a, EmbeddedWindowFrame b)
    {
        int za = Panel.GetZIndex(a), zb = Panel.GetZIndex(b);
        if (za != zb) return za > zb;
        return InfiniteCanvas.Children.IndexOf(a) > InfiniteCanvas.Children.IndexOf(b);
    }

    /// <summary>
    /// Algunas apps reponen su propia región (p.ej. al cambiar de tamaño) y borran la nuestra. El
    /// vigilante lo detecta y se vuelve a aplicar.
    /// </summary>
    private void ReapplyLostClipRegions()
    {
        bool lost = false;
        foreach (var (hwnd, signature) in _clipSignatures.ToList())
        {
            if (signature == "" || !Win32.IsWindow(hwnd)) continue;
            IntPtr probe = Win32.CreateRectRgn(0, 0, 0, 0);
            int type = Win32.GetWindowRgn(hwnd, probe);
            Win32.DeleteObject(probe);
            if (type == 0)
            {
                _clipSignatures.Remove(hwnd);
                lost = true;
            }
        }
        if (lost) UpdateAllClipRegions();
    }

    // --- Orquestación desde MainWindow (cambio de pestaña, mover/redimensionar la ventana host) ---

    public void OnTabActivated()
    {
        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>())
        {
            if (frame.CurrentMode == EmbeddedWindowFrame.WindowMode.Expanded &&
                frame.TargetHwnd != IntPtr.Zero && Win32.IsWindow(frame.TargetHwnd))
            {
                Win32.ShowWindow(frame.TargetHwnd, Win32.SW_SHOW);
                ForceRepaint(frame.TargetHwnd);
            }
        }
        RepositionAllFrames();
        // Al cambiar de pestaña, esta vista aún no está en pantalla en este momento: se vuelve a
        // recolocar en cuanto WPF la haya cargado.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RepositionAllFrames));
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

    /// <summary>
    /// Solo para "Salir de verdad" y el apagado del PC (cerrar con la X solo oculta la oficina y
    /// las herramientas siguen dentro). Una ventana embebida no puede sobrevivir dentro de una
    /// oficina que deja de existir — si no se suelta antes, Windows la destruye junto con la
    /// nuestra. Se sueltan minimizadas en la barra de tareas: nunca se cierran (nada de WM_CLOSE,
    /// que en apps de proceso compartido como VS Code llegó a cerrar también otras ventanas suyas),
    /// no piden guardar nada y no aparecen en medio del escritorio.
    /// </summary>
    public void ReleaseAllEmbeddedWindowsMinimized()
    {
        _watchdog.Stop();
        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>().ToList())
        {
            var hwnd = frame.TargetHwnd;
            if (!_originalStyles.TryGetValue(hwnd, out int originalStyle)) continue;

            _guard.Detach(hwnd);
            EmbeddedWindowRegistry.ReleaseMinimized(hwnd, originalStyle);
            _originalStyles.Remove(hwnd);
        }
    }

    public void ShutdownHooks()
    {
        _watchdog.Stop();
        _mouseHook.Dispose();
        _guard.Dispose();
    }

    // --- Guardar / restaurar workspace ---

    public WorkspaceTab ExportState()
    {
        var nodes = new List<WorkspaceNode>();

        foreach (var frame in InfiniteCanvas.Children.OfType<EmbeddedWindowFrame>())
        {
            if (frame.TargetHwnd == IntPtr.Zero) continue;

            string? exePath = OpenWindowsService.GetExecutablePath(frame.TargetHwnd);
            if (exePath == null) continue; // sin ruta del ejecutable no hay forma de relanzarla

            var home = _iconHomeCells.TryGetValue(frame, out var cell) ? cell : (Col: 0, Row: 0);
            var expanded = frame.GetExpandedMemory();
            frame.LaunchArgs = LaunchInfoService.CaptureLaunchArgs(frame.TargetHwnd, exePath, frame.LaunchArgs);
            frame.LaunchShortcut = LaunchInfoService.CaptureLaunchShortcut(frame.TargetHwnd, exePath, frame.LaunchShortcut);
            nodes.Add(new WorkspaceNode
            {
                Id = frame.NodeId.ToString(),
                ExecutablePath = exePath,
                LaunchArgs = frame.LaunchArgs,
                LaunchShortcut = frame.LaunchShortcut,
                Title = frame.TitleValue,
                HomeCol = home.Col,
                HomeRow = home.Row,
                ExpandedLeft = expanded?.Left,
                ExpandedTop = expanded?.Top,
                ExpandedWidth = expanded?.Width,
                ExpandedHeight = expanded?.Height
            });
        }

        var connections = _connections.Select(c => new WorkspaceConnection
        {
            FromNodeId = c.From.NodeId.ToString(),
            FromSide = c.FromSide.ToString(),
            ToNodeId = c.To.NodeId.ToString(),
            ToSide = c.ToSide.ToString()
        }).ToList();

        return new WorkspaceTab
        {
            Nodes = nodes,
            Connections = connections,
            ViewX = CanvasTranslate.X,
            ViewY = CanvasTranslate.Y,
            ViewScale = CanvasScale.ScaleX
        };
    }

    /// <summary>Restaura el pan/zoom guardado; a partir de aquí la vista ya no se recentra sola.</summary>
    private void ApplyViewState(double x, double y, double scale)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(scale) || double.IsInfinity(x) || double.IsInfinity(y)) return;

        _canvasCentered = true;
        scale = Math.Clamp(scale, MinZoom, MaxZoom);
        CanvasScale.ScaleX = scale;
        CanvasScale.ScaleY = scale;
        CanvasTranslate.X = x;
        CanvasTranslate.Y = y;
        ZoomText.Text = $"{Math.Round(scale * 100)}%";
        _lastForwardedZoomScale = scale;
    }

    /// <summary>
    /// Restaura una pestaña guardada: por cada nodo lanza una instancia NUEVA de su ejecutable
    /// (nunca se toca nada que el usuario ya tuviera abierto en su escritorio), espera a que
    /// aparezca su ventana, la embebe como icono en su posición guardada, y al final reconstruye
    /// los cables. Los nodos se procesan uno detrás de otro, no en paralelo, para no confundir
    /// varias ventanas nuevas del mismo ejecutable entre sí (p.ej. varias terminales a la vez).
    /// Devuelve los títulos/rutas de los nodos que no se pudieron restaurar, para que MainWindow lo
    /// muestre — nunca lanza si algo falla, sigue con el resto.
    /// </summary>
    public async Task<List<string>> ImportStateAsync(WorkspaceTab tab)
    {
        var failed = new List<string>();
        var restored = new Dictionary<string, EmbeddedWindowFrame>();

        if (tab.ViewX is { } viewX && tab.ViewY is { } viewY && tab.ViewScale is { } viewScale)
            ApplyViewState(viewX, viewY, viewScale);

        foreach (var node in tab.Nodes)
        {
            var frame = await TryRestoreNodeAsync(node);
            if (frame == null)
            {
                failed.Add(string.IsNullOrWhiteSpace(node.Title) ? node.ExecutablePath : node.Title);
                continue;
            }

            if (node.ExpandedLeft is { } left && node.ExpandedTop is { } top &&
                node.ExpandedWidth is { } width && node.ExpandedHeight is { } height)
            {
                frame.SetExpandedMemory(left, top, width, height);
            }

            restored[node.Id] = frame;
        }

        foreach (var connection in tab.Connections)
        {
            if (!restored.TryGetValue(connection.FromNodeId, out var from)) continue;
            if (!restored.TryGetValue(connection.ToNodeId, out var to)) continue;
            if (!Enum.TryParse<EmbeddedWindowFrame.ConnectorSide>(connection.FromSide, out var fromSide)) continue;
            if (!Enum.TryParse<EmbeddedWindowFrame.ConnectorSide>(connection.ToSide, out var toSide)) continue;
            CreateConnection(from, fromSide, to, toSide);
        }

        return failed;
    }

    private async Task<EmbeddedWindowFrame?> TryRestoreNodeAsync(WorkspaceNode node)
    {
        if (!System.IO.File.Exists(node.ExecutablePath)) return null;

        // Foto de las ventanas que YA existían antes de lanzar este nodo. Es la única protección
        // real contra secuestrar una ventana ajena (p.ej. una terminal que ya tuvieras abierta, que
        // comparte el mismo ejecutable que el nodo a restaurar) — sin esto, si la app no crea una
        // ventana nueva claramente distinguible a tiempo, el plan B por ruta de ejecutable podía
        // "encontrar" y agarrar cualquier ventana ya abierta que coincidiera. Ver informe de
        // problemas — esto causó que se cerrara una ventana de trabajo ajena a la app.
        var preExisting = OpenWindowsService.ListTopLevelWindows().Select(w => w.Handle).ToHashSet();

        // Sin documento que reabrir, se lanza como la lanza el usuario (su acceso directo de la barra
        // de tareas / menú Inicio): así una terminal sale con sus colores y fuente, y cada app con su
        // carpeta de inicio. Con documento, el ejecutable con ese documento.
        string? shortcut = null;
        if (string.IsNullOrWhiteSpace(node.LaunchArgs) && node.LaunchShortcut != LaunchInfoService.DirectLaunch)
        {
            shortcut = !string.IsNullOrWhiteSpace(node.LaunchShortcut) && System.IO.File.Exists(node.LaunchShortcut)
                ? node.LaunchShortcut
                : ShortcutFinder.FindShortcutFor(node.ExecutablePath);
        }
        var startInfo = shortcut != null
            ? new ProcessStartInfo(shortcut) { UseShellExecute = true }
            : new ProcessStartInfo(node.ExecutablePath, node.LaunchArgs) { UseShellExecute = true };

        int pid = 0;
        try
        {
            using var process = Process.Start(startInfo);
            // Al abrir un acceso directo Windows no siempre devuelve el proceso: no pasa nada, la
            // ventana nueva se busca también por ruta del ejecutable.
            if (process != null) pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            // Proceso sin identificador accesible: se sigue buscando por ruta del ejecutable.
        }
        catch
        {
            return null;
        }

        var hwnd = await WaitForWindowAsync(pid, node.ExecutablePath, preExisting, TimeSpan.FromSeconds(20));
        if (hwnd == IntPtr.Zero) return null;

        string title = OpenWindowsService.TryGetEmbeddableTitle(hwnd, out var liveTitle) ? liveTitle : node.Title;
        var frame = EmbedWindowAsIcon(hwnd, title, new Point(node.HomeCol * IconCellSize, node.HomeRow * IconCellSize));
        frame.LaunchArgs = node.LaunchArgs;
        frame.LaunchShortcut = node.LaunchShortcut == LaunchInfoService.DirectLaunch
            ? LaunchInfoService.DirectLaunch
            : shortcut ?? node.LaunchShortcut ?? "";
        return frame;
    }

    /// <summary>
    /// Sondea hasta encontrar la ventana embebible del proceso recién lanzado — genérico, sin casos
    /// especiales por app: primero por PID exacto; si esa app usa un proceso "lanzador" intermedio
    /// que ya no existe, prueba por ruta del ejecutable entre las ventanas que van apareciendo. En
    /// ambos casos se excluye explícitamente cualquier ventana que ya existiera antes de lanzar este
    /// nodo (<paramref name="preExisting"/>) — si no aparece ninguna genuinamente nueva a tiempo, se
    /// trata como fallo en vez de arriesgarse a coger la que ya hubiera.
    /// </summary>
    private static async Task<IntPtr> WaitForWindowAsync(int pid, string executablePath, HashSet<IntPtr> preExisting, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = FindEmbeddableWindow(h =>
            {
                if (preExisting.Contains(h)) return false;
                Win32.GetWindowThreadProcessId(h, out int p);
                return p == pid;
            });
            if (hwnd == IntPtr.Zero)
            {
                hwnd = FindEmbeddableWindow(h =>
                    !preExisting.Contains(h) &&
                    string.Equals(OpenWindowsService.GetExecutablePath(h), executablePath, StringComparison.OrdinalIgnoreCase));
            }
            if (hwnd != IntPtr.Zero) return hwnd;

            await Task.Delay(200);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindEmbeddableWindow(Func<IntPtr, bool> matches)
    {
        foreach (var entry in OpenWindowsService.ListTopLevelWindows())
        {
            if (matches(entry.Handle)) return entry.Handle;
        }
        return IntPtr.Zero;
    }
}
