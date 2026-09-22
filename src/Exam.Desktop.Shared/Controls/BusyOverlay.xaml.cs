using System.Windows;
using System.Windows.Controls;

namespace Exam.Desktop.Shared.Controls;

/// <summary>
/// 网络等待遮罩。IsBusy 为 true 时显示并阻断输入；IsHitTestingVisible 随状态切换。
/// </summary>
public partial class BusyOverlay : UserControl
{
    public static readonly DependencyProperty IsBusyProperty =
        DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(BusyOverlay),
            new PropertyMetadata(false, OnBusyChanged));

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(BusyOverlay),
            new PropertyMetadata("正在连接服务器…", OnMessageChanged));

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public BusyOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (IsBusy) StartSpin();
        };
    }

    private static void OnBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (BusyOverlay)d;
        var busy = (bool)e.NewValue;
        c.Root.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        c.IsHitTestVisible = busy;
        if (busy)
        {
            c.StartSpin();
        }
        else
        {
            c.StopSpin();
        }
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((BusyOverlay)d).MessageText.Text = e.NewValue as string ?? string.Empty;
    }

    private void StartSpin()
    {
        var sb = (System.Windows.Media.Animation.Storyboard)Resources["Spin"];
        sb.Begin(this, true);
    }

    private void StopSpin()
    {
        var sb = (System.Windows.Media.Animation.Storyboard)Resources["Spin"];
        sb.Remove(this);
    }
}
