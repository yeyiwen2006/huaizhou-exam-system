using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Exam.Desktop.Shared.Controls;

public enum StatusLevel
{
    Neutral,
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>状态标识胶囊：圆点 + 文字，按级别取语义色。</summary>
public partial class StatusPill : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusPill),
            new PropertyMetadata(string.Empty, OnVisualChanged));

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(StatusLevel), typeof(StatusPill),
            new PropertyMetadata(StatusLevel.Neutral, OnVisualChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusLevel Level
    {
        get => (StatusLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public StatusPill()
    {
        InitializeComponent();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((StatusPill)d).UpdateVisual();
    }

    private void UpdateVisual()
    {
        Label.Text = Text;
        var (fg, soft) = Level switch
        {
            StatusLevel.Info => ("Brush.Info", "Brush.Info.Soft"),
            StatusLevel.Success => ("Brush.Success", "Brush.Success.Soft"),
            StatusLevel.Warning => ("Brush.Warning", "Brush.Warning.Soft"),
            StatusLevel.Danger => ("Brush.Danger", "Brush.Danger.Soft"),
            _ => ("Brush.Text.Secondary", "Brush.Surface.Alt"),
        };
        var brush = (Brush)FindResource(fg);
        var bg = (Brush)FindResource(soft);
        Dot.Fill = brush;
        Label.Foreground = brush;
        Root.Background = bg;
    }
}
