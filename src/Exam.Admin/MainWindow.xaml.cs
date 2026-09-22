using System.Windows;
using Exam.Admin.ViewModels;

namespace Exam.Admin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    private void OperatorToken_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox passwordBox)
        {
            _viewModel.Connection.OperatorToken = passwordBox.Password;
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e) => _viewModel.Dispose();
}
