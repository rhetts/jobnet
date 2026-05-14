using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jobnet.ViewModels;

namespace Jobnet;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CompanyList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (vm.SelectedCompany is null) return;
        if (vm.SelectedCompany.IsAllJobsSentinel) return;
        if (vm.SelectedCompany.Company is null) return;
        vm.OpenCompanyProfile(vm.SelectedCompany.Company.Id);
    }
}
