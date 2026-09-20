using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OficinaVirtual.Views;

namespace OficinaVirtual;

public partial class MainWindow : Window
{
    private const string NewTabHeader = "+";

    private TabItem? _previouslySelectedTab;

    public MainWindow()
    {
        InitializeComponent();

        MainTabControl.Items.Add(BuildNewTabButton());
        AddCanvasTab("Pestaña 1");
    }

    private TabItem BuildNewTabButton() => new()
    {
        Header = NewTabHeader,
        FontWeight = FontWeights.Bold
    };

    private void AddCanvasTab(string header)
    {
        var tab = new TabItem
        {
            Header = header,
            Content = new CanvasTabView()
        };

        MainTabControl.Items.Insert(MainTabControl.Items.Count - 1, tab);
        MainTabControl.SelectedItem = tab;
    }

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

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) =>
        CurrentCanvasView?.NotifyHostMovedOrResized();

    private void Window_LocationChanged(object sender, EventArgs e) =>
        CurrentCanvasView?.NotifyHostMovedOrResized();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        foreach (var view in AllCanvasViews())
        {
            if (WindowState == WindowState.Minimized)
                view.OnTabDeactivated();
        }

        if (WindowState != WindowState.Minimized)
            CurrentCanvasView?.OnTabActivated();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        foreach (var view in AllCanvasViews())
        {
            view.ReleaseAllEmbeddedWindows();
            view.ShutdownHooks();
        }
    }

    private System.Collections.Generic.IEnumerable<CanvasTabView> AllCanvasViews() =>
        MainTabControl.Items
            .OfType<TabItem>()
            .Select(t => t.Content as CanvasTabView)
            .Where(v => v != null)!;
}
