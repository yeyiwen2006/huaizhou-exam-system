using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Exam.Desktop.Shared.Controls;

namespace Exam.StudentUnitTests;

[Trait("Category", "Wpf")]
public sealed class BannerLayoutTests
{
    [Theory]
    [InlineData("login-error", "姓名、班级或准考证号与考生名单不一致，请核对后重新填写。")]
    [InlineData("entry-time", "当前不可进入该考试。最早可进入时间：2026-10-15 13:55（开考前20分钟）。")]
    public async Task LongMessagesWrapInsideLoginCard(string name, string message)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var banner = new Banner { Width = 382 };
                banner.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml")
                });
                banner.Show(message);
                banner.Measure(new Size(382, double.PositiveInfinity));
                banner.Arrange(new Rect(banner.DesiredSize));
                banner.UpdateLayout();

                var text = (TextBlock)banner.FindName("MessageText");
                var singleLine = new TextBlock { Text = message, FontFamily = text.FontFamily, FontSize = text.FontSize };
                singleLine.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var bounds = text.TransformToAncestor(banner).TransformBounds(new Rect(text.RenderSize));
                Assert.Equal(message, text.Text);
                Assert.True(bounds.Right <= banner.ActualWidth, "提示文字不能超出提示框右侧。");
                Assert.True(bounds.Bottom <= banner.ActualHeight, "提示框必须随换行后的文字增高。");
                Assert.True(text.ActualHeight > singleLine.DesiredSize.Height * 1.5, "长提示应显示为多行。");

                if (Environment.GetEnvironmentVariable("EXAM_UI_SCREENSHOT_DIRECTORY") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(banner.ActualWidth),
                        (int)Math.Ceiling(banner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(banner);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, name + ".png"));
                    encoder.Save(file);
                }
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
