using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
