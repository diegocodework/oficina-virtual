using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace OficinaVirtual.Services;

/// <summary>
/// Hook de ratón de bajo nivel (WH_MOUSE_LL) — se instala en nuestro propio proceso, sin
/// necesidad de inyectar nada en las apps embebidas, y avisa de cada clic izquierdo en cualquier
/// punto de la pantalla. Se usa para saber si el usuario ha hecho clic directamente sobre el
/// contenido de una ventana embebida (algo que no podemos detectar de otra forma, ya que ese clic
/// nunca llega a nuestros controles WPF).
/// </summary>
internal sealed class GlobalMouseHook : IDisposable
{
    // Mientras el hook esté instalado, Windows guarda un puntero a _proc. Esta referencia estática
    // garantiza que el recolector de basura nunca lo recoja aunque el dueño de este objeto se pierda
    // (si lo recogiera, la siguiente llamada de Windows mataría el proceso en seco).
    private static readonly HashSet<GlobalMouseHook> Installed = new();

    private readonly Win32.LowLevelMouseProc _proc;
    private IntPtr _hookHandle;

    public event Action<Point>? LeftButtonDown;

    /// <summary>Se usa para detectar cuándo se suelta el arrastre de una ventana ajena sobre el canvas.</summary>
    public event Action<Point>? LeftButtonUp;

    public GlobalMouseHook()
    {
        _proc = HookCallback;
        lock (Installed) Installed.Add(this);
        _hookHandle = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Una excepción que escapara de aquí atravesaría código nativo y tumbaría el proceso — y con
        // él todas las ventanas embebidas. Se registra y se sigue.
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)Win32.WM_LBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                LeftButtonDown?.Invoke(new Point(hookStruct.pt.X, hookStruct.pt.Y));
            }
            else if (nCode >= 0 && wParam == (IntPtr)Win32.WM_LBUTTONUP)
            {
                var hookStruct = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                LeftButtonUp?.Invoke(new Point(hookStruct.pt.X, hookStruct.pt.Y));
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("GlobalMouseHook.HookCallback", ex);
        }

        return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        lock (Installed) Installed.Remove(this);
    }
}
