using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Exam.Desktop.Shared.Controls;

/// <summary>消息条：危险 / 成功 / 信息 / 警告。IsOpen 控制显隐，Message 为文案。</summary>
public partial class Banner : UserControl
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(Banner),
            new PropertyMetadata(string.Empty, OnStateChanged));

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(Banner),
            new PropertyMetadata(false, OnStateChanged));

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(StatusLevel), typeof(Banner),
            new PropertyMetadata(StatusLevel.Danger, OnStateChanged));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public StatusLevel Level
    {
        get => (StatusLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Banner()
    {
        InitializeComponent();
    }

    public void Show(string message, StatusLevel level = StatusLevel.Danger)
    {
        Message = message;
        Level = level;
        IsOpen = true;
    }

    public void Hide() => IsOpen = false;

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((Banner)d).UpdateVisual();
    }

    private void UpdateVisual()
    {
        Root.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
        MessageText.Text = Message ?? string.Empty;
        var (fg, soft, icon) = Level switch
        {
            StatusLevel.Success => ("Brush.Success", "Brush.Success.Soft", "Icon.Check"),
            StatusLevel.Info => ("Brush.Info", "Brush.Info.Soft", "Icon.Info"),
            StatusLevel.Warning => ("Brush.Warning", "Brush.Warning.Soft", "Icon.Warn"),
            _ => ("Brush.Danger", "Brush.Danger.Soft", "Icon.Alert"),
        };
        var brush = (Brush)FindResource(fg);
        Root.Background = (Brush)FindResource(soft);
        Icon.Stroke = brush;
        Icon.Data = (Geometry)FindResource(icon);
    }
}
