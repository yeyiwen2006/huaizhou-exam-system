using System.Windows;
using System.Windows.Controls;

namespace Exam.Desktop.Shared.Controls;

/// <summary>
/// 统一确认弹窗。ShowAsync 返回用户是否确认。
/// IsDanger=true 时确认按钮使用红色（提交答卷 / 退出系统等）。
/// </summary>
public partial class ConfirmDialog : Window
{
    private TaskCompletionSource<bool> _tcs = new();

    public ConfirmDialog()
    {
        if (Application.Current is null) Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        InitializeComponent();
        Closed += (_, _) => _tcs.TrySetResult(false);
    }

    public static Task<bool> ShowAsync(
        Window owner,
        string title,
        object body,
        string confirmText = "确定",
        string cancelText = "取消",
        bool isDanger = false)
    {
        var dialog = new ConfirmDialog
        {
            Owner = owner,
            Title = title,
        };
        dialog.TitleText.Text = title;
        dialog.BodyHost.Content = body is string s
            ? new TextBlock
            {
                Text = s,
                TextWrapping = TextWrapping.Wrap,
                FontSize = (double)dialog.FindResource("Font.Size.Body"),
                Foreground = (System.Windows.Media.Brush)dialog.FindResource("Brush.Text.Primary"),
            }
            : body;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;
        if (isDanger)
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("Btn.Danger");
        }

        return dialog.ShowDialogAsync();
    }

    private Task<bool> ShowDialogAsync()
    {
        var result = _tcs.Task;
        ShowDialog();
        return result;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(true);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _tcs.TrySetResult(false);
        Close();
    }
}
