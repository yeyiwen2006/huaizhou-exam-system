using System.Windows;
using System.Windows.Controls;

namespace Exam.Desktop.Shared.Controls;

/// <summary>
/// 带标签、水印与错误反馈的输入框。
/// IsNumericOnly=true 时仅允许输入数字（班级、准考证号）。
/// </summary>
public partial class ValidatedInput : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(ValidatedInput),
            new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty WatermarkProperty =
        DependencyProperty.Register(nameof(Watermark), typeof(string), typeof(ValidatedInput),
            new PropertyMetadata(string.Empty, OnWatermarkChanged));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(ValidatedInput),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnTextChanged));

    public static readonly DependencyProperty IsNumericOnlyProperty =
        DependencyProperty.Register(nameof(IsNumericOnly), typeof(bool), typeof(ValidatedInput),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ErrorTextProperty =
        DependencyProperty.Register(nameof(ErrorText), typeof(string), typeof(ValidatedInput),
            new PropertyMetadata(string.Empty, OnErrorChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Watermark
    {
        get => (string)GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsNumericOnly
    {
        get => (bool)GetValue(IsNumericOnlyProperty);
        set => SetValue(IsNumericOnlyProperty, value);
    }

    public string ErrorText
    {
        get => (string)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    /// <summary>文本内容变化事件（Text 已更新）。</summary>
    public event EventHandler? InputChanged;

    public ValidatedInput()
    {
        InitializeComponent();
        Input.PreviewTextInput += OnPreviewTextInput;
        DataObject.AddPastingHandler(Input, OnPaste);
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ValidatedInput)d).LabelText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ValidatedInput)d;
        c.WatermarkText.Text = e.NewValue as string ?? string.Empty;
        c.UpdateWatermark();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ValidatedInput)d;
        if (!ReferenceEquals(c.Input.Text, e.NewValue))
        {
            c.Input.Text = e.NewValue as string ?? string.Empty;
        }

        c.UpdateWatermark();
    }

    private static void OnErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (ValidatedInput)d;
        var msg = e.NewValue as string ?? string.Empty;
        c.ErrorTextBlock.Text = msg;
        c.ErrorTextBlock.Visibility = string.IsNullOrEmpty(msg)
            ? Visibility.Collapsed
            : Visibility.Visible;
        c.Input.BorderBrush = string.IsNullOrEmpty(msg)
            ? (System.Windows.Media.Brush)c.FindResource("Brush.Border.Strong")
            : (System.Windows.Media.Brush)c.FindResource("Brush.Danger");
    }

    private void UpdateWatermark()
    {
        WatermarkText.Visibility = string.IsNullOrEmpty(Input.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ReferenceEquals(Text, Input.Text))
        {
            SetCurrentValue(TextProperty, Input.Text);
        }

        InputChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (IsNumericOnly)
        {
            e.Handled = e.Text.Any(ch => ch is < '0' or > '9');
        }
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!IsNumericOnly)
        {
            return;
        }

        if (e.DataObject.GetData(typeof(string)) is not string pasted
            || pasted.Any(ch => ch is < '0' or > '9'))
        {
            e.CancelCommand();
        }
    }
}
