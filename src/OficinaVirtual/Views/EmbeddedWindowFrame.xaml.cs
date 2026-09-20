using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace OficinaVirtual.Views;

/// <summary>
/// Marco visual que aloja una ventana nativa embebida, con dos modos:
/// Icon (cuadrado con el icono de la app, sin ventana real visible — se puede arrastrar libremente
/// sin ningún conflicto con ventanas nativas) y Expanded (tamaño real, cabecera arrastrable +
/// contenido embebido + tirador de redimensionado). No conoce Win32: solo notifica eventos;
/// la orquestación real (mostrar/ocultar la ventana nativa, rejilla de iconos, etc.) la hace
/// CanvasTabView.
/// </summary>
public partial class EmbeddedWindowFrame : UserControl
{
    public enum WindowMode { Icon, Expanded }

    /// <summary>Alto fijo de la cabecera (RowDefinition Height="28" en el XAML).</summary>
    public const double HeaderHeight = 28;
    /// <summary>Grosor fijo del borde exterior (BorderThickness="10" en el XAML).</summary>
    public const double FrameBorderThickness = 10;
    private const double MinFrameWidth = 200;
    private const double MinFrameHeight = 150;

    public const double IconWidth = 88;
    public const double IconHeight = 104;

    private const double DragThreshold = 4;

    public IntPtr TargetHwnd { get; set; }
    public WindowMode CurrentMode { get; private set; } = WindowMode.Icon;

    public event EventHandler? MoveOrResize;
    public event EventHandler? CloseRequested;
    public event EventHandler? Selected;
    public event EventHandler? ExpandRequested;
    public event EventHandler? MinimizeRequested;
    public event EventHandler? IconDropped;

    /// <summary>
    /// Color base del marco. Hoy es un gris-azulado neutro para todas las ventanas; en el futuro
    /// cada ventana podrá tener el suyo propio, y la selección seguirá expresándose igual:
    /// atenuado (no seleccionada) / intenso (seleccionada).
    /// </summary>
    public Color AccentColor { get; set; } = Color.FromRgb(0x3E, 0x5C, 0x8A);

    private bool _isSelected;
    private double _expandedWidth = 640;
    private double _expandedHeight = 420;
    private double _expandedLeft = double.NaN;
    private double _expandedTop = double.NaN;

    /// <summary>Última posición conocida en modo Icono, independiente de la posición en modo Expandido.</summary>
    public Point? LastIconPosition { get; private set; }

    private bool _dragging;
    private Point _dragStartMouse;
    private double _dragStartLeft;
    private double _dragStartTop;

    private bool _iconPotentialDrag;
    private bool _iconDragging;
    private Point _iconDragStartMouse;
    private double _iconDragStartLeft;
    private double _iconDragStartTop;

    public EmbeddedWindowFrame()
    {
        InitializeComponent();
        SetSelected(false);
        IconView.Background = new SolidColorBrush(Dim(AccentColor)); // fijo: el icono nunca cambia de color
        Width = IconWidth;
        Height = IconHeight;
    }

    public string TitleValue
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value;
            IconTitleText.Text = value;
        }
    }

    public ImageSource? IconSource
    {
        set => IconImage.Source = value;
    }

    public Border EmbedTargetElement => EmbedTarget;

    private static readonly Color UnselectedGray = Color.FromRgb(0xC4, 0xC4, 0xC8);

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        var background = selected ? Dim(AccentColor) : UnselectedGray;
        var foreground = selected ? Colors.White : Color.FromRgb(0x20, 0x20, 0x20);

        HeaderBar.Background = new SolidColorBrush(background);
        IconView.BorderBrush = new SolidColorBrush(selected ? Colors.DodgerBlue : Colors.Transparent);
        TitleText.Foreground = new SolidColorBrush(foreground);
        IconTitleText.Foreground = new SolidColorBrush(foreground);
    }

    public void SetMode(WindowMode mode)
    {
        if (CurrentMode == mode) return;

        if (mode == WindowMode.Expanded)
        {
            if (CurrentMode == WindowMode.Icon)
                LastIconPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

            CurrentMode = WindowMode.Expanded;
            Width = _expandedWidth;
            Height = _expandedHeight;
            // Si ya habíamos estado en tamaño real antes, recuperamos esa posición exacta;
            // si es la primera vez, se queda creciendo desde donde estaba el icono.
            if (!double.IsNaN(_expandedLeft)) Canvas.SetLeft(this, _expandedLeft);
            if (!double.IsNaN(_expandedTop)) Canvas.SetTop(this, _expandedTop);
            IconView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
        }
        else
        {
            // Recordamos tamaño y posición actuales por si se vuelve a expandir más adelante.
            _expandedWidth = Width;
            _expandedHeight = Height;
            _expandedLeft = Canvas.GetLeft(this);
            _expandedTop = Canvas.GetTop(this);

            CurrentMode = WindowMode.Icon;
            Width = IconWidth;
            Height = IconHeight;
            ExpandedView.Visibility = Visibility.Collapsed;
            IconView.Visibility = Visibility.Visible;
        }
    }

    private static Color Dim(Color c) => Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));

    // --- Modo icono: arrastrar libremente; un clic (sin arrastrar) expande a tamaño real ---

    private void IconView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Parent is not Canvas canvas) return;

        _iconPotentialDrag = true;
        _iconDragging = false;
        _iconDragStartMouse = e.GetPosition(canvas);
        _iconDragStartLeft = Canvas.GetLeft(this);
        _iconDragStartTop = Canvas.GetTop(this);
        IconView.CaptureMouse();
        e.Handled = true;
    }

    private void IconView_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_iconPotentialDrag || Parent is not Canvas canvas) return;

        var current = e.GetPosition(canvas);
        var deltaX = current.X - _iconDragStartMouse.X;
        var deltaY = current.Y - _iconDragStartMouse.Y;

        if (!_iconDragging && (Math.Abs(deltaX) > DragThreshold || Math.Abs(deltaY) > DragThreshold))
            _iconDragging = true;

        if (_iconDragging)
        {
            Canvas.SetLeft(this, _iconDragStartLeft + deltaX);
            Canvas.SetTop(this, _iconDragStartTop + deltaY);
        }
    }

    private void IconView_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        IconView.ReleaseMouseCapture();
        if (!_iconPotentialDrag) return;
        _iconPotentialDrag = false;

        if (_iconDragging)
        {
            _iconDragging = false;
            IconDropped?.Invoke(this, EventArgs.Empty);
        }
        else if (_isSelected)
        {
            ExpandRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Selected?.Invoke(this, EventArgs.Empty);
        }
    }

    // --- Modo tamaño real: cabecera arrastrable, tirador de redimensionado ---

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Parent is not Canvas canvas) return;
        if (IsWithin(e.OriginalSource, CloseButton) || IsWithin(e.OriginalSource, MinimizeButton)) return;

        Selected?.Invoke(this, EventArgs.Empty);

        _dragging = true;
        _dragStartMouse = e.GetPosition(canvas);
        _dragStartLeft = Canvas.GetLeft(this);
        _dragStartTop = Canvas.GetTop(this);
        HeaderBar.CaptureMouse();
        e.Handled = true;
    }

    private void HeaderBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Parent is not Canvas canvas) return;

        var current = e.GetPosition(canvas);
        var deltaX = current.X - _dragStartMouse.X;
        var deltaY = current.Y - _dragStartMouse.Y;

        Canvas.SetLeft(this, _dragStartLeft + deltaX);
        Canvas.SetTop(this, _dragStartTop + deltaY);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void HeaderBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        HeaderBar.ReleaseMouseCapture();
    }

    // --- Tiradores de redimensionado (4 lados + 4 esquinas) ---
    // Los del lado izquierdo/arriba, además de cambiar Width/Height, desplazan Canvas.Left/Top
    // para que el lado contrario quede fijo mientras se estira desde ese lado.

    private void GrowRight(double dx) => Width = Math.Max(MinFrameWidth, Width + dx);

    private void GrowBottom(double dy) => Height = Math.Max(MinFrameHeight, Height + dy);

    private void GrowLeft(double dx)
    {
        double newWidth = Math.Max(MinFrameWidth, Width - dx);
        double appliedDx = Width - newWidth;
        Width = newWidth;
        Canvas.SetLeft(this, Canvas.GetLeft(this) + appliedDx);
    }

    private void GrowTop(double dy)
    {
        double newHeight = Math.Max(MinFrameHeight, Height - dy);
        double appliedDy = Height - newHeight;
        Height = newHeight;
        Canvas.SetTop(this, Canvas.GetTop(this) + appliedDy);
    }

    private void ThumbLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowLeft(e.HorizontalChange);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void ThumbRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowRight(e.HorizontalChange);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void ThumbTop_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowTop(e.VerticalChange);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void ThumbBottom_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowBottom(e.VerticalChange);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void ThumbTopLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowLeft(e.HorizontalChange);
        GrowTop(e.VerticalChange);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void ThumbTopRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowRight(e.HorizontalChange);
        GrowTop(e.VerticalChange);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void ThumbBottomLeft_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowLeft(e.HorizontalChange);
        GrowBottom(e.VerticalChange);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void ThumbBottomRight_DragDelta(object sender, DragDeltaEventArgs e)
    {
        GrowRight(e.HorizontalChange);
        GrowBottom(e.VerticalChange);
        MoveOrResize?.Invoke(this, EventArgs.Empty);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        MinimizeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsWithin(object originalSource, DependencyObject ancestor)
    {
        if (originalSource is not DependencyObject d) return false;
        while (d != null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}
