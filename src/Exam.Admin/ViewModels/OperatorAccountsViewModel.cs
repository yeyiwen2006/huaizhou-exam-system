using System.Collections.ObjectModel;
using Exam.Admin.Infrastructure;
using Exam.Admin.Services;
using Exam.Contracts.Operators;
using Exam.Desktop.Shared.Controls;

namespace Exam.Admin.ViewModels;

/// <summary>考务账号管理（仅管理员）：查看全部账号，批准新注册的账号，停用或重新启用账号。</summary>
public sealed class OperatorAccountsViewModel : BindableBase
{
    private readonly AdminApiClient _api;
    private string _status = "管理员登录后可在此批准或停用考务账号。";
    private long _generation;

    public OperatorAccountsViewModel(AdminApiClient api)
    {
        _api = api;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _api.IsAdministrator, SetError);
        _api.ConfigurationChanged += (_, _) =>
        {
            _generation++;
            Accounts.Clear();
            Status = "管理员登录后可在此批准或停用考务账号。";
            RefreshCommand.RaiseCanExecuteChanged();
        };
    }

    public ObservableCollection<OperatorAccountRowViewModel> Accounts { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public void NotifyLoginChanged() => RefreshCommand.RaiseCanExecuteChanged();

    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        Status = "正在读取考务账号…";
        var accounts = await _api.ListOperatorsAsync();
        if (generation != _generation) return;
        Accounts.Clear();
        foreach (var account in accounts)
            Accounts.Add(new OperatorAccountRowViewModel(account, _api.OperatorId, status => SetStatusAsync(account.Username, status), SetError));
        var pending = Accounts.Count(item => item.Account.Status == OperatorStatus.Pending);
        Status = pending == 0 ? $"共 {Accounts.Count} 个考务账号。" : $"共 {Accounts.Count} 个考务账号，其中 {pending} 个等待批准。";
    }

    private async Task SetStatusAsync(string username, OperatorStatus status)
    {
        await _api.SetOperatorStatusAsync(username, status);
        await RefreshAsync();
    }

    private void SetError(Exception exception) => Status = $"操作未完成：{FriendlyErrors.Describe(exception)}";
}

public sealed class OperatorAccountRowViewModel
{
    public OperatorAccountRowViewModel(OperatorAccount account, string currentUsername, Func<OperatorStatus, Task> setStatus,
        Action<Exception> onError)
    {
        Account = account;
        var isSelf = string.Equals(OperatorRules.Key(account.Username), OperatorRules.Key(currentUsername), StringComparison.Ordinal);
        ApproveCommand = new AsyncRelayCommand(() => setStatus(OperatorStatus.Active),
            () => account.Status != OperatorStatus.Active, onError);
        DisableCommand = new AsyncRelayCommand(() => setStatus(OperatorStatus.Disabled),
            () => account.Status != OperatorStatus.Disabled && !isSelf, onError);
    }

    public OperatorAccount Account { get; }
    public string Username => Account.Username;
    public string DisplayName => Account.DisplayName;
    public string RoleText => Account.IsAdministrator ? "管理员" : "考务人员";
    public string StatusText => Account.Status switch
    {
        OperatorStatus.Active => "已启用",
        OperatorStatus.Pending => "等待批准",
        _ => "已停用",
    };
    public StatusLevel StatusLevel => Account.Status switch
    {
        OperatorStatus.Active => StatusLevel.Success,
        OperatorStatus.Pending => StatusLevel.Warning,
        _ => StatusLevel.Neutral,
    };
    public string CreatedText => Account.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ApproveText => Account.Status == OperatorStatus.Pending ? "批准" : "启用";
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand DisableCommand { get; }
}
