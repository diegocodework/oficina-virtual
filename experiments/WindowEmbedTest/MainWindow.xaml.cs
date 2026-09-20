using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace WindowEmbedTest;

public partial class MainWindow : Window
{
    // --- Win32 interop ---

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const uint GW_OWNER = 4;
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const int SW_SHOW = 5;

    private class WindowEntry
    {
        public IntPtr Handle;
        public string Title = "";
        public override string ToString() => $"{Title}  [hwnd={Handle}]";
    }

    private IntPtr _embeddedHwnd = IntPtr.Zero;
    private int _originalStyle;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = new List<WindowEntry>();
        var myProcessId = Process.GetCurrentProcess().Id;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true; // solo ventanas de nivel superior

            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hWnd, out int pid);
            if (pid == myProcessId) return true; // no listar nuestra propia ventana

            entries.Add(new WindowEntry { Handle = hWnd, Title = title });
            return true;
        }, IntPtr.Zero);

        WindowsCombo.ItemsSource = entries;
        if (entries.Count > 0) WindowsCombo.SelectedIndex = 0;
    }

    private void EmbedButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsCombo.SelectedItem is not WindowEntry entry) return;
        if (_embeddedHwnd != IntPtr.Zero) ReleaseEmbeddedWindow();

        IntPtr targetHwnd = entry.Handle;
        IntPtr myHwnd = new WindowInteropHelper(this).Handle;

        _originalStyle = GetWindowLong(targetHwnd, GWL_STYLE);

        int newStyle = _originalStyle;
        newStyle &= ~WS_POPUP;
        newStyle &= ~WS_CAPTION;
        newStyle &= ~WS_THICKFRAME;
        newStyle &= ~WS_SYSMENU;
        newStyle |= WS_CHILD;
        SetWindowLong(targetHwnd, GWL_STYLE, newStyle);

        SetParent(targetHwnd, myHwnd);

        _embeddedHwnd = targetHwnd;
        PlaceholderText.Visibility = Visibility.Collapsed;

        PositionEmbeddedWindow();
        ShowWindow(targetHwnd, SW_SHOW);
    }

    private void EmbedContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionEmbeddedWindow();
    }

    private void PositionEmbeddedWindow()
    {
        if (_embeddedHwnd == IntPtr.Zero) return;

        var topLeft = EmbedContainer.PointToScreen(new Point(0, 0));
        var myHwnd = new WindowInteropHelper(this).Handle;

        // Convertimos coordenadas de pantalla a coordenadas de cliente de nuestra ventana.
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var clientTopLeft = PointFromScreen(topLeft);

        int x = (int)(clientTopLeft.X * dpiX);
        int y = (int)(clientTopLeft.Y * dpiY);
        int width = (int)(EmbedContainer.ActualWidth * dpiX);
        int height = (int)(EmbedContainer.ActualHeight * dpiY);

        if (width <= 0 || height <= 0) return;

        MoveWindow(_embeddedHwnd, x, y, width, height, true);
    }

    private void ReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        ReleaseEmbeddedWindow();
    }

    private void ReleaseEmbeddedWindow()
    {
        if (_embeddedHwnd == IntPtr.Zero) return;

        SetWindowLong(_embeddedHwnd, GWL_STYLE, _originalStyle);
        SetParent(_embeddedHwnd, IntPtr.Zero);
        MoveWindow(_embeddedHwnd, 100, 100, 800, 600, true);
        ShowWindow(_embeddedHwnd, SW_SHOW);

        _embeddedHwnd = IntPtr.Zero;
        PlaceholderText.Visibility = Visibility.Visible;
    }

    protected override void OnClosed(EventArgs e)
    {
        ReleaseEmbeddedWindow();
        base.OnClosed(e);
    }
}
