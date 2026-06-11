using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class ParserReportWindow : Window
{
    public ParserReportWindow(ParserReportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
