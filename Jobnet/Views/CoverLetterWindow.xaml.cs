using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class CoverLetterWindow : Window
{
    public CoverLetterWindow(CoverLetterViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
