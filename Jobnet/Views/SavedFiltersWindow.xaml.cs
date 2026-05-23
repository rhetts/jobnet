using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class SavedFiltersWindow : Window
{
    public SavedFiltersWindow(SavedFiltersViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
