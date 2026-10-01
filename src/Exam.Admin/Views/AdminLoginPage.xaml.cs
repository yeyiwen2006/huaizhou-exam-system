using System.Windows;
using System.Windows.Controls;
using Exam.Admin.ViewModels;

namespace Exam.Admin.Views;

public partial class AdminLoginPage : UserControl
{
    private readonly ConnectionViewModel _vm;

    public AdminLoginPage(ConnectionViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
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

    public void Detach() => _vm.PasswordsCleared -= OnPasswordsCleared;
}
