using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OficinaVirtual.Views;

/// <summary>
/// Cabecera de una pestaña de la oficina: nombre (doble clic para renombrar) y X para cerrarla.
/// </summary>
public partial class CanvasTabHeader : UserControl
{
    public event EventHandler? CloseRequested;

    public CanvasTabHeader(string name)
    {
        InitializeComponent();
        NameText.Text = name;
    }

    public string TabName => NameText.Text;

    private void NameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return; // un clic normal sigue seleccionando la pestaña

        e.Handled = true;
        NameEditor.Text = NameText.Text;
        NameText.Visibility = Visibility.Collapsed;
        NameEditor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            NameEditor.Focus();
            NameEditor.SelectAll();
        }));
    }

    private void NameEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EndRename(commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndRename(commit: false);
            e.Handled = true;
        }
    }

    private void NameEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(commit: true);

    private void EndRename(bool commit)
    {
        if (NameEditor.Visibility != Visibility.Visible) return;

        var newName = NameEditor.Text.Trim();
        if (commit && newName.Length > 0)
            NameText.Text = newName; // un nombre vacío no se acepta: se mantiene el anterior

        NameEditor.Visibility = Visibility.Collapsed;
        NameText.Visibility = Visibility.Visible;
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
