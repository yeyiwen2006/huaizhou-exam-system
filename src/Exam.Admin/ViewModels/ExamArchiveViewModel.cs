using System.IO;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Archives;

namespace Exam.Admin.ViewModels;

public sealed partial class ExamHomeViewModel
{
    private readonly IExamArchiveService _archives;
    private readonly IExamArchiveDirectoryDialog _archiveDirectories;
    private CancellationTokenSource? _archiveCancellation;
    private bool _archiveBusy, _archiveDisposed;
    private string _archiveStatus = string.Empty;

    public Func<Task<AddExamChoice?>> ChooseAddExamAsync { get; set; } = () => Task.FromResult<AddExamChoice?>(null);
    public Func<ExamArchivePreview, Task<ArchiveImportDecision?>> ChooseArchiveConflictAsync { get; set; } =
        _ => Task.FromResult<ArchiveImportDecision?>(null);
    public AsyncRelayCommand AddExamCommand { get; }
    public RelayCommand CancelArchiveCommand { get; }
    public event Action<string>? ExamReplacedFromArchive;
    public bool IsArchiveBusy
    {
        get => _archiveBusy;
        private set
        {
            if (!SetProperty(ref _archiveBusy, value)) return;
            AddExamCommand.RaiseCanExecuteChanged(); CancelArchiveCommand.RaiseCanExecuteChanged();
            foreach (var card in _cards) card.ExportArchiveCommand.RaiseCanExecuteChanged();
        }
    }
    public string ArchiveStatus
    {
        get => _archiveStatus;
        private set { if (SetProperty(ref _archiveStatus, value)) OnPropertyChanged(nameof(HasArchiveStatus)); }
    }
    public bool HasArchiveStatus => ArchiveStatus.Length > 0;

    public async Task AddExamAsync()
    {
        if (!CanStartArchive()) return;
        var generation = _api.Generation;
        IsArchiveBusy = true;
        AddExamChoice? choice;
        try { choice = await ChooseAddExamAsync(); }
        finally { if (ArchiveSessionCurrent(generation)) IsArchiveBusy = false; }
        if (!ArchiveSessionCurrent(generation)) return;
        if (choice == AddExamChoice.New) NewExamRequested?.Invoke();
        else if (choice == AddExamChoice.Import) await ImportArchiveAsync();
    }

    public async Task ExportArchiveAsync(ExamCardViewModel card)
    {
        if (!CanStartArchive()) return;
        var generation = _api.Generation;
        using var cancellation = BeginArchive();
        try
        {
            var directory = _archiveDirectories.ChooseExportDirectory();
            if (directory is null || !ArchiveSessionCurrent(generation)) return;
            ArchiveStatus = $"正在导出“{card.Name}”的全部考试数据…";
            var completed = await _archives.ExportAsync(card.Exam.ExamId, card.Name, directory, cancellation.Token);
            if (!ArchiveSessionCurrent(generation)) return;
            ArchiveStatus = $"“{card.Name}”已导出到：{completed}。归档保存读取时刻的数据，源考试可继续作答。";
        }
        catch (OperationCanceledException)
        {
            if (ArchiveSessionCurrent(generation)) ArchiveStatus = "导出已取消，未完成的本地归档已清理。";
        }
        catch (Exception error)
        {
            if (ArchiveSessionCurrent(generation)) ArchiveStatus = $"考试数据导出未完成：{FriendlyErrors.Describe(error)}";
        }
        finally { EndArchive(cancellation, generation); }
    }

    public async Task ImportArchiveAsync()
    {
        if (!CanStartArchive()) return;
        var generation = _api.Generation;
        using var cancellation = BeginArchive();
        var submitted = false;
        try
        {
            var directory = _archiveDirectories.ChooseImportDirectory();
            if (directory is null || !ArchiveSessionCurrent(generation)) return;
            ArchiveStatus = "正在读取考试归档并核对目标服务器上的考试…";
            using var source = await _archives.OpenImportAsync(directory, cancellation.Token);
            if (!ArchiveSessionCurrent(generation)) return;
            var preview = source.Preview;
            if (preview.Conflicts is null) throw new InvalidDataException("考试归档预览缺少同场考试信息。");
            var decision = preview.Conflicts.Count == 0 ? new ArchiveImportDecision(ArchiveImportChoice.Copy)
                : await ChooseArchiveConflictAsync(preview);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ArchiveSessionCurrent(generation)) return;
            if (decision is null) { ArchiveStatus = "已取消导入，目标服务器上的考试未作修改。"; return; }
            if (decision.Choice == ArchiveImportChoice.Skip)
            { ArchiveStatus = $"已跳过“{preview.ExamName}”的导入。"; return; }
            if (decision.Choice == ArchiveImportChoice.Overwrite && (decision.Target is not { } target ||
                !preview.Conflicts.Any(c => c.ExamId == target.ExamId && c.Version == target.Version)))
                throw new InvalidDataException("覆盖目标不属于本次预览，请重新选择归档目录。");
            ArchiveStatus = decision.Choice == ArchiveImportChoice.Overwrite
                ? $"正在用归档替换“{decision.Target!.ExamName}”的全部本场数据…"
                : $"正在将“{preview.ExamName}”作为新副本导入…";
            submitted = true;
            var result = await source.ImportAsync(decision, Guid.NewGuid().ToString("N"), cancellation.Token);
            if (!ArchiveSessionCurrent(generation)) return;
            if (result.Skipped) { ArchiveStatus = $"已跳过“{preview.ExamName}”的导入。"; return; }
            if (!result.Imported || string.IsNullOrWhiteSpace(result.ExamId)) throw new InvalidDataException("服务器未确认导入完成。");
            if (decision.Choice == ArchiveImportChoice.Overwrite && result.ExamId != decision.Target!.ExamId)
                throw new InvalidDataException("导入回执与选定的目标考试不一致。");
            if (decision.Choice == ArchiveImportChoice.Overwrite) ExamReplacedFromArchive?.Invoke(result.ExamId);
            ArchiveStatus = $"“{result.ExamName}”已导入，可以进入考试继续使用。";
            try
            {
                await RefreshAsync();
                if (ArchiveSessionCurrent(generation) && _cards.FirstOrDefault(c => c.Exam.ExamId == result.ExamId) is { } imported)
                    ExamOpened?.Invoke(imported.Exam);
            }
            catch
            {
                if (ArchiveSessionCurrent(generation)) ArchiveStatus = $"“{result.ExamName}”已导入，请刷新考试列表查看。";
            }
        }
        catch (OperationCanceledException)
        {
            if (ArchiveSessionCurrent(generation)) ArchiveStatus = submitted
                ? "已停止等待导入结果，请刷新考试列表核对；已提交的导入不会因此撤销。"
                : "已取消导入，目标服务器上的考试未作修改。";
        }
        catch (Exception error)
        {
            if (ArchiveSessionCurrent(generation)) ArchiveStatus = submitted
                ? $"考试数据导入结果未确认：{FriendlyErrors.Describe(error)}；请刷新考试列表核对。"
                : error is FileNotFoundException ? "所选目录没有 exam.archive 文件，请选择完整导出的考试数据目录。"
                : $"考试归档读取未完成：{FriendlyErrors.Describe(error)}";
        }
        finally { EndArchive(cancellation, generation); }
    }

    private bool CanStartArchive() => !_archiveDisposed && _api.IsConfigured && !IsArchiveBusy;
    private bool ArchiveSessionCurrent(long generation) => !_archiveDisposed && _api.IsConfigured && generation == _api.Generation;
    private CancellationTokenSource BeginArchive()
    {
        var cancellation = _archiveCancellation = new();
        IsArchiveBusy = true;
        return cancellation;
    }
    private void EndArchive(CancellationTokenSource cancellation, long generation)
    {
        if (!ReferenceEquals(_archiveCancellation, cancellation)) return;
        _archiveCancellation = null;
        if (ArchiveSessionCurrent(generation)) IsArchiveBusy = false;
    }
    private void ResetArchiveSession()
    {
        _archiveCancellation?.Cancel(); _archiveCancellation = null;
        ArchiveStatus = string.Empty; IsArchiveBusy = false;
        AddExamCommand.RaiseCanExecuteChanged(); CancelArchiveCommand.RaiseCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_archiveDisposed) return;
        _archiveDisposed = true; ResetArchiveSession();
        _api.ConfigurationChanged -= OnApiConfigurationChanged;
    }
}
