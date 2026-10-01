using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class SourcesWindow : Window
{
    public SourcesWindow(SourcesViewModel sources, ParserReportViewModel parsers)
    {
        InitializeComponent();
        SourcesTab.DataContext = sources;
        ParsersTab.DataContext = parsers;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
