using System.Collections.ObjectModel;
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
        Busy.Message = "正在获取考试列表…";
        Busy.IsBusy = true;
        try
        {
            var overviews = await App.Services.Api.GetExamOverviewsAsync();
            var enrolled = App.Services.Session.LoginResult?.ExamIds;
            _rows.Clear();
            foreach (var exam in overviews)
            {
                if (enrolled is not null && !enrolled.Contains(exam.ExamId))
                {
                    continue;
                }

                _rows.Add(new ExamRow(exam));
            }
        }
        finally
        {
            Busy.IsBusy = false;
        }
    }

    private async void OnEnterExam(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string examId })
        {
            return;
        }

        if (App.Services.Session.LoginResult is not { } session)
        {
            return;
        }

        EntryBanner.Hide();
        Busy.Message = "正在请求进入考试…";
        Busy.IsBusy = true;
        ApiEnvelope<EntryPreparationResult> envelope;
        try
        {
            var request = new PrepareExamEntryRequest(RequestIds.New(), session.SessionId, examId);
            envelope = await App.Services.Api.PrepareEntryAsync(request);
        }
        finally
        {
            Busy.IsBusy = false;
        }

        if (!envelope.IsSuccess)
        {
            EntryBanner.Show(envelope.Error!.Code switch
            {
                ParticipationErrorCodes.EntryNotAllowed => "当前不可进入该考试！",
                ParticipationErrorCodes.ResourcesNotReady => envelope.Error.Message,
                _ => envelope.Error.Message,
            });
            return;
        }

        var overview = _rows.FirstOrDefault(r => r.ExamId == examId)?.Overview
            ?? new ExamOverview(examId, examId, DateTimeOffset.Now, DateTimeOffset.Now.AddHours(2),
                Array.Empty<string>(), ExamLifecycle.Scheduled);
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

        App.Services.Session.Logout();
        _navigation.GoToLogin();
    }

    /// <summary>考试行展示模型。</summary>
    public sealed class ExamRow
    {
        public ExamRow(ExamOverview exam)
        {
            Overview = exam;
            ExamId = exam.ExamId;
            Name = exam.Name;
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
    }
}
