using System.Net.Http;
using Exam.Contracts.Commands;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient
{
    public Task<CommandEventView> RequestUploadAsync(CreateCommandEventRequest request, CancellationToken ct = default) =>
        SendAsync<CommandEventView>(HttpMethod.Post, "api/v1/commands/", request, ct);
}
