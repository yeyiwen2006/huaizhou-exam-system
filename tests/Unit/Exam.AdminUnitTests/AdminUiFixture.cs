using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Threading;
using Exam.Admin;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Operators;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

/// <summary>Actual WPF navigation against an in-process, synthetic-only HTTP transport. No production authentication is bypassed.</summary>
internal sealed class AdminUiFixture : IDisposable
{
    public const string ExamId = "synthetic-exam";
    private readonly AdminApiClient _api;
    public MainViewModel ViewModel { get; }
    public MainWindow Window { get; }
    public static readonly DateTimeOffset Start = new(2027, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));
    public static readonly ExamSummary Summary = new(ExamId, "合成模拟考试", Start, Start.AddHours(2),
        ExamLifecycle.Scheduled, true, false, 0, 0, 1);
    private static readonly OperatorAccount Account = new("synthetic-admin", "合成管理员", OperatorStatus.Active, true, Start.AddDays(-1), null);

    public AdminUiFixture(App application)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        // Verify that the real StartupUri created the expected production window, then inject only its HTTP dependency.
        var startupWindow = Assert.IsType<MainWindow>(application.MainWindow);
        _api = new AdminApiClient(new Handler());
        ViewModel = new MainViewModel(_api);
        Window = new MainWindow(ViewModel) { ShowInTaskbar = false, Left = -4000, Top = -4000, Width = 1440, Height = 900 };
        application.MainWindow = Window;
        startupWindow.Close();
        Window.Show();
        Drain();
        Window.UpdateLayout();
    }

    public void Login()
    {
        ViewModel.Connection.ServerAddress = "https://synthetic-server.invalid/";
        ViewModel.Connection.Username = Account.Username;
        ViewModel.Connection.Password = "synthetic-test-only-password";
        ViewModel.Connection.ConnectCommand.Execute(null);
        Until(() => ViewModel.Connection.IsConnected, () => ViewModel.Connection.Status);
        Assert.False(ViewModel.Connection.IsLoggedOut);
    }

    public void OpenExam()
    {
        ViewModel.OpenExam(Summary);
        Until(() => ViewModel.Exam.CanSave && ViewModel.Roster.LoadedVersion.HasValue,
            () => ViewModel.Exam.Status + " / " + ViewModel.Roster.Status);
        Assert.Equal(ExamId, ViewModel.Exam.ExamId);
        Assert.NotEmpty(ViewModel.Exam.Subjects);
    }

    public static void Until(Func<bool> ready, Func<string>? failure = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!ready() && stopwatch.Elapsed < TimeSpan.FromSeconds(8)) { Drain(); Thread.Sleep(2); }
        Assert.True(ready(), failure?.Invoke() ?? "Synthetic UI operation did not complete.");
        Drain();
    }

    public static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    public static ExamDetail Detail() => new(Summary, new ExamSettings([
        new ExamSubjectSettings("subject-a", "合成科目A", "20", [new ExamGroupSettings("group-a", "一、单项选择题", "single", 1, 10, "20", false,
            new ManifestOneCorrect("2", "0", "0"), null)])]), 1, ["合成科目A（满分20分），共10小题，每题2分。"]);

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/health/ready") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            if (path == "/api/v1/operators/login") return Reply(new OperatorSession("synthetic-ui-session", Account));
            if (path == "/api/v1/operators/me") return Reply(Account);
            if (path == "/api/v1/operators") return Reply(new[] { Account,
                new OperatorAccount("synthetic-pending", "合成待审批人员", OperatorStatus.Pending, false, Start.AddHours(-1), null) });
            if (path == "/api/v1/exams") return Reply(new[] { Summary,
                Summary with { ExamId = "synthetic-second", Name = "合成第二场考试", StartsAt = Start.AddDays(1), EndsAt = Start.AddDays(1).AddHours(2) } });
            if (path == "/api/v1/exams/" + ExamId) return Reply(Detail());
            if (path.EndsWith("/roster", StringComparison.Ordinal)) return Reply(new CandidateRosterSnapshot(ExamId, 1,
                [new("synthetic-person-1", "合成考生甲", "001", "000001"), new("synthetic-person-2", "合成考生乙", "002", "000002")]));
            if (path == "/api/v1/rooms") return Reply(new[] { new RoomGroup("synthetic-room-a", "合成试场A", 1, 2), new RoomGroup("synthetic-room-b", "合成试场B", 1, 0) });
            if (path == "/api/v1/devices") return Reply(new DeviceDirectoryPage([
                new("synthetic-device-1", "20261002001", "synthetic-room-a", "合成试场A", ActivationStatus.Active,
                    DeviceConnectionState.Online, "合成考生甲", "001", "000001", ExamId, Summary.Name, "等待考试", null, Start.AddMinutes(-1), 1),
                new("synthetic-device-2", "20261002002", "synthetic-room-a", "合成试场A", ActivationStatus.Active,
                    DeviceConnectionState.Offline, null, null, null, null, null, "登录", null, Start.AddMinutes(-3), 1)], null, Start));
            if (path.EndsWith("/room-history", StringComparison.Ordinal)) return Reply(Array.Empty<DeviceRoomHistory>());
            if (path.Contains("activation", StringComparison.Ordinal) && request.Method == HttpMethod.Get) return Reply(Array.Empty<ActivationRequestResult>());
            if (path.EndsWith("/actual", StringComparison.Ordinal)) return Reply(Array.Empty<ParticipationResult>());
            if (path.EndsWith("/focus-events", StringComparison.Ordinal)) return Reply(Array.Empty<FocusLossEventSummary>());
            if (path.Contains("/submissions/collection/events", StringComparison.Ordinal)) return Reply(Array.Empty<CollectionCompletedNotification>());
            if (path == "/api/v1/versioning/exams/" + ExamId) return Reply(new ExamVersionSnapshot(ExamId, new(1, 1, 1),
                new(Start, Start.AddHours(2)), ExamLifecycle.Scheduled, null, null, null, null, new Dictionary<string, ParticipantVersionStatus>()));
            if (path.EndsWith("/monitoring", StringComparison.Ordinal)) return Reply(new MonitoringSnapshot(ExamId, Start, new(1, 1, 1),
                ExamLifecycle.Scheduled, 0, 0, 0, [], []));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new ApiEnvelope<object>("synthetic-server", "synthetic-node", 1, "ui-fixture", null,
                    new ApiError("SYNTHETIC_NOT_AVAILABLE", "此合成场景没有对应业务记录。")), options: JsonDefaults.Create())
            });
        }
        private static Task<HttpResponseMessage> Reply<T>(T result) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "ui-fixture", result, null), options: JsonDefaults.Create()) });
    }

    public void Dispose() { Window.Close(); _api.Dispose(); }
}
