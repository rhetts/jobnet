using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class RefreshWindow : Window
{
    public RefreshWindow(RefreshViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
