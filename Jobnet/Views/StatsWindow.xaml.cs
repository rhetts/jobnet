using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class StatsWindow : Window
{
    public StatsWindow(StatsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
