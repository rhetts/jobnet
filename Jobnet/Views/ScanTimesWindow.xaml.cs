using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class ScanTimesWindow : Window
{
    public ScanTimesWindow(ScanTimesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
