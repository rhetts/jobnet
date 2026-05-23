using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class ServiceLimitsWindow : Window
{
    public ServiceLimitsWindow(ServiceLimitsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
