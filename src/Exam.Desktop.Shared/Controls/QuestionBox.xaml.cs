using System.Windows;
using System.Windows.Controls;

namespace Exam.Desktop.Shared.Controls;

/// <summary>
/// 题号方框。IsAnswered 填充绿色；IsMarked 右上角红色圆点；IsCurrent 主色描边。
/// </summary>
public partial class QuestionBox : UserControl
{
    public static readonly DependencyProperty NumberProperty =
        DependencyProperty.Register(nameof(Number), typeof(int), typeof(QuestionBox),
            new PropertyMetadata(1, OnVisualChanged));

    public static readonly DependencyProperty IsAnsweredProperty =
        DependencyProperty.Register(nameof(IsAnswered), typeof(bool), typeof(QuestionBox),
            new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty IsMarkedProperty =
        DependencyProperty.Register(nameof(IsMarked), typeof(bool), typeof(QuestionBox),
            new PropertyMetadata(false, OnVisualChanged));

    public static readonly DependencyProperty IsCurrentProperty =
        DependencyProperty.Register(nameof(IsCurrent), typeof(bool), typeof(QuestionBox),
            new PropertyMetadata(false, OnVisualChanged));

    public int Number
    {
        get => (int)GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    public bool IsAnswered
    {
        get => (bool)GetValue(IsAnsweredProperty);
        set => SetValue(IsAnsweredProperty, value);
    }

    public bool IsMarked
    {
        get => (bool)GetValue(IsMarkedProperty);
        set => SetValue(IsMarkedProperty, value);
    }

    public bool IsCurrent
    {
        get => (bool)GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    public event EventHandler? BoxClicked;

    public QuestionBox()
    {
        InitializeComponent();
        MouseLeftButtonUp += (_, _) => BoxClicked?.Invoke(this, EventArgs.Empty);
        Cursor = System.Windows.Input.Cursors.Hand;
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((QuestionBox)d).UpdateVisual();
    }

    private void UpdateVisual()
    {
        NumberText.Text = Number.ToString();
        MarkDot.Visibility = IsMarked ? Visibility.Visible : Visibility.Collapsed;
        Box.Background = IsAnswered
            ? (System.Windows.Media.Brush)FindResource("Brush.Answer")
            : (System.Windows.Media.Brush)FindResource("Brush.Surface");
        NumberText.Foreground = IsAnswered
            ? (System.Windows.Media.Brush)FindResource("Brush.OnPrimary")
            : (System.Windows.Media.Brush)FindResource("Brush.Text.Primary");
        Box.BorderBrush = IsCurrent
            ? (System.Windows.Media.Brush)FindResource("Brush.Primary")
            : (System.Windows.Media.Brush)FindResource("Brush.Border.Strong");
        Box.BorderThickness = IsCurrent ? new Thickness(2) : new Thickness(1);
    }
}
