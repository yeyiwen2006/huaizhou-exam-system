using System.IO;

using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Grading;
using Exam.Domain.Importing;

namespace Exam.Admin.ViewModels;

/// <summary>
/// 「只改答案与计分规则」入口：从**本地材料包**读清单与完整答案，复用既有规则发布端点提交
/// （不重新编译、不组包）。发布提交后由服务器内部及时安排重算，后台循环补偿。
///
/// 「发布成功」与「成绩重算完」是两件事，分两行显示：发布成功只说明规则版本已切换；
/// 重算中 / 结果就绪由计分状态读取给出。重算期间旧报表不可查询或导出（既有报表门）。
/// </summary>
public sealed class GradingRevisionViewModel : BindableBase
{
    private const string NoExamStatus = "请先在「全部考试」中打开一场考试，再改答案与计分规则。";

    private readonly AdminApiClient _api;
    private readonly IExamImportSourceDialog _dialog;
    private string _examId = string.Empty;
    private string _packagePath = string.Empty;
    private string _reason = string.Empty;
    private string _status = NoExamStatus;
    private string _publishedText = "尚未发布规则修订。";
    private string _recalculationText = "尚未核对重算状态。";
    private bool _busy;

    public GradingRevisionViewModel(AdminApiClient api, IExamImportSourceDialog? dialog = null)
    {
        _api = api;
        _dialog = dialog ?? new FileDialogService();
        SelectPackageCommand = new AsyncRelayCommand(SelectPackageAsync, onError: SetError);
        PublishCommand = new AsyncRelayCommand(PublishAsync, onError: SetError);
        RefreshStatusCommand = new AsyncRelayCommand(RefreshStatusAsync, onError: SetError);
    }

    /// <summary>由外壳在切换当前考试时设置；发布与状态读取都以它为准。</summary>
    public string ExamId
    {
        get => _examId;
        set
        {
            if (!SetProperty(ref _examId, value))
            {
                return;
            }

            // 换考试就是换发布对象：上一次的规则版本与状态结论都不能沿用。
            PackagePath = string.Empty;
            Status = string.IsNullOrWhiteSpace(value) ? NoExamStatus : "已选择考试；请选择材料包目录后发布规则修订。";
            PublishedText = "尚未发布规则修订。";
            RecalculationText = "尚未核对重算状态。";
            RaiseCommandStates();
        }
    }

    public string PackagePath
    {
        get => _packagePath;
        private set => SetProperty(ref _packagePath, value);
    }

    /// <summary>本次修改原因，随规则版本一起记录。</summary>
    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>发布结果：规则版本与试卷版本。与重算状态分开显示。</summary>
    public string PublishedText
    {
        get => _publishedText;
        private set => SetProperty(ref _publishedText, value);
    }

    /// <summary>重算状态：重算中 / 结果就绪（来自计分状态读取，每次都重读）。</summary>
    public string RecalculationText
    {
        get => _recalculationText;
        private set => SetProperty(ref _recalculationText, value);
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

    public bool CanPublish => !IsBusy && !string.IsNullOrWhiteSpace(ExamId);
    public bool CanRefreshStatus => !IsBusy && !string.IsNullOrWhiteSpace(ExamId);
    public bool CanSelectPackage => !IsBusy && !string.IsNullOrWhiteSpace(ExamId);

    public AsyncRelayCommand SelectPackageCommand { get; }
    public AsyncRelayCommand PublishCommand { get; }
    public AsyncRelayCommand RefreshStatusCommand { get; }

    public Task SelectPackageAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = NoExamStatus;
            return Task.CompletedTask;
        }

        var directory = _dialog.OpenDirectory();
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Task.CompletedTask;
        }

        PackagePath = Path.GetFullPath(directory);
        Status = "已选择材料包目录；发布前会重新校验清单与完整答案。";
        return Task.CompletedTask;
    }

    public async Task PublishAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            Status = NoExamStatus;
            return;
        }

        if (string.IsNullOrWhiteSpace(PackagePath))
        {
            Status = "请先选择材料包目录（含 manifest.json 与 server-only/answer-key.json）。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            Status = "请填写本次修改原因。";
            return;
        }

        var manifestPath = Path.Combine(PackagePath, "manifest.json");
        var answerPath = Path.Combine(PackagePath, "server-only", "answer-key.json");
        if (!File.Exists(manifestPath) || !File.Exists(answerPath))
        {
            Status = "材料包里找不到 manifest.json 或 server-only/answer-key.json；换规则必须带完整参考答案。";
            return;
        }

        IsBusy = true;
        Status = "正在提交规则修订（不重新编译、不组包）…";
        try
        {
            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var answerJson = await File.ReadAllTextAsync(answerPath);
            var validation = new ExamManifestValidator().ValidateJson(manifestJson, answerJson);
            if (!validation.IsValid)
            {
                Status = "清单或参考答案未通过校验：" +
                    string.Join("；", validation.Issues.Take(5).Select(issue => $"{issue.Rule}@{issue.Location}"));
                return;
            }

            if (validation.RequiresAnswerKeyArithmetic)
            {
                Status = "参考答案不完整：缺少计分算术所需的条目，不能改计分规则。";
                return;
            }

            var snapshot = await _api.GetExamAsync(ExamId);
            if (snapshot.Versions.PaperVersion < 1 || snapshot.Paper is null)
            {
                Status = "该考试尚未发布试卷版本；答案与计分规则必须对应一份已发布的试卷。";
                return;
            }

            var published = await _api.ReviseGradingRulesAsync(ExamId, manifestJson, answerJson,
                snapshot.Versions.GradingVersion, Reason);
            PublishedText = $"规则发布成功：规则版本 {published.Versions.GradingVersion}，" +
                $"试卷版本 {published.Versions.PaperVersion}（规则集 {published.Grading?.RuleSetId ?? "未记录"}）。" +
                "发布成功只说明服务器已切换规则版本，成绩是否重算完见下一行。";
            Status = "规则修订已提交；正在核对重算状态…";
        }
        finally
        {
            IsBusy = false;
        }

        // 发布已经成功：状态核对失败也不得改写上面的发布结果。
        await TryRefreshStatusAsync();
    }

    public async Task RefreshStatusAsync()
    {
        if (string.IsNullOrWhiteSpace(ExamId))
        {
            RecalculationText = "尚未核对重算状态。";
            return;
        }

        IsBusy = true;
        try
        {
            RecalculationText = Describe(await _api.GetGradingStatusAsync(ExamId));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TryRefreshStatusAsync()
    {
        try
        {
            await RefreshStatusAsync();
            Status = "规则修订已提交；重算状态见下方。重算期间旧报表不可查询或导出。";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecalculationText = $"重算状态核对失败（发布结果不受影响）：{exception.Message}";
        }
    }

    /// <summary>把计分状态翻成「重算中 / 结果就绪」，带上范围与阻断原因。</summary>
    internal static string Describe(GradingStatusReport report)
    {
        if (report.CurrentGradingVersion is not { } version)
        {
            return "尚未发布任何计分规则：没有可核对的结果版本。";
        }

        if (report.Readiness is not { } readiness)
        {
            return $"已发布规则版本 {version}，但本次读取没有就绪结论。";
        }

        var scope = $"范围 {report.FinalSubmissionCount} 份最终答卷，已按版本 {version} 计分 {report.GradedAtCurrentVersion} 份。";
        if (readiness.RecalculationReady)
        {
            return $"结果就绪：规则版本 {version}，{scope}";
        }

        var blockers = readiness.Blockers.Count == 0 ? string.Empty : " 阻断：" + string.Join("；", readiness.Blockers);
        return $"重算中：规则版本 {version}，{scope} 未完成 {readiness.Outstanding.Count} 份。{blockers}".TrimEnd();
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(CanRefreshStatus));
        OnPropertyChanged(nameof(CanSelectPackage));
    }

    private void SetError(Exception exception) => Status = $"操作失败：{exception.Message}";
}
