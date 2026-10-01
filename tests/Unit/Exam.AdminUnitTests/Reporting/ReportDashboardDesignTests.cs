using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Exam.Admin.Reporting;
using Exam.Contracts.Grading;
using Exam.Contracts.Reporting;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;

namespace Exam.AdminUnitTests.Reporting;

public sealed partial class ReportViewReviewTests
{
    [Fact]
    public async Task Design_SummaryUsesAuthoritativeStatusAndClearsOnIdentityChange() => await OnUiThreadAsync(async () =>
    {
        using var handler = new PageHandler(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal)
            ? PageResponse(ReadyStatus()) : PageResponse(Published(ReadyStatus()))));
        using var api = PageApi(handler);
        using var page = Page(api);
        Assert.Equal("—", page.ParticipantCountText);
        await page.RefreshAsync();
        Assert.Equal("1", page.ParticipantCountText);
        Assert.Equal("1", page.CollectedCountText);
        Assert.Equal("1", page.GradedCountText);
        Assert.Equal("第 1 版", page.GradingVersionText);
        Assert.Equal("成绩已生成", page.StateLabel);
        api.Disconnect();
        Assert.Equal("—", page.ParticipantCountText);
        Assert.Equal("—", page.CollectedCountText);
        Assert.Equal("—", page.GradedCountText);
        Assert.Equal("—", page.GradingVersionText);
        Assert.NotEqual("成绩已生成", page.StateLabel);
        Assert.False(page.Session.HasTables);
    });

    [Theory]
    [InlineData(1d)]
    [InlineData(1.25d)]
    [InlineData(1.5d)]
    public async Task Design_DashboardFitsAtEachScaleAndKeepsActionsAndFooterVisible(double scale) => await OnUiThreadAsync(async () =>
    {
        var result = DashboardResult();
        var status = ReadyStatus() with { ParticipantCount = result.StudentTotals.Count, CollectedCount = result.StudentTotals.Count,
            GradedCount = result.StudentTotals.Count, Reason = "报表已更新。统计仅使用服务器已核对的当前版本。" };
        var published = new PublishedReport(1, status, JsonSerializer.SerializeToElement(result, ReportJson.Create()));
        var denied = false;
        using var handler = new PageHandler(request => Task.FromResult(denied ? new System.Net.Http.HttpResponseMessage(HttpStatusCode.Unauthorized)
            : request.RequestUri!.AbsolutePath.EndsWith("/status", StringComparison.Ordinal) ? PageResponse(status) : PageResponse(published)));
        using var api = PageApi(handler);
        using var page = Page(api);
        var view = new ReportPageView { DataContext = page };
        view.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        var window = new Window { Content = view, Width = 1366 / scale, Height = 768 / scale,
            Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
        try
        {
            window.Show(); await page.RefreshAsync(); Drain(); window.UpdateLayout();
            var export = (Button)view.FindName("ExportButton");
            var table = Descendants<ReportTableView>(view).Single();
            var footer = (Grid)table.FindName("FooterGrid");
            var rows = (DataGrid)table.FindName("RowsGrid");
            Assert.True(export.IsEnabled);
            AssertWithin(view, export);
            AssertWithin(view, (FrameworkElement)view.FindName("SummaryCards"));
            Assert.True(rows.ActualHeight >= 100, $"The report viewport is too short: {rows.ActualHeight}.");
            var footerBottom = footer.TransformToAncestor(view).Transform(new Point(0, footer.ActualHeight)).Y;
            Assert.True(footerBottom <= view.ActualHeight + 1, "The fixed average row must remain in the viewport.");
            SaveDashboardScreenshot(view, scale, "ready");
            page.Session.SearchText = "合成考生 07";
            page.Session.SearchCommand.Execute(null);
            Drain(); window.UpdateLayout();
            Assert.Single(rows.Items);
            Assert.True(footer.Children.Count > 0, "查询时仍须显示固定平均分行。");
            var searchFooterBottom = footer.TransformToAncestor(view).Transform(new Point(0, footer.ActualHeight)).Y;
            Assert.True(searchFooterBottom <= view.ActualHeight + 1, "查询时固定平均分行仍须在可视区域内。");
            SaveDashboardScreenshot(view, scale, "search");
            page.Session.ClearSearchCommand.Execute(null);
            Drain(); window.UpdateLayout();
            Assert.Equal(24, rows.Items.Count);
            Assert.True(page.Session.TrySelect(ReportTableKind.SubjectAnswers, "design-subject-1"));
            Drain(); window.UpdateLayout();
            SaveDashboardScreenshot(view, scale, "answers");
            denied = true; await page.PollAsync(); Drain(); window.UpdateLayout();
            Assert.False(export.IsEnabled);
            Assert.Empty(rows.Items);
            Assert.Empty(footer.Children);
            Assert.Equal("—", page.ParticipantCountText);
            SaveDashboardScreenshot(view, scale, "unavailable");
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task Design_ShortTablesFillCardAndKeepFooterAlignedAfterResize() => await OnUiThread(() =>
    {
        var model = ReportTableProjection.Build(ReportTableSessionTests.Result(1))[0];
        var view = new ReportTableView { Table = model };
        var window = WindowFor(view, 1300, 650);
        try
        {
            window.Show(); Drain(); window.UpdateLayout();
            foreach (var width in new[] { 1300d, 1000d, 850d, 1300d })
            {
                window.Width = width; Drain(); window.UpdateLayout();
                var scroller = (ScrollViewer)view.FindName("HorizontalScroller");
                var body = (Grid)view.FindName("TableBody");
                var rows = (DataGrid)view.FindName("RowsGrid");
                var footer = (Grid)view.FindName("FooterGrid");
                Assert.True(body.Width >= scroller.ActualWidth - 1);
                for (var index = 0; index < model.Columns.Count; index++)
                {
                    Assert.True(rows.Columns[index].Width.Value >= model.Columns[index].Width);
                    Assert.Equal(rows.Columns[index].Width.Value, footer.ColumnDefinitions[index].Width.Value, 3);
                }
            }
        }
        finally { window.Close(); }
    });

    private static void AssertWithin(FrameworkElement view, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));
        Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= view.ActualWidth + 1 &&
            bounds.Bottom <= view.ActualHeight + 1, $"{element.Name} is outside its viewport: {bounds}.");
    }

    private static void SaveDashboardScreenshot(FrameworkElement view, double scale, string state)
    {
        var directory = Environment.GetEnvironmentVariable("EXAM_REPORT_SCREENSHOT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth * scale), (int)Math.Ceiling(view.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, $"report-dashboard-{state}-{(int)(scale * 100)}.png"));
        encoder.Save(file);
    }

    private static ReportCalculationResult DashboardResult()
    {
        static ExactRational R(int value) => ExactRational.FromInteger(value);
        var subjects = Enumerable.Range(1, 3).Select(number => new ReportSubject($"design-subject-{number}",
            $"合成科目{number}", number, R(30), Enumerable.Range(1, 3)
                .Select(question => new ReportQuestion($"design-q-{number}-{question}", (number - 1) * 3 + question, R(10))).ToArray())).ToArray();
        var participants = Enumerable.Range(1, 24).Select(number => new ReportParticipant($"design-candidate-{number}",
            number.ToString("000000"), $"合成考生 {number:00}", ((number - 1) % 3 + 1).ToString("00"))).ToArray();
        var submissions = participants.Select((participant, index) =>
        {
            var grades = subjects.Select((subject, subjectIndex) =>
            {
                var questions = subject.Questions.Select((question, questionIndex) =>
                {
                    var correct = (index + subjectIndex + questionIndex) % 5 != 0;
                    return new ReportQuestionGrade(question.QuestionId, correct ? "A" : "B",
                        correct ? AnswerOutcome.Correct : AnswerOutcome.Wrong, R(correct ? 10 : 0));
                }).ToArray();
                return new ReportSubjectGrade(subject.SubjectId, questions.Aggregate(R(0), (sum, question) => sum + question.Score), questions);
            }).ToArray();
            return new ReportSubmission($"design-submission-{index}", "synthetic-exam", participant.CandidateId, 1,
                grades, grades.Aggregate(R(0), (sum, subject) => sum + subject.FinalScore));
        }).ToArray();
        return ReportCalculator.Calculate(new("synthetic-exam", 1, subjects, participants, submissions));
    }
}
