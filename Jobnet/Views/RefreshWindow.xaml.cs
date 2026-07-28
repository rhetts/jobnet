using System;
using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class RefreshWindow : Window
{
    private readonly Func<ScanTimesWindow>? _scanTimesWindowFactory;
    private readonly Func<FiltersWindow>? _filtersWindowFactory;

    public RefreshWindow(RefreshViewModel viewModel,
                          Func<ScanTimesWindow>? scanTimesWindowFactory = null,
                          Func<FiltersWindow>? filtersWindowFactory = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _scanTimesWindowFactory = scanTimesWindowFactory;
        _filtersWindowFactory = filtersWindowFactory;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Open the Filters list. Transient, so each open re-reads filter_rule — including
    /// any rule the Scan Times window just added.</summary>
    private void Filters_Click(object sender, RoutedEventArgs e)
    {
        if (_filtersWindowFactory is null) return;
        var win = _filtersWindowFactory();
        win.Owner = this;
        win.ShowDialog();
    }

    /// <summary>Open the Scan Times report. The window is transient, so each open builds a fresh
    /// ViewModel and re-reads the timings — useful right after a run finishes.</summary>
    private void ScanTimes_Click(object sender, RoutedEventArgs e)
    {
        if (_scanTimesWindowFactory is null) return;
        var win = _scanTimesWindowFactory();
        win.Owner = this;
        win.ShowDialog();
    }
}
