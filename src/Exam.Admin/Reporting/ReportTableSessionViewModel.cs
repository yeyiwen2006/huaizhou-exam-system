using System.Windows.Threading;
using Exam.Admin.Infrastructure;
using Exam.Domain.Reporting;

namespace Exam.Admin.Reporting;

/// <summary>
/// Owns only the local display of one authorized, published report result.
/// The future API adapter must decide whether a result is currently readable.
/// All methods that change the view are called on the UI dispatcher.
/// </summary>
public sealed class ReportTableSessionViewModel : BindableBase, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _cancellation;
    private ReportLoadTicket? _pending;
    private IReadOnlyList<ReportTable> _tables = Array.Empty<ReportTable>();
    private ReportTable? _selectedTable;
    private string? _examId;
    private string _unavailableReason = "报表尚未开放";
    private ReportTableKind _preferredKind = ReportTableKind.StudentTotals;
    private string? _preferredSubjectId;
    private bool _disposed;
    private string _searchText = string.Empty;
    private string? _appliedQuery;

    public ReportTableSessionViewModel()
    {
        SearchCommand = new RelayCommand(ApplySearch, () => CanSearch && !string.IsNullOrWhiteSpace(SearchText));
        ClearSearchCommand = new RelayCommand(ClearSearch, () => IsSearching);
    }

    public RelayCommand SearchCommand { get; }
    /// <summary>“返回”：恢复当前表格的完整内容。</summary>
    public RelayCommand ClearSearchCommand { get; }

    /// <summary>查询框内容：姓名或准考证号（可只填一部分）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty)) SearchCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSearching => _appliedQuery is not null;

    /// <summary>班级平均分表没有学生行，不提供查询。</summary>
    public bool CanSearch => SelectedTable is { Kind: not ReportTableKind.ClassAverages };

    /// <summary>
    /// 实际显示的表格：未查询时为完整表格；查询时只保留姓名或准考证号匹配的行，
    /// 列、排名、着色与完整表格一致，底部平均分仍为全部合格样本的结果。
    /// </summary>
    public ReportTable? DisplayedTable => _appliedQuery is { } query && SelectedTable is { Kind: not ReportTableKind.ClassAverages } table
        ? table with { Rows = Array.AsReadOnly(table.Rows.Where(row => Matches(row, query)).ToArray()) }
        : SelectedTable;

    public string SearchResultText => _appliedQuery is null || DisplayedTable is null ? string.Empty
        : DisplayedTable.Rows.Count == 0 ? "未找到匹配的学生。"
        : $"找到 {DisplayedTable.Rows.Count} 名学生。";

    public IReadOnlyList<ReportTable> Tables
    {
        get => _tables;
        private set
        {
            if (SetProperty(ref _tables, value)) OnPropertyChanged(nameof(HasTables));
        }
    }

    public bool HasTables => Tables.Count > 0;

    public ReportTable? SelectedTable
    {
        get => _selectedTable;
        set
        {
            VerifyAccess();
            if (value is not null && !Tables.Contains(value)) return;
            if (SetProperty(ref _selectedTable, value))
            {
                if (value is not null)
                {
                    _preferredKind = value.Kind;
                    _preferredSubjectId = value.SubjectId;
                }
                // 切换表格时保留当前查询，便于在各表之间查看同一名学生；班级表不过滤。
                NotifySearch();
            }
        }
    }

    public string? ExamId => _examId;

    // This is local UI text, not a server report-state code.
    public string UnavailableReason
    {
        get => _unavailableReason;
        private set => SetProperty(ref _unavailableReason, value);
    }

    /// <summary>
    /// Starts a read through an external authorized loader. Every start hides the
    /// previous table and cancels its request, including refreshes of one exam.
    /// </summary>
    public ReportLoadTicket BeginLoad(string examId)
    {
        VerifyAccess();
        if (_disposed) throw new ObjectDisposedException(nameof(ReportTableSessionViewModel));
        if (string.IsNullOrWhiteSpace(examId)) throw new ArgumentException("考试标识不能为空。", nameof(examId));

        if (!string.Equals(_examId, examId, StringComparison.Ordinal))
        {
            _preferredKind = ReportTableKind.StudentTotals;
            _preferredSubjectId = null;
        }
        CancelPending();
        _examId = examId;
        OnPropertyChanged(nameof(ExamId));
        ClearTables();
        UnavailableReason = "正在加载报表";
        _cancellation = new CancellationTokenSource();
        _pending = new ReportLoadTicket(examId, _cancellation.Token);
        return _pending;
    }

    /// <summary>Accept only the current request and one complete published result.</summary>
    public bool TryAcceptPublished(ReportLoadTicket ticket, ReportCalculationResult result)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(result);
        if (!IsCurrent(ticket)) return false;

        IReadOnlyList<ReportTable> tables;
        try
        {
            ReportResultShapeValidator.Validate(result);
            tables = ReportTableProjection.Build(result);
        }
        catch (ArgumentException)
        {
            CompletePending();
            ClearTables();
            UnavailableReason = "报表结果无效";
            return false;
        }
        CompletePending();
        Tables = tables;
        SelectedTable = tables.FirstOrDefault(table => table.Kind == _preferredKind &&
            string.Equals(table.SubjectId, _preferredSubjectId, StringComparison.Ordinal)) ?? tables[0];
        UnavailableReason = string.Empty;
        return true;
    }

    public bool TryFail(ReportLoadTicket ticket)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(ticket);
        if (!IsCurrent(ticket)) return false;
        CompletePending();
        ClearTables();
        UnavailableReason = "报表请求失败";
        return true;
    }

    public bool TrySelect(ReportTableKind kind, string? subjectId = null)
    {
        VerifyAccess();
        if (_disposed) return false;
        var table = Tables.FirstOrDefault(item => item.Kind == kind &&
            string.Equals(item.SubjectId, subjectId, StringComparison.Ordinal));
        if (table is null) return false;
        SelectedTable = table;
        return true;
    }

    /// <summary>Call immediately on a version, permission, or connection invalidation.</summary>
    public void Invalidate()
    {
        VerifyAccess();
        if (_disposed) return;
        CancelPending();
        ClearTables();
        UnavailableReason = "报表尚未开放";
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        CancelPending();
        ClearTables();
    }

    private bool IsCurrent(ReportLoadTicket ticket) => !_disposed &&
        ReferenceEquals(_pending, ticket) && !ticket.CancellationToken.IsCancellationRequested &&
        string.Equals(_examId, ticket.ExamId, StringComparison.Ordinal);

    private void ClearTables()
    {
        // 任何失效、重新加载或切换考试都同时清除查询，避免把旧结果带到新数据上。
        _appliedQuery = null;
        SearchText = string.Empty;
        SelectedTable = null;
        Tables = Array.Empty<ReportTable>();
        NotifySearch();
    }

    private void ApplySearch()
    {
        VerifyAccess();
        var query = SearchText.Trim();
        if (query.Length == 0 || !CanSearch) return;
        _appliedQuery = query;
        NotifySearch();
    }

    private void ClearSearch()
    {
        VerifyAccess();
        _appliedQuery = null;
        SearchText = string.Empty;
        NotifySearch();
    }

    // 学生表前四列固定为排名、准考证号、姓名、班级。
    private static bool Matches(ReportTableRow row, string query) =>
        row.Cells.Count > 2 &&
        (row.Cells[1].Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
         row.Cells[2].Text.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void NotifySearch()
    {
        OnPropertyChanged(nameof(DisplayedTable));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(SearchResultText));
        SearchCommand.RaiseCanExecuteChanged();
        ClearSearchCommand.RaiseCanExecuteChanged();
    }

    private void CancelPending()
    {
        var source = _cancellation;
        _pending = null;
        _cancellation = null;
        source?.Cancel();
        source?.Dispose();
    }

    private void CompletePending()
    {
        _pending = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private void VerifyAccess() => _dispatcher.VerifyAccess();
}

public sealed class ReportLoadTicket
{
    internal ReportLoadTicket(string examId, CancellationToken cancellationToken)
    {
        ExamId = examId;
        CancellationToken = cancellationToken;
    }

    public string ExamId { get; }
    public CancellationToken CancellationToken { get; }
}
