using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.LoadTests;

public sealed partial class LoadRunner
{
    private async Task CommandsAsync(CancellationToken ct, Task? holdOpenUntil = null, TaskCompletionSource? connected = null)
    {
        var hubs = new HubConnection[_manifest.Sessions.Length];
        var completions = new TaskCompletionSource[_manifest.Sessions.Length];
        var due = new double[_manifest.Sessions.Length];
        var eventIds = new string[_manifest.Sessions.Length][];
        using var socketHttp = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (holdOpenUntil is null) lifetime.CancelAfter(TimeSpan.FromSeconds(_options.DurationSeconds + _options.TimeoutSeconds));
        var token = lifetime.Token;
        var automatic = !string.IsNullOrWhiteSpace(_manifest.OperatorToken);
        var closing = false;
        try
        {
            for (var index = 0; index < hubs.Length; index++)
            {
                var i = index;
                var s = _manifest.Sessions[i];
                completions[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                eventIds[i] = automatic ? [$"load-command-{_runId}-{i}"] : _manifest.ExpectedCommandEventIds;
                var pending = new ConcurrentDictionary<string, byte>(eventIds[i].Select(id => new KeyValuePair<string, byte>(id, 0)));
                var remaining = pending.Count;
                var endpoint = new Uri(_options.Target, "/hubs/exam-events?" + s.Query +
                    $"&serverId={Uri.EscapeDataString(_manifest.ServerId)}&serverEpoch={_manifest.ServerEpoch}");
                var hub = new HubConnectionBuilder().WithUrl(endpoint, options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, cancellation) =>
                    {
                        if (context.Uri.Host != endpoint.Host || context.Uri.Port != endpoint.Port ||
                            (context.Uri.Scheme != "wss" && !(_options.AllowLoopbackHttp && endpoint.IsLoopback && context.Uri.Scheme == "ws")))
                            throw new InvalidOperationException("untrusted-websocket-origin");
                        var socket = new ClientWebSocket();
                        socket.Options.SetRequestHeader("X-Exam-Device-Credential", s.Credential);
                        try { await socket.ConnectAsync(context.Uri, socketHttp, cancellation); return socket; }
                        catch { socket.Dispose(); throw; }
                    };
                }).AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter())).Build();
                hubs[i] = hub;
                hub.Closed += _ =>
                {
                    if (!closing) _report.Add(new("command-disconnected", i, false, 0, 0, "unexpected-connection-close", DateTimeOffset.UtcNow));
                    return Task.CompletedTask;
                };
                hub.On<CommandDelivery>("Command", delivery =>
                {
                    // Do not await a hub invocation inside its dispatch callback: the reply needs that dispatcher.
                    if (!pending.TryRemove(delivery.EventId, out _)) return;
                    _ = ReceiveAsync(delivery);
                });
                async Task ReceiveAsync(CommandDelivery delivery)
                {
                    try
                    {
                        if (delivery.Server.ServerId != _manifest.ServerId || delivery.Server.ServerEpoch != _manifest.ServerEpoch ||
                            delivery.ExamId != s.ExamId || delivery.ParticipationId != s.ParticipationId || delivery.EntryVersion != s.EntryVersion ||
                            delivery.TargetDeviceId != s.DeviceId || delivery.TargetAuthorizationVersion != s.AuthorizationVersion)
                            throw new InvalidDataException("command-identity-mismatch");
                        var received = _clock.Elapsed.TotalMilliseconds;
                        _report.Add(new("command-received", i, true, Math.Max(0, received - due[i]), 0, null, DateTimeOffset.UtcNow));
                        var request = new AcknowledgeCommandRequest($"load-ack-{_runId}-{i}-{delivery.EventId}", delivery.EventId,
                            s.ParticipationId, s.EntryVersion, s.DeviceId, s.AuthorizationVersion, CommandProcessingResult.Received, s.PaperVersion, null);
                        var ack = await hub.InvokeAsync<CommandOperationResult<CommandAcknowledgement>>("Acknowledge", request, token);
                        if (!ack.IsSuccess || ack.Value?.EventId != delivery.EventId || ack.Value.ProcessingResult != CommandProcessingResult.Received)
                            throw new InvalidDataException("command-ack-mismatch");
                        _report.Add(new("command-received-and-acknowledged", i, true, _clock.Elapsed.TotalMilliseconds - due[i], 0, null, DateTimeOffset.UtcNow));
                        if (Interlocked.Decrement(ref remaining) == 0) completions[i].TrySetResult();
                    }
                    catch (Exception error) { completions[i].TrySetException(error); }
                }
            }
            var connectDue = _clock.Elapsed.TotalMilliseconds + 100;
            await Task.WhenAll(hubs.Select((hub, index) => AtAsync(connectDue, async () =>
            {
                due[index] = connectDue;
                await MeasureAsync("command-connect", index, connectDue, cancellation => hub.StartAsync(cancellation), token);
            }, token)));
            connected?.TrySetResult();
            if (automatic)
            {
                var createDue = _clock.Elapsed.TotalMilliseconds + 100;
                await Task.WhenAll(hubs.Select((hub, index) => AtAsync(createDue, async () =>
                {
                    due[index] = createDue;
                    var s = _manifest.Sessions[index];
                    await MeasureAsync("command-create", index, createDue, async cancellation =>
                    {
                        if (hub.State != HubConnectionState.Connected) throw new InvalidOperationException("hub-not-connected");
                        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/testing/commands");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _manifest.OperatorToken);
                        request.Content = JsonContent.Create(new CreateCommandEventRequest(eventIds[index][0], eventIds[index][0],
                            s.ExamId, s.ParticipationId, s.EntryVersion, CommandEventType.ScheduleChanged, 1,
                            s.DeviceId, s.AuthorizationVersion, null, null), options: LoadJson.Options);
                        using var response = await _http.SendAsync(request, cancellation);
                        if (!response.IsSuccessStatusCode) throw new HttpRequestException("http-status", null, response.StatusCode);
                        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<CommandEventView>>(LoadJson.Options, cancellation);
                        if (envelope?.Error is not null || envelope?.Result?.EventId != eventIds[index][0] ||
                            envelope.ServerId != _manifest.ServerId || envelope.ServerEpoch != _manifest.ServerEpoch)
                            throw new InvalidDataException("command-create-mismatch");
                    }, token);
                }, token)));
            }
            await Task.WhenAll(completions.Select((completion, index) => MeasureAsync("command-complete", index, due[index],
                cancellation => completion.Task.WaitAsync(cancellation), token)));
            if (holdOpenUntil is not null)
            {
                await holdOpenUntil.WaitAsync(token);
                for (var i = 0; i < hubs.Length; i++)
                {
                    var alive = hubs[i].State == HubConnectionState.Connected;
                    _report.Add(new("command-held-open", i, alive, _clock.Elapsed.TotalMilliseconds - connectDue, 0,
                        alive ? null : "hub-no-longer-connected", DateTimeOffset.UtcNow));
                }
            }
        }
        catch (Exception error) { connected?.TrySetException(error); throw; }
        finally
        {
            closing = true;
            lifetime.Cancel();
            foreach (var hub in hubs) if (hub is not null) await hub.DisposeAsync();
        }
    }
}
