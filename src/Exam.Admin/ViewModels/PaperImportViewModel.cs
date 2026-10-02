using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 试题导入页：选择材料包目录 → **服务端预检**（校验、逐题编译、逐题选项定位，返回逐题摘要）→
/// 确认后**发布**（组包加密、版本资源入库、试卷版本发布）→ 发布后单独核对**客户端更新**。
///
/// 「发布成功」与「全部客户端已更新」是两件事：前者只说明服务器已切换试卷版本，学生端可能还没装上。
/// 本页把两者分开显示，不合并成一个「完成」。
///
/// 预检结论与**发布输入绑定**：换考试、换材料包、改目标版本或改编译目录都会作废上一次的预检结论，
/// 不允许拿旧包、旧版本的预检结论去发布新包、新版本。
/// </summary>
public sealed class PaperImportViewModel : BindableBase
{
    private const string NoExamOrPackageStatus =
        "请先选择当前考试与材料包目录。";
    private const string IdleStatus =
        "选择材料包目录后先预检：预检会真正编译每一题并定位每个选项，通过后才可发布。";

    private readonly AdminApiClient _api;
    private readonly IPaperImportFileDialogService _dialogs;
    private string _examId = string.Empty;
    private string _packageRoot = string.Empty;
    private string _compilationDirectory = string.Empty;
    private string _packageOutputDirectory = string.Empty;
    private string _reason = string.Empty;
    private string _declaredChangedQuestions = string.Empty;
    private string _targetVersionText = "1";
    private string _status = IdleStatus;
    private string _stage = string.Empty;
    private string _clientUpdateSummary = "尚未核对客户端更新。";
    private string _gradingRevisionSummary = "尚未修订答案与规则。";
    private bool _busy;
    private bool _previewPassed;

    public PaperImportViewModel(AdminApiClient api, IPaperImportFileDialogService? dialogs = null)
    {
        _api = api;
        _dialogs = dialogs ?? new PaperImportFileDialogService();
        SelectPackageCommand = new AsyncRelayCommand(SelectPackageAsync, onError: SetError);
        LoadTargetVersionCommand = new AsyncRelayCommand(LoadTargetVersionAsync, onError: SetError);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, onError: SetError);
        PublishCommand = new AsyncRelayCommand(PublishAsync, onError: SetError);
        RefreshClientUpdatesCommand = new AsyncRelayCommand(RefreshClientUpdatesAsync, onError: SetError);
        ReviseGradingRulesCommand = new AsyncRelayCommand(ReviseGradingRulesAsync, onError: SetError);
    }

    /// <summary>由外壳在切换当前考试时设置；预检、发布与客户端核对都以它为准。</summary>
    public string ExamId
    {
        get => _examId;
        set
        {
            if (!SetProperty(ref _examId, value))
            {
                return;
            }

            // 换考试就是换发布对象：上一次考试的预检结论、问题与发布结果一律不能沿用。
            PackageRoot = string.Empty;
            CompilationDirectory = string.Empty;
            PackageOutputDirectory = string.Empty;
            InvalidatePreview();
            ClearResults();
            Status = string.IsNullOrWhiteSpace(value) ? NoExamOrPackageStatus : IdleStatus;
            ClientUpdateSummary = "尚未核对客户端更新。";
            RaiseCommandStates();
        }
    }

    /// <summary>
    /// 本次要发布到的**目标**试卷版本（本次发布的产物即该版本）；服务器按「当前版本 = 目标版本 − 1」
    /// 判定并发冲突，版本对不上会整场拒绝发布。可用「读取当前版本」按服务器实况填入。
    ///
    /// 以**文本**接受输入并显式解析：文本框里的非法值（空、负数、小数、超范围）不会被静默取上一次的旧值，
    /// 而是明确拒绝并提示，避免拿一个操作者没填的版本去发布。
    /// </summary>
    public string TargetVersionText
    {
        get => _targetVersionText;
        set
        {
            if (SetProperty(ref _targetVersionText, value))
            {
                InvalidatePreview();
            }
        }
    }

    /// <summary>服务器本机可读的材料包目录（含 manifest.json、questions、styles 与 server-only）。</summary>
    public string PackageRoot
    {
        get => _packageRoot;
        private set
        {
            if (SetProperty(ref _packageRoot, value))
            {
                InvalidatePreview();
            }
        }
    }

    /// <summary>服务端编译产物目录（服务端本机路径）；变更会影响编译结果，故同样作废预检结论。</summary>
    public string CompilationDirectory
    {
        get => _compilationDirectory;
        set
        {
            if (SetProperty(ref _compilationDirectory, value))
            {
                InvalidatePreview();
            }
        }
    }

    /// <summary>服务端加密包输出目录（服务端本机路径）。</summary>
    public string PackageOutputDirectory
    {
        get => _packageOutputDirectory;
        set => SetProperty(ref _packageOutputDirectory, value);
    }

    /// <summary>本次发布的业务原因，随版本一起记录。它不改变发布对象，因此改动不作废预检结论。</summary>
    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }

    /// <summary>
    /// 本次变更的题号（开考后修订必填，用空格、逗号或顿号分隔）。
    /// 首次发布不需要填：服务器按整卷都是新题处理。
    /// 不改变将要发布的包本身，因此改动不作废预检结论；服务器会在发布时按声明逐题核对。
    /// </summary>
    public string DeclaredChangedQuestions
    {
        get => _declaredChangedQuestions;
        set => SetProperty(ref _declaredChangedQuestions, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>流水线停在哪个阶段；失败时据此说明问题出在哪一步。</summary>
    public string Stage
    {
        get => _stage;
        private set => SetProperty(ref _stage, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>预检是否通过；只有通过才允许发布。</summary>
    public bool PreviewPassed
    {
        get => _previewPassed;
        private set
        {
            if (SetProperty(ref _previewPassed, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>客户端更新情况；与发布结果分开显示。</summary>
    public string ClientUpdateSummary
    {
        get => _clientUpdateSummary;
        private set => SetProperty(ref _clientUpdateSummary, value);
    }

    /// <summary>
    /// 只改答案与计分规则的结果：**规则已发布**与**成绩已重算完**分开说，
    /// 不把「规则已换」说成「成绩已重算完」。
    /// </summary>
    public string GradingRevisionSummary
    {
        get => _gradingRevisionSummary;
        private set => SetProperty(ref _gradingRevisionSummary, value);
    }

    public bool CanPreview => !IsBusy;
    public bool CanPublish => PreviewPassed && !IsBusy;
    public bool CanReadCurrentVersion => !IsBusy && !string.IsNullOrWhiteSpace(ExamId);
    public bool CanRefreshClientUpdates => !IsBusy && !string.IsNullOrWhiteSpace(ExamId);
    public bool CanReviseGradingRules => !IsBusy && !string.IsNullOrWhiteSpace(ExamId) && !string.IsNullOrWhiteSpace(PackageRoot);

    /// <summary>预检的逐题摘要：是否编译成功、清单选项数与实际定位到的区域数。</summary>
    public ObservableCollection<ImportQuestionPreview> Questions { get; } = [];

    /// <summary>预检与发布发现的问题，逐条带稳定问题码。</summary>
    public ObservableCollection<ImportPipelineIssue> Issues { get; } = [];

    public AsyncRelayCommand SelectPackageCommand { get; }
    public AsyncRelayCommand LoadTargetVersionCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public AsyncRelayCommand RefreshClientUpdatesCommand { get; }
    public AsyncRelayCommand ReviseGradingRulesCommand { get; }

    public Task SelectPackageAsync()
    {
        var directory = _dialogs.OpenPackageDirectory();
        if (directory is null)
        {
            return Task.CompletedTask;
        }

        PackageRoot = directory;
        // 单机部署下考务端与服务器同机：由包目录派生的输出目录服务端可直接使用，规范化后不带 ".."。
        CompilationDirectory = Path.GetFullPath(Path.Combine(directory, "..", "_compile"));
        PackageOutputDirectory = Path.GetFullPath(Path.Combine(directory, "..", "_packages"));
        ClearResults();
        Status = "已选择材料包目录；请确认考试与目标版本后预检。";
        return Task.CompletedTask;
    }

    /// <summary>按服务器实况读取当前试卷版本，并把目标版本置为其下一版。</summary>
    public async Task LoadTargetVersionAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = NoExamOrPackageStatus;
            return;
        }

        IsBusy = true;
        try
        {
            var snapshot = await _api.GetExamAsync(ExamId);
            var current = snapshot.Versions.PaperVersion;
            TargetVersionText = (current + 1).ToString(CultureInfo.InvariantCulture);
            Status = current < 1
                ? $"该考试尚未发布过试卷；目标版本记为 {TargetVersionText}。"
                : $"该考试当前试卷版本为 {current}；目标版本记为 {TargetVersionText}。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task PreviewAsync()
    {
        if (!EnsureReady(forPublish: false, out var targetVersion))
        {
            return;
        }

        ClearResults();
        IsBusy = true;
        Status = "正在预检：校验清单与答案、逐题编译、逐题定位选项…";
        try
        {
            var result = await _api.PreviewPaperImportAsync(
                PackageRoot, ExamId, targetVersion, CompilationDirectory, Reason);
            Apply(result);
            Status = result.Succeeded
                ? $"预检通过：{result.Preview?.Count ?? 0} 题已编译并定位。确认无误后发布。"
                : $"预检未通过，停在 {result.Stage} 阶段；请按问题逐条处理后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task PublishAsync()
    {
        if (!EnsureReady(forPublish: true, out var targetVersion))
        {
            return;
        }

        ClearResults();
        IsBusy = true;
        Status = "正在发布：组包加密、版本资源入库、整体发布试卷版本与计分规则…";
        try
        {
            var result = await _api.PublishPaperImportAsync(
                PackageRoot, ExamId, targetVersion, CompilationDirectory, PackageOutputDirectory,
                Reason, ParseDeclaredChangedQuestionIds());
            Apply(result);
            if (!result.Succeeded)
            {
                Status = $"发布未完成，停在 {result.Stage} 阶段；请按问题逐条处理后重试。";
                return;
            }

            Status = $"发布成功：试卷版本 {result.PublishedPaperVersion} 已生效；学生端是否装上见客户端更新。";
        }
        finally
        {
            IsBusy = false;
        }

        // 发布成功**不**代表客户端已更新；这一步失败也不得覆盖上面的发布结果。
        await TryRefreshClientUpdatesAsync();
    }

    /// <summary>
    /// 发布后核对**客户端更新**：逐参试者是否已装到当前已发布的试卷版本。
    /// 数据取自考试版本快照（与设备监控页同一来源），不另立一套口径。
    /// </summary>
    public async Task RefreshClientUpdatesAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            ClientUpdateSummary = "请先选择当前考试。";
            return;
        }

        IsBusy = true;
        try
        {
            var snapshot = await _api.GetExamAsync(ExamId);
            ClientUpdateSummary = DescribeClientUpdates(snapshot);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 只改**答案与计分规则**：材料包里的清单与完整参考答案决定新规则，试卷版本不变，也不需要重新组包。
    /// 规则版本取服务器当前版本的下一版（不要求操作者记版本号）。
    /// </summary>
    public async Task ReviseGradingRulesAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId) || string.IsNullOrWhiteSpace(PackageRoot))
        {
            Status = NoExamOrPackageStatus;
            return;
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            Status = "请先填写本次发布原因。";
            return;
        }

        IsBusy = true;
        Status = "正在发布新的答案与计分规则，并按新版本规划重算…";
        try
        {
            var result = await _api.ReviseGradingRulesAsync(ExamId, PackageRoot, 0, Reason);
            if (!result.Succeeded)
            {
                Status = "规则修订未完成；请按问题逐条处理后重试。";
                foreach (var issue in result.Issues)
                {
                    Issues.Add(issue);
                }

                GradingRevisionSummary = $"规则修订未完成：{string.Join("；", result.Issues.Select(issue => $"{issue.Code} {issue.Message}"))}";
                return;
            }

            if (result.AlreadyInEffect)
            {
                // 内容没变就不推新版本：不把「已是该规则」说成「又发布了一次」。
                Status = $"材料与当前已发布规则内容一致，未发布新版本（当前规则版本 {result.PublishedGradingVersion}）。";
                GradingRevisionSummary = $"规则内容未变化，未推进规则版本；当前规则版本 {result.PublishedGradingVersion}。";
                return;
            }

            Status = $"答案与计分规则已发布到版本 {result.PublishedGradingVersion}（试卷版本不变）。";
            var readiness = result.Readiness?.RecalculationReady == true
                ? "就绪"
                : $"未就绪（{string.Join("；", result.Readiness?.Blockers ?? [])}）";
            GradingRevisionSummary =
                $"规则版本 {result.PublishedGradingVersion} 已发布：重算范围 {result.SubmissionCount} 份答卷，" +
                $"本次入队 {result.Enqueued} 份，重算{readiness}。" +
                (result.RecalculationError is null ? string.Empty : $" 重算未触发：{result.RecalculationError}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TryRefreshClientUpdatesAsync()
    {
        try
        {
            await RefreshClientUpdatesAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ClientUpdateSummary = $"客户端更新核对失败（发布结果不受影响）：{exception.Message}";
        }
    }

    internal static string DescribeClientUpdates(ExamVersionSnapshot snapshot)
    {
        var target = snapshot.Versions.PaperVersion;
        if (target < 1)
        {
            return "该考试尚未发布试卷版本，无法核对客户端更新。";
        }

        // 只统计仍在参试状态的人：已退出/取消的参试者不参与「全部客户端已更新」。
        var participants = (snapshot.Participants ?? new Dictionary<string, ParticipantVersionStatus>())
            .Values
            .Where(participant => participant is not null && participant.ExamState is
                ParticipationBusinessState.Waiting or ParticipationBusinessState.Answering
                or ParticipationBusinessState.Submitted)
            .ToList();
        var pending = participants
            .Where(participant => participant.InstalledPaperVersion < target)
            .ToList();
        if (participants.Count == 0)
        {
            return $"试卷版本 {target} 已发布，当前没有处于参试状态的参试者。";
        }

        if (pending.Count == 0)
        {
            return $"全部客户端已更新：{participants.Count} 名参试者均已装到版本 {target}。";
        }

        var detail = string.Join("、", pending.Take(5)
            .Select(participant => $"{participant.ParticipantId}（{DescribeUpdateState(participant.PaperUpdateState)}）"));
        return $"客户端尚未全部更新：已装到版本 {target} 的 {participants.Count - pending.Count}/{participants.Count} 名，" +
               $"仍有 {pending.Count} 名未更新：{detail}{(pending.Count > 5 ? " 等" : string.Empty)}。";
    }

    private static string DescribeUpdateState(PaperUpdateState state) => state switch
    {
        PaperUpdateState.NotReceived => "尚未收到",
        PaperUpdateState.DownloadFailed => "安装失败",
        PaperUpdateState.ReviewRequired => "待复核",
        PaperUpdateState.Updated => "已装",
        _ => state.ToString()
    };

    /// <summary>把界面里填写的变更题号切成题号列表；空表示未声明（首次发布不需要声明）。</summary>
    internal IReadOnlyList<string> ParseDeclaredChangedQuestionIds() =>
        _declaredChangedQuestions
            .Split(DeclaredQuestionSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>变更题号的分隔符：空格、逗号、顿号与分号的中英文写法都接受。</summary>
    private static readonly char[] DeclaredQuestionSeparators =
        [' ', '\t', '\r', '\n', ',', '，', '、', ';', '；'];


    private bool EnsureReady(bool forPublish, out long targetVersion)
    {
        targetVersion = 0;
        if (string.IsNullOrWhiteSpace(ExamId) || string.IsNullOrWhiteSpace(PackageRoot))
        {
            Status = NoExamOrPackageStatus;
            return false;
        }

        // 只接受纯十进制整数：空、负号、小数、千分位、超范围一律拒绝，不静默回退旧值。
        if (!long.TryParse(TargetVersionText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                out targetVersion) || targetVersion < 1)
        {
            Status = "目标试卷版本必须是不少于 1 的整数；可用「读取当前版本」按服务器实况填入。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(CompilationDirectory) || string.IsNullOrWhiteSpace(Reason))
        {
            Status = "请填写服务端编译输出目录与本次发布原因。";
            return false;
        }

        if (forPublish && !PreviewPassed)
        {
            Status = "请先完成预检；预检通过后才可发布。";
            return false;
        }

        return true;
    }

    private void Apply(ImportPipelineResult result)
    {
        Stage = result.Stage.ToString();
        foreach (var issue in result.Issues)
        {
            Issues.Add(issue);
        }

        foreach (var question in result.Preview ?? [])
        {
            Questions.Add(question);
        }

        // 预检结论只由流水线的成功与否决定：停在任何阶段都作废上一次的「已通过」，不保留假通过。
        PreviewPassed = result.Succeeded;
    }

    private void InvalidatePreview()
    {
        if (PreviewPassed)
        {
            PreviewPassed = false;
        }
    }

    private void ClearResults()
    {
        Issues.Clear();
        Questions.Clear();
        Stage = string.Empty;
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanPreview));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanReadCurrentVersion));
        OnPropertyChanged(nameof(CanRefreshClientUpdates));
        OnPropertyChanged(nameof(CanReviseGradingRules));
    }

    private void SetError(Exception exception) => Status = $"操作失败：{exception.Message}";
}
