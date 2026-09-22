using System.ComponentModel;
using System.Runtime.CompilerServices;
using Exam.Contracts.Participation;

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

    /// <summary>当前正在作答的考试（B02 解锁后进入；B03 渲染消费，B04 持久化）。</summary>
    public UnlockedExamContext? CurrentExam { get; private set; }

    public void OnActivated(string clientNumber)
    {
        ClientNumber = clientNumber;
    }

    public void OnLoggedIn(StudentSessionResult result)
    {
        LoginResult = result;
        Candidate = new SessionCandidate(result.Candidate, result.SessionId);
        OnPropertyChanged(nameof(LoginResult));
    }

    /// <summary>
    /// 开始考试：记录解锁后的试卷内容（仅内存）与参试授权上下文
    /// （B04 据此核对本地记录权限：ParticipationId/EntryVersion/AuthorizationVersion）。
    /// </summary>
    public void BeginExam(
        string examId,
        byte[] unlockedPayload,
        ParticipationAuthorization? authorization = null)
    {
        CurrentExam = new UnlockedExamContext(examId, unlockedPayload, authorization);
        OnPropertyChanged(nameof(CurrentExam));
    }

    /// <summary>退出登录：清空考生信息与会话，账号数据互不互通。</summary>
    public void Logout()
    {
        LoginResult = null;
        Candidate = null;
        CurrentExam = null;
    }
}

/// <summary>已解锁的考试上下文：明文仅在内存，交卷/离开即由 GC 回收。</summary>
public sealed record UnlockedExamContext(
    string ExamId,
    byte[] Payload,
    ParticipationAuthorization? Authorization = null);

/// <summary>
/// 参试授权上下文（来自确认参试结果）：B04 本地恢复时核对权限，
/// 防止旧授权或另一参试记录写入进度（S05：旧授权不能写进度）。
/// </summary>
public sealed record ParticipationAuthorization(
    string ParticipationId,
    long EntryVersion,
    long AuthorizationVersion,
    long PaperVersion,
    string? DeviceId = null);

/// <summary>登录考生会话信息。</summary>
public sealed record SessionCandidate(CandidateIdentity Identity, string SessionId)
{
    public string Display => $"{Identity.Name} · 班级{Identity.ClassText} · 准考证号{Identity.AdmissionNo}";
}
