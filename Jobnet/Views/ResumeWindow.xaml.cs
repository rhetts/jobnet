using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class ResumeWindow : Window
{
    public ResumeWindow(ResumeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
