using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class FiltersWindow : Window
{
    public FiltersWindow(FiltersViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
