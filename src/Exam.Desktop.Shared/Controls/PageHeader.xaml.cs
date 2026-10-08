using System.Windows;
using System.Windows.Controls;

namespace Exam.Desktop.Shared.Controls;

/// <summary>
/// 统一页头：左上角设备编号与考生姓名区域，右侧操作插槽。
/// 编号取登录时服务器分配的 clientNumber，姓名随登录切换，保证无前一学生内容。
/// </summary>
public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty ClientNumberProperty =
        DependencyProperty.Register(nameof(ClientNumber), typeof(string), typeof(PageHeader),
            new PropertyMetadata(null, OnIdentityChanged));

    public static readonly DependencyProperty CandidateNameProperty =
        DependencyProperty.Register(nameof(CandidateName), typeof(string), typeof(PageHeader),
            new PropertyMetadata(null, OnIdentityChanged));

    public static readonly DependencyProperty ClassTextProperty =
        DependencyProperty.Register(nameof(ClassText), typeof(string), typeof(PageHeader),
            new PropertyMetadata(null, OnIdentityChanged));

    public static readonly DependencyProperty RightContentProperty =
        DependencyProperty.Register(nameof(RightContent), typeof(object), typeof(PageHeader),
            new PropertyMetadata(null));

    public string ClientNumber
    {
        get => (string)GetValue(ClientNumberProperty);
        set => SetValue(ClientNumberProperty, value);
    }

    public string CandidateName
    {
        get => (string)GetValue(CandidateNameProperty);
        set => SetValue(CandidateNameProperty, value);
    }

    public string ClassText
    {
        get => (string)GetValue(ClassTextProperty);
        set => SetValue(ClassTextProperty, value);
    }

    public object? RightContent
    {
        get => GetValue(RightContentProperty);
        set => SetValue(RightContentProperty, value);
    }

    public PageHeader()
    {
        InitializeComponent();
    }

    private static void OnIdentityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((PageHeader)d).UpdateIdentity();
    }

    private void UpdateIdentity()
    {
        NumberText.Text = string.IsNullOrEmpty(ClientNumber) ? "未连接" : $"编号 {ClientNumber}";
        NameText.Text = string.IsNullOrEmpty(CandidateName) ? "未登录" : CandidateName;
        ClassTextBlock.Text = ClassText ?? string.Empty;
    }
}
