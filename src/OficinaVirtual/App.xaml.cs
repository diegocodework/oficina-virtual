using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OficinaVirtual.Services;

namespace OficinaVirtual;

/// <summary>
/// Arranque de la app: instancia única, icono en la bandeja del sistema, red de seguridad ante
/// errores y apagado del PC.
/// </summary>
public partial class App : Application
{
    // "Local\" = por sesión de usuario de Windows (cada usuario tiene su propia oficina).
    private const string SingleInstanceMutexName = @"Local\OficinaVirtual.SingleInstance";
    private const string ActivateEventName = @"Local\OficinaVirtual.Activate";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _exiting;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Ya hay una oficina abierta (quizá oculta en la bandeja): se le pide que se muestre y
            // esta segunda copia se cierra sin crear nada.
            SignalRunningInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnFatalUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        new Thread(ListenForActivationRequests) { IsBackground = true, Name = "OficinaVirtual.Activate" }.Start();

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.HiddenToTray += (_, _) => ShowTrayHintOnce();
        CreateTrayIcon();
        _mainWindow.Show();
    }

    private static void SignalRunningInstance()
    {
        // La primera instancia crea el evento justo al arrancar; si la pillamos en ese instante, se
        // reintenta un poco antes de rendirse.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                // Permite que la oficina ya abierta se ponga en primer plano (Windows lo impide a
                // procesos en segundo plano salvo que quien tiene el foco — nosotros — lo autorice).
                Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
                activate.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private void ListenForActivationRequests()
    {
        try
        {
            while (_activateEvent != null && _activateEvent.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(new Action(() => _mainWindow?.ShowOffice()));
            }
        }
        catch (ObjectDisposedException)
        {
            // Salida de la app: el evento ya se ha cerrado.
        }
    }

    // --- Bandeja del sistema ---

    private void CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir oficina", null, (_, _) => _mainWindow?.ShowOffice());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Salir de verdad", null, (_, _) => ExitOffice());

        System.Drawing.Icon icon;
        try
        {
            icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Oficina Virtual",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => _mainWindow?.ShowOffice();
    }

    private void ShowTrayHintOnce()
    {
        if (_trayHintShown || _trayIcon == null) return;
        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(4000, "La oficina sigue abierta",
            "Tus herramientas siguen dentro, tal cual. Doble clic aquí para volver; clic derecho → \"Salir de verdad\" para cerrarla.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    // --- Salir de verdad / apagado ---

    /// <summary>
    /// "Salir de verdad": se guarda todo y cada herramienta se cierra como si se pulsara su propia
    /// X. Al volver a abrir la oficina se relanzan en su sitio.
    /// </summary>
    private async void ExitOffice()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            if (_mainWindow != null)
                await _mainWindow.ExitAndCloseToolsAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write("ExitOffice", ex);
            EmbeddedWindowRegistry.EmergencyReleaseAll();
        }

        DisposeTrayIcon();
        _mainWindow?.CloseForExit();
        Shutdown();
    }

    /// <summary>
    /// Apagar/reiniciar/cerrar sesión: se guarda todo y cada herramienta vuelve a ser una ventana
    /// normal (minimizada), para que Windows la cierre como a cualquier otra app (con su aviso de
    /// guardar cambios si lo necesita) en vez de destruirla junto con la oficina.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try
        {
            if (!_exiting)
            {
                _exiting = true;
                _mainWindow?.PrepareForSessionEnd();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnSessionEnding", ex);
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DisposeTrayIcon();

        _activateEvent?.Dispose();
        _activateEvent = null;

        if (_singleInstanceMutex != null)
        {
            try
            {
                if (_ownsMutex) _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // No es nuestro o ya se liberó — no importa al salir.
            }
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null) return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    // --- Red de seguridad ante errores ---

    /// <summary>
    /// Error en el hilo de la interfaz: se registra y la app sigue viva. Antes, cualquier error así
    /// cerraba la oficina de golpe — y Windows destruía con ella todas las herramientas embebidas.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write("DispatcherUnhandledException", e.Exception);
        e.Handled = true;

        try
        {
            _mainWindow?.ShowStatus("Se produjo un error interno (registrado en crash.log). La oficina sigue funcionando.");
        }
        catch
        {
            // El aviso es secundario.
        }
    }

    /// <summary>
    /// Error fatal (no recuperable): antes de que el proceso muera, se sueltan todas las
    /// herramientas embebidas para que Windows no las destruya con él.
    /// </summary>
    private void OnFatalUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        CrashLog.Write("Fatal UnhandledException", e.ExceptionObject as Exception);
        EmbeddedWindowRegistry.EmergencyReleaseAll();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
