using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Views;

namespace Exam.StudentUnitTests;

[Trait("Category", "Wpf")]
public sealed class StudentMouseWheelTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Synthetic input")]
    public Task LoginInput_HoveringTheEditorScrollsTheSurroundingPage(string text) => Sta(() =>
    {
        // Use the same component and theme as LoginPage without opening the real student data store.
        var input = new ValidatedInput { Label = "Synthetic input", Text = text, Width = 380 };
        var body = new StackPanel();
        body.Children.Add(new Border { Height = 200 });
        body.Children.Add(input);
        body.Children.Add(new Border { Height = 1000 });
        var page = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        WithWindow(page, () =>
        {
            page.ScrollToVerticalOffset(100); Drain();
            var editor = (TextBox)input.FindName("Input");
            foreach (var point in new[] { new Point(0.5, 0.5), new Point(0.05, 0.5), new Point(0.95, 0.5), new Point(0.5, 0.2), new Point(0.5, 0.8) })
            {
                var source = Assert.IsAssignableFrom<UIElement>(editor.InputHitTest(new Point(editor.ActualWidth * point.X, editor.ActualHeight * point.Y)));
                var before = page.VerticalOffset;
                Wheel(source, -120);
                Assert.True(page.VerticalOffset > before, "Hovering the student login input must not trap the wheel.");
                var down = page.VerticalOffset;
                Wheel(source, 120);
                Assert.True(page.VerticalOffset < down);
            }
            Assert.Equal(text, input.Text);
        });
    });

    [Fact]
    public Task QuestionView_WheelOverAnOptionScrollsWithoutSelectingIt() => Sta(() =>
    {
        var view = new PdfQuestionView();
        var pages = (StackPanel)view.FindName("PagesHost");
        var choice = new System.Windows.Controls.Primitives.ToggleButton { Content = "Synthetic option", Height = 60 };
        pages.Children.Add(choice);
        pages.Children.Add(new Border { Height = 1600, Width = 600, Background = Brushes.White });
        var page = (ScrollViewer)view.FindName("PdfScroll");
        WithWindow(view, () =>
        {
            Assert.True(page.ScrollableHeight > 0);
            var source = Assert.IsAssignableFrom<UIElement>(choice.InputHitTest(new Point(choice.ActualWidth / 2, choice.ActualHeight / 2)));
            Wheel(source, -120);
            Assert.True(page.VerticalOffset > 0);
            var down = page.VerticalOffset;
            Wheel(page, 120);
            Assert.True(page.VerticalOffset < down);
            Assert.False(choice.IsChecked);
        });
    });

    private static void WithWindow(UIElement content, Action assertion)
    {
        var window = new Window
        {
            Content = content, Width = 700, Height = 500, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None
        };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml")
        });
        try { window.Show(); Drain(); window.UpdateLayout(); assertion(); }
        finally { window.Close(); }
    }

    private static void Wheel(UIElement source, int delta)
    {
        var input = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent
        };
        source.RaiseEvent(input);
        if (!input.Handled)
        {
            input.RoutedEvent = Mouse.MouseWheelEvent;
            source.RaiseEvent(input);
        }
        Drain();
    }

    private static async Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
