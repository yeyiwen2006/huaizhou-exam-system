using System.ComponentModel;
using System.Runtime.CompilerServices;
using Exam.Contracts.Participation;
using Exam.Contracts.Activation;
using Exam.Contracts.Submissions;

namespace Exam.Student.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// 当前会话状态：设备编号 + 当前登录考生。
/// 编号在激活后保持稳定；姓名随登录切换；退出登录清除考生数据，确保无前一学生内容。
/// </summary>
public sealed class StudentSession : ObservableObject
{
    private string? _clientNumber;
    private SessionCandidate? _candidate;
    private bool _isActivated;
    private readonly HashSet<string> _submittedExams = new(StringComparer.Ordinal);

    public bool IsActivated
    {
        get => _isActivated;
        private set => SetProperty(ref _isActivated, value);
    }

    /// <summary>服务器分配的设备编号（激活后稳定，与服务器是否在线无关）。</summary>
    public string? ClientNumber
    {
        get => _clientNumber;
        private set => SetProperty(ref _clientNumber, value);
    }

    /// <summary>当前登录考生；null 表示未登录。</summary>
    public SessionCandidate? Candidate
    {
        get => _candidate;
        private set
        {
            if (SetProperty(ref _candidate, value))
            {
                OnPropertyChanged(nameof(CandidateDisplay));
            }
        }
    }

    public string CandidateDisplay => Candidate is null ? "未登录" : Candidate.Identity.Name;

    /// <summary>登录后填充（含 SessionId 与报名考试范围）。</summary>
    public StudentSessionResult? LoginResult { get; private set; }

    public void OnActivated(string clientNumber)
    {
        ClientNumber = clientNumber;
        IsActivated = true;
    }

    public void OnActivationChanged(DeviceRecord? device)
    {
        IsActivated = device?.Status == ActivationStatus.Active;
        ClientNumber = IsActivated ? device!.ClientNumber : null;
        if (!IsActivated)
        {
            Logout();
        }
    }

    public void OnLoggedIn(StudentSessionResult result)
    {
        _submittedExams.Clear();
        LoginResult = result;
        Candidate = new SessionCandidate(result.Candidate, result.SessionId);
        OnPropertyChanged(nameof(LoginResult));
    }

    /// <summary>退出登录：清空考生信息与会话，账号数据互不互通。</summary>
    public void Logout()
    {
        LoginResult = null;
        Candidate = null;
        _submittedExams.Clear();
        OnPropertyChanged(nameof(LoginResult));
    }

    public bool HasSubmitted(string examId) => _submittedExams.Contains(examId);

    public void RecordSubmission(SubmissionReceipt receipt)
    {
        var candidateId = LoginResult?.CandidateIdsByExam?.GetValueOrDefault(receipt.ExamId) ?? LoginResult?.Candidate.CandidateId;
        if (LoginResult is null || receipt.CandidateId != candidateId ||
            !LoginResult.ExamIds.Contains(receipt.ExamId) || string.IsNullOrWhiteSpace(receipt.ReceiptId))
        {
            throw new InvalidOperationException("交卷回执与当前考生不匹配。");
        }

        _submittedExams.Add(receipt.ExamId);
    }
}

/// <summary>登录考生会话信息。</summary>
public sealed record SessionCandidate(CandidateIdentity Identity, string SessionId)
{
    public string Display => $"{Identity.Name} · 班级{Identity.ClassText} · 准考证号{Identity.AdmissionNo}";
}
