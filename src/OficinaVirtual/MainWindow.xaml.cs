using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OficinaVirtual.Services;
using OficinaVirtual.Views;

namespace OficinaVirtual;

public partial class MainWindow : Window
{
    private const string NewTabHeader = "+";

    private TabItem? _previouslySelectedTab;
    private readonly WorkspaceFile? _savedWorkspace;
    private bool _exitRequested;
    private WindowState _lastVisibleState = WindowState.Normal;

    /// <summary>La X ha ocultado la oficina en la bandeja (App muestra un aviso la primera vez).</summary>
    public event EventHandler? HiddenToTray;

    public MainWindow()
    {
        InitializeComponent();

        // Se crea siempre una pestaña vacía por defecto de forma síncrona (como antes) — el
        // TabControl necesita ya tener una selección válida en su primer paso de layout, si no
        // WPF dispara SelectionChanged de forma reentrante mientras el generador de contenedores
        // aún está inicializando y lanza una excepción. Si hay workspace guardado, esta pestaña se
        // sustituye después, una vez cargada la ventana.
        MainTabControl.Items.Add(BuildNewTabButton());
        AddCanvasTab("Pestaña 1");

        _savedWorkspace = WorkspaceService.Load();
        ApplyWindowPlacement(_savedWorkspace?.Window);

        Loaded += async (_, _) => await RestoreWorkspaceAsync();
    }

    // --- Ocultar / mostrar / salir ---

    /// <summary>
    /// La X (o Alt+F4) no cierra la oficina: la oculta en la bandeja del sistema. Las herramientas
    /// embebidas siguen dentro y vivas, con todo su estado — una ventana embebida no puede
    /// sobrevivir a la destrucción de la nuestra, así que "cerrar" de verdad nunca puede dejar las
    /// cosas "dentro". Solo "Salir" (bandeja) o el apagado del PC cierran de verdad.
    /// </summary>
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_exitRequested) return;

        e.Cancel = true;
        SaveWorkspaceSafe();
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }

    public void ShowOffice()
    {
        if (_exitRequested) return;

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _lastVisibleState;
        Activate();
        CurrentCanvasView?.OnTabActivated();
    }

    /// <summary>
    /// "Salir de verdad" (menú de la bandeja): se guarda el workspace (con las herramientas aún
    /// vivas, para poder relanzarlas al volver) y cada herramienta se cierra como si se pulsara su
    /// propia X. Las que sigan abiertas pasado un momento (preguntando si guardar, o si el usuario
    /// cancela) se muestran normalmente — nunca quedan vivas pero invisibles.
    /// </summary>
    public async Task ExitAndCloseToolsAsync()
    {
        _exitRequested = true; // a partir de aquí la X ya no oculta
        SaveWorkspaceSafe();
        Hide();

        var closing = new List<IntPtr>();
        foreach (var view in AllCanvasViews())
        {
            try
            {
                closing.AddRange(view.CloseAllEmbeddedWindows());
                view.ShutdownHooks();
            }
            catch (Exception ex)
            {
                CrashLog.Write("ExitAndCloseTools", ex);
            }
        }

        // Red de seguridad: cualquier ventana que por lo que sea siga registrada, se suelta.
        EmbeddedWindowRegistry.EmergencyReleaseAll();
        await EmbeddedWindowRegistry.RevealSurvivorsAsync(closing, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Apagado/reinicio del PC: se guarda todo y cada herramienta vuelve a ser una ventana normal
    /// (minimizada); Windows se encarga de cerrarlas como a cualquier otra app, con sus avisos.
    /// </summary>
    public void PrepareForSessionEnd()
    {
        _exitRequested = true;
        SaveWorkspaceSafe();

        foreach (var view in AllCanvasViews())
        {
            try
            {
                view.ReleaseAllEmbeddedWindowsMinimized();
                view.ShutdownHooks();
            }
            catch (Exception ex)
            {
                CrashLog.Write("PrepareForSessionEnd", ex);
            }
        }

        EmbeddedWindowRegistry.EmergencyReleaseAll();
    }

    public void CloseForExit()
    {
        _exitRequested = true;
        Close();
    }

    public void ShowStatus(string message) => WorkspaceStatusText.Text = message;

    // --- Workspace ---

    private async Task RestoreWorkspaceAsync()
    {
        var workspace = _savedWorkspace;
        if (workspace == null || workspace.Tabs.Count == 0) return; // se queda la pestaña vacía por defecto

        // Loaded puede dispararse mientras el TabControl todavía está terminando su primera pasada
        // de generación de contenedores — tocar Items justo ahora puede reentrar en el generador y
        // hacer que WPF lance "StartAt mientras la generación está en curso" (crash visto en el
        // Visor de sucesos). Cedemos un ciclo al dispatcher antes de tocar nada.
        await Dispatcher.Yield(DispatcherPriority.Background);

        var placeholder = CanvasTabs().FirstOrDefault();

        // Cada pestaña se restaura estando seleccionada y ya cargada (para embeber una ventana la
        // vista tiene que estar dentro de la ventana de la oficina).
        var failed = new List<string>();
        foreach (var tab in workspace.Tabs)
        {
            AddCanvasTab(string.IsNullOrWhiteSpace(tab.Name) ? $"Pestaña {CanvasTabs().Count() + 1}" : tab.Name);
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (CurrentCanvasView is { } view)
                failed.AddRange(await view.ImportStateAsync(tab));
        }

        // La pestaña de arranque se quita al FINAL, cuando ya no está seleccionada: si se quitara
        // estando seleccionada, WPF seleccionaría la siguiente — la "+" — y eso crea una pestaña
        // nueva vacía (se acumulaba una en cada arranque).
        if (placeholder != null && !ReferenceEquals(MainTabControl.SelectedItem, placeholder))
        {
            await Dispatcher.Yield(DispatcherPriority.Background); // antes de volver a tocar Items
            // Antes de descartarla hay que pararle sus hooks de Windows (ya activos desde que su
            // CanvasTabView se construyó).
            if (placeholder.Content is CanvasTabView placeholderView)
                placeholderView.ShutdownHooks();
            MainTabControl.Items.Remove(placeholder);
        }

        if (failed.Count > 0)
            ShowStatus("No se pudo reabrir: " + string.Join(", ", failed));
    }

    private void SaveWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SaveWorkspaceSafe())
            ShowStatus("Workspace guardado.");
    }

    private bool SaveWorkspaceSafe()
    {
        try
        {
            WorkspaceService.Save(ExportWorkspace());
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("SaveWorkspace", ex);
            ShowStatus("No se pudo guardar el workspace (detalles en crash.log).");
            return false;
        }
    }

    private WorkspaceFile ExportWorkspace()
    {
        var workspace = new WorkspaceFile { Window = CaptureWindowPlacement() };
        foreach (var tabItem in CanvasTabs())
        {
            var tab = ((CanvasTabView)tabItem.Content).ExportState();
            tab.Name = (tabItem.Header as CanvasTabHeader)?.TabName ?? "";
            workspace.Tabs.Add(tab);
        }
        return workspace;
    }

    private WorkspaceWindowPlacement? CaptureWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Width) || double.IsNaN(bounds.Left)) return null;

        return new WorkspaceWindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized ||
                        (WindowState == WindowState.Minimized && _lastVisibleState == WindowState.Maximized)
        };
    }

    /// <summary>
    /// Recoloca la ventana de la oficina donde estaba. Si esa posición ya no cae en ninguna pantalla
    /// (p.ej. se desconectó un monitor), se ignora y se usa la posición por defecto.
    /// </summary>
    private void ApplyWindowPlacement(WorkspaceWindowPlacement? placement)
    {
        if (placement == null || placement.Width < 400 || placement.Height < 300) return;

        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var titleBarPoint = new Point(placement.Left + 60, placement.Top + 10);
        if (!virtualScreen.Contains(titleBarPoint)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
            _lastVisibleState = WindowState.Maximized;
        }
    }

    // --- Pestañas ---

    private TabItem BuildNewTabButton() => new()
    {
        Header = NewTabHeader,
        FontWeight = FontWeights.Bold
    };

    private void AddCanvasTab(string name)
    {
        var header = new CanvasTabHeader(name);
        var tab = new TabItem
        {
            Header = header,
            Content = new CanvasTabView()
        };
        header.CloseRequested += async (_, _) => await CloseTabAsync(tab);

        MainTabControl.Items.Insert(MainTabControl.Items.Count - 1, tab);
        MainTabControl.SelectedItem = tab;
    }

    /// <summary>
    /// Cerrar una pestaña = cerrar ese "escritorio": sus herramientas se cierran como si se pulsara
    /// su propia X (tras confirmarlo si tiene alguna). Siempre queda al menos una pestaña.
    /// </summary>
    private async Task CloseTabAsync(TabItem tab)
    {
        if (tab.Content is not CanvasTabView view || !MainTabControl.Items.Contains(tab)) return;

        string name = (tab.Header as CanvasTabHeader)?.TabName ?? "esta pestaña";
        int tools = view.ToolCount;
        if (tools > 0)
        {
            var answer = MessageBox.Show(this,
                $"¿Cerrar «{name}» y sus {tools} herramienta(s)?\n\nCada una se cerrará como si pulsaras su propia X " +
                "(si tiene cambios sin guardar, te lo preguntará).",
                "Cerrar pestaña", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        // Antes de quitarla, se selecciona otra pestaña normal — nunca la "+", que crearía una
        // pestaña nueva sola. Si era la última, primero se crea una vacía.
        var others = CanvasTabs().Where(t => !ReferenceEquals(t, tab)).ToList();
        if (others.Count == 0)
        {
            AddCanvasTab("Pestaña 1");
        }
        else if (ReferenceEquals(MainTabControl.SelectedItem, tab))
        {
            int index = MainTabControl.Items.IndexOf(tab);
            MainTabControl.SelectedItem = others.LastOrDefault(t => MainTabControl.Items.IndexOf(t) < index) ?? others.First();
        }

        // Tocar las pestañas en el mismo ciclo que otro cambio de pestañas ya provocó crashes de
        // WPF (reentrada del generador): se cede un ciclo al dispatcher antes de quitarla.
        await Dispatcher.Yield(DispatcherPriority.Background);

        var closing = view.CloseAllEmbeddedWindows();
        view.ShutdownHooks();
        MainTabControl.Items.Remove(tab);
        if (ReferenceEquals(_previouslySelectedTab, tab)) _previouslySelectedTab = null;

        SaveWorkspaceSafe();
        await EmbeddedWindowRegistry.RevealSurvivorsAsync(closing, TimeSpan.FromSeconds(3));
    }

    private IEnumerable<TabItem> CanvasTabs() =>
        MainTabControl.Items.OfType<TabItem>().Where(t => t.Content is CanvasTabView);

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabControl.SelectedItem is TabItem { Header: NewTabHeader })
        {
            AddCanvasTab($"Pestaña {MainTabControl.Items.Count}");
            return;
        }

        if (_previouslySelectedTab?.Content is CanvasTabView previousView)
            previousView.OnTabDeactivated();

        if (MainTabControl.SelectedItem is TabItem selected)
        {
            _previouslySelectedTab = selected;
            if (selected.Content is CanvasTabView selectedView)
                selectedView.OnTabActivated();
        }
    }

    private CanvasTabView? CurrentCanvasView =>
        (MainTabControl.SelectedItem as TabItem)?.Content as CanvasTabView;

    // --- Cambios de la ventana host ---

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) =>
        CurrentCanvasView?.NotifyHostMovedOrResized();

    private void Window_LocationChanged(object sender, EventArgs e) =>
        CurrentCanvasView?.NotifyHostMovedOrResized();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
            _lastVisibleState = WindowState;

        foreach (var view in AllCanvasViews())
        {
            if (WindowState == WindowState.Minimized)
                view.OnTabDeactivated();
        }

        if (WindowState != WindowState.Minimized)
            CurrentCanvasView?.OnTabActivated();
    }

    private IEnumerable<CanvasTabView> AllCanvasViews() =>
        MainTabControl.Items
            .OfType<TabItem>()
            .Select(t => t.Content as CanvasTabView)
            .Where(v => v != null)!;
}
