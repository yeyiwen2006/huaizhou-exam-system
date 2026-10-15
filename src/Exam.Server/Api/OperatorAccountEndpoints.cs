using Exam.Contracts.Operators;
using Exam.Infrastructure.Postgres.Operators;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

/// <summary>考务账号：注册、登录为公开接口；其余接口要求已登录，批准和停用账号要求管理员。</summary>
public static class OperatorAccountEndpoints
{
    public static RouteGroupBuilder MapOperatorAccountApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/operators").WithTags("Operator Accounts");

        group.MapPost("/register", async (RegisterOperatorRequest request, [FromServices] PostgresOperatorAccounts accounts,
                HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier,
                () => accounts.RegisterAsync(request, cancellationToken), StatusCodes.Status201Created));

        group.MapPost("/login", async (LoginOperatorRequest request, [FromServices] PostgresOperatorAccounts accounts,
                HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier,
                () => accounts.LoginAsync(request, cancellationToken)));

        group.MapPost("/logout", async ([FromServices] PostgresOperatorAccounts accounts, HttpContext context,
                IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier, async () =>
            {
                var token = context.Request.Headers.Authorization.ToString()["Bearer ".Length..];
                await accounts.LogoutAsync(token, cancellationToken);
                return new { loggedOut = true };
            }));

        group.MapGet("/me", async ([FromServices] PostgresOperatorAccounts accounts, HttpContext context,
                IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier,
                () => accounts.GetAsync(ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context), cancellationToken)))
            .WithMetadata(new ReadOnlyObservation());

        group.MapGet("", async ([FromServices] PostgresOperatorAccounts accounts, HttpContext context,
                IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier, async () =>
            {
                if (!ExamApiAccessMiddleware.RequireAuthenticatedOperator(context).IsAdministrator)
                    throw new Exam.Application.Participation.ParticipationRuleException(
                        OperatorErrorCodes.NotAdministrator, "只有管理员可以查看全部考务账号。");
                return await accounts.ListAsync(cancellationToken);
            })).WithMetadata(new ReadOnlyObservation());

        group.MapPut("/status", async (SetOperatorStatusRequest request, [FromServices] PostgresOperatorAccounts accounts,
                HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier,
                () => accounts.SetStatusAsync(request, ExamApiAccessMiddleware.RequireAuthenticatedOperator(context), cancellationToken)));

        return api;
    }
}
