using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 选择考试页：显示个人登录状态与报名考试列表；右上角红色“退出系统”。
/// 进入考试结果：同意→等待页；ENTRY_NOT_ALLOWED→“当前不可进入该考试！”；
/// RESOURCES_NOT_READY→“试题获取失败！”；其余错误显示服务器中文说明。
/// </summary>
public partial class ExamSelectionPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private readonly ObservableCollection<ExamRow> _rows = new();
    private bool _requesting;

    public ExamSelectionPage(MainWindow.Navigation navigation)
    {
        _navigation = navigation;
        InitializeComponent();
        DataContext = App.Services.Session;
        ExamList.ItemsSource = _rows;

        var candidate = App.Services.Session.Candidate;
        CandidateText.Text = candidate?.Display ?? "—";
        LoginPill.Text = candidate is null ? "未登录" : "已登录";
        LoginPill.Level = candidate is null ? StatusLevel.Neutral : StatusLevel.Success;

        Loaded += async (_, _) => await LoadExamsAsync();
    }

    private async Task LoadExamsAsync()
    {
        if (_requesting)
        {
            return;
        }

        _requesting = true;
        Busy.Message = "正在获取考试列表…";
        Busy.IsBusy = true;
        try
        {
            if (App.Services.Session.LoginResult is not { } session)
            {
                _navigation.GoToLogin();
                return;
            }

            var overviews = await App.Services.Api.GetExamOverviewsAsync(session.SessionId);
            var enrolled = session.ExamIds;
            _rows.Clear();
            foreach (var exam in overviews)
            {
                if (!enrolled.Contains(exam.ExamId))
                {
                    continue;
                }

                _rows.Add(new ExamRow(exam));
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            EntryBanner.Show("考试列表获取失败，请检查连接后重试。");
        }
        finally
        {
            Busy.IsBusy = false;
            _requesting = false;
        }
    }

    private void OnEnterExam(object sender, RoutedEventArgs e)
    {
        if (_requesting || sender is not Button { Tag: string examId })
        {
            return;
        }

        if (App.Services.Session.LoginResult is not { } session)
        {
            return;
        }

        if (App.Services.Session.HasSubmitted(examId))
        {
            EntryBanner.Show("当前不可进入该考试！");
            return;
        }

        var overview = _rows.FirstOrDefault(r => r.ExamId == examId)?.Overview;
        if (overview is null)
        {
            EntryBanner.Show("考试信息已变化，请重新获取考试列表。");
            return;
        }

        if (App.Services.Session.ActiveExam is { } runtime && runtime.Manager.ExamId != examId)
        {
            EntryBanner.Show("原考试尚未处理完成，请先恢复原考试。");
            return;
        }

        // 进入等待页后才下载、展示说明及确认参试；选择页不重复创建准备请求。
        _navigation.GoToWaitingExam(overview);
    }

    private async void OnExitSystem(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(
            window,
            "退出系统",
            "退出后下次需重新登录。确定退出系统吗？",
            "退出",
            "取消",
            isDanger: true);
        if (!confirmed)
        {
            return;
        }

        await App.Services.Session.ReleaseExamAsync();
        App.Services.Session.Logout();
        if (window is MainWindow main) main.PrepareForApplicationExit();
        Application.Current.Shutdown();
    }

    /// <summary>考试行展示模型。</summary>
    public sealed class ExamRow
    {
        public ExamRow(ExamOverview exam)
        {
            Overview = exam;
            ExamId = exam.ExamId;
            Name = exam.Name;
            CategoryText = string.IsNullOrWhiteSpace(exam.CategoryName) ? "未设置类别" : $"类别：{exam.CategoryName}";
            TimeText = $"{exam.ScheduledStart:yyyy-MM-dd HH:mm} — {exam.ScheduledEnd:HH:mm}";
            SubjectsText = exam.Subjects.Count == 0
                ? string.Empty
                : $"科目：{string.Join("、", exam.Subjects)}";
            (StateText, StateLevel) = exam.Lifecycle switch
            {
                ExamLifecycle.Running => ("进行中", StatusLevel.Success),
                ExamLifecycle.Ended => ("已结束", StatusLevel.Neutral),
                _ => ("未开始", StatusLevel.Info),
            };
        }

        public ExamOverview Overview { get; }

        public string ExamId { get; }

        public string Name { get; }

        public string StateText { get; }

        public StatusLevel StateLevel { get; }

        public string TimeText { get; }

        public string SubjectsText { get; }

        public string CategoryText { get; }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadExamsAsync();
}
