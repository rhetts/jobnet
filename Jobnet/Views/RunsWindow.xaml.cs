using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class RunsWindow : Window
{
    public RunsWindow(RunsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
