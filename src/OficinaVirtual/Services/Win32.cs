using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OficinaVirtual.Services;

/// <summary>
/// Interop Win32 usado para "reparentar" ventanas nativas de Windows dentro de nuestro canvas.
/// Técnica validada en experiments/WindowEmbedTest antes de construir la app real.
/// </summary>
internal static class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    public static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_ERASE = 0x0004;
    public const uint RDW_ALLCHILDREN = 0x0080;
    public const uint RDW_UPDATENOW = 0x0100;
    public const uint RDW_FRAME = 0x0400;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    public const uint WM_GETICON = 0x007F;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;
    public const int GCL_HICON = -14;
    public const int GCL_HICONSM = -34;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const uint GW_OWNER = 4;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_CHILD = 0x40000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int DWMWA_CLOAKED = 14;
    public const int SW_SHOW = 5;
    public const int SW_HIDE = 0;

    // --- Observación de ventanas de otros procesos (sin subclassing cruzado, que no es fiable) ---

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint idEventTime);

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const int OBJID_WINDOW = 0;

    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;

    // --- Detección de "arrastrar una ventana ajena por su barra de título" ---
    public const uint WM_NCHITTEST = 0x0084;
    public const int HTCAPTION = 2;

    // --- Reenvío de Ctrl+Scroll a la app embebida (para activar su zoom de contenido propio) ---

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_MOUSEWHEEL = 0x020A;
    public const int MK_CONTROL = 0x0008;

    // --- Liberar / cerrar herramientas / instancia única ---

    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINNOACTIVE = 7;
    public const uint WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    public const int ASFW_ANY = -1;

    // --- Barra de menú clásica (Windows no la dibuja en ventanas hijas; la replicamos en WPF) ---

    [StructLayout(LayoutKind.Sequential)]
    public struct MENUITEMINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetMenu(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    public static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMenuItemInfo(IntPtr hMenu, uint uItem, bool fByPosition, ref MENUITEMINFO lpmii);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMenuString(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int nMaxCount, uint uFlag);

    public const uint MF_BYPOSITION = 0x0400;
    public const uint MIIM_STATE = 0x0001;
    public const uint MIIM_ID = 0x0002;
    public const uint MIIM_SUBMENU = 0x0004;
    public const uint MIIM_FTYPE = 0x0100;
    public const uint MFT_SEPARATOR = 0x0800;
    public const uint MFS_DISABLED = 0x0003;
    public const uint MFS_CHECKED = 0x0008;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_INITMENUPOPUP = 0x0117;

    // --- Qué documento tiene abierto una app / cómo relanzarla ---

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        IntPtr processInformation, int processInformationLength, out int returnLength);

    public const int ProcessCommandLineInformation = 60;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryString(uint flags, int str, string pszAssoc, string? pszExtra,
        StringBuilder? pszOut, ref uint pcchOut);

    public const int ASSOCSTR_EXECUTABLE = 2;
    public const uint ASSOCF_NOTRUNCATE = 0x00000020;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>
    /// Título actual de una ventana de otro proceso. GetWindowText no sirve para ventanas embebidas
    /// (sin barra de título propia devuelve vacío); WM_GETTEXT sí, y con timeout no nos cuelga.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    public static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    public const uint WM_GETTEXT = 0x000D;

    // --- Foco del teclado en ventanas embebidas ---

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(int idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(int idAttach, int idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();

    // --- Recorte de ventanas embebidas (región visible) ---

    /// <summary>Tras una llamada correcta la región pasa a ser del sistema: no se debe borrar.</summary>
    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    /// <summary>Devuelve 0 (ERROR) si la ventana no tiene región.</summary>
    [DllImport("user32.dll")]
    public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int mode);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    public const int RGN_AND = 1;
    public const int RGN_DIFF = 4;

    public static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));
}
