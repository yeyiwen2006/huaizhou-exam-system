using System.Windows;
using System.Windows.Controls;
using Exam.Admin.ViewModels;

namespace Exam.Admin.Views;

public partial class RegistrationSuccessPage : UserControl
{
    public RegistrationSuccessPage(ConnectionViewModel viewModel)
    {
        if (System.Windows.Application.Current is null) Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        DataContext = viewModel;
        InitializeComponent();
    }
}
