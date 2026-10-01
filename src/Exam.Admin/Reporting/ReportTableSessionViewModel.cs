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
            if (SetProperty(ref _selectedTable, value) && value is not null)
            {
                _preferredKind = value.Kind;
                _preferredSubjectId = value.SubjectId;
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
        SelectedTable = null;
        Tables = Array.Empty<ReportTable>();
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
