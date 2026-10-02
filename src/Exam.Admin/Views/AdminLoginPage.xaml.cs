using System.Windows;
using System.Windows.Controls;
using Exam.Admin.ViewModels;

namespace Exam.Admin.Views;

public partial class AdminLoginPage : UserControl
{
    private readonly ConnectionViewModel _vm;
    public event Action? ManagementRequested;

    private void OnManagementRequested(object sender, RoutedEventArgs e) => ManagementRequested?.Invoke();

    public AdminLoginPage(ConnectionViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        if (Application.Current is null) Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        InitializeComponent();
        vm.PasswordsCleared += OnPasswordsCleared;
    }

    private void Password_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        _vm.Password = PasswordInput.Password;

    private void ConfirmPassword_OnPasswordChanged(object sender, RoutedEventArgs e) =>
        _vm.ConfirmPassword = ConfirmPasswordInput.Password;

    private void OnPasswordsCleared()
    {
        PasswordInput.Clear();
        ConfirmPasswordInput.Clear();
    }

    public void Detach()
    {
        PasswordInput.Clear();
        ConfirmPasswordInput.Clear();
        _vm.PasswordsCleared -= OnPasswordsCleared;
    }
}
