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
    private readonly Win32.LowLevelMouseProc _proc; // mantiene viva la referencia (evita que el GC la recoja)
    private IntPtr _hookHandle;

    public event Action<Point>? LeftButtonDown;

    public GlobalMouseHook()
    {
        _proc = HookCallback;
        _hookHandle = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)Win32.WM_LBUTTONDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            LeftButtonDown?.Invoke(new Point(hookStruct.pt.X, hookStruct.pt.Y));
        }

        return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }
}
