using System.Windows;
using Jobnet.ViewModels;

namespace Jobnet.Views;

public partial class CompanyProfileWindow : Window
{
    public CompanyProfileWindow(CompanyProfileViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
