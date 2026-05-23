using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Jobnet.ViewModels;

namespace Jobnet;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CompanyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.SelectedCompany is null) return;
        if (vm.SelectedCompany.IsAllJobsSentinel) return;
        if (vm.SelectedCompany.Company is null) return;
        vm.OpenCompanyProfile(vm.SelectedCompany.Company.Id);
    }

    private void JobsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb) return;
        if (lb.SelectedItem is not JobViewModel jvm) return;
        var url = jvm.Job.Url;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.StatusBarText = $"Could not open {url}: {ex.Message}";
        }
    }

    /// <summary>Single-click on a job card toggles the accordion. Skipped if the click landed on
    /// an inner Button/ToggleButton/CheckBox — those handle their own clicks.</summary>
    private void JobCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not JobViewModel jvm) return;

        // Walk up from the original source — if any ancestor is a button, the click belongs to that
        // button and we should not also toggle the card.
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null && d != fe)
        {
            if (d is ButtonBase) return;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }

        jvm.IsExpanded = !jvm.IsExpanded;
    }
}
