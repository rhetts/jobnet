using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class SourcesWindow : Window
{
    public SourcesWindow(SourcesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
