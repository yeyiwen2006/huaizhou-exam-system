using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Student;
using Exam.Infrastructure.Security;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed partial class ExamPackageManagerTests
{
    [Fact]
    public async Task WithdrawalAllowsWaitingAndReturningBeforeASecondConfirmation()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(manager.CanLeaveWaitingPage);
        Assert.True(await manager.PrepareAndDownloadAsync());
        Assert.True(manager.CanLeaveWaitingPage);
        Assert.True((await manager.ConfirmParticipationAsync())?.IsSuccess);
        Assert.True(manager.CanLeaveWaitingPage);
        Assert.True((await manager.WithdrawAsync())?.IsSuccess);
        Assert.True(await manager.PrepareAndDownloadAsync());
        Assert.Equal(ExamPackageManager.Phase.Downloaded, manager.CurrentPhase);
        Assert.True(manager.CanLeaveWaitingPage);
    }

    [Fact]
    public async Task FailedUnlockBeforeFirstAnswersDoesNotRequireANonexistentAnswerDatabase()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        fixture.Api.Lifecycle = ExamLifecycle.Running;
        fixture.Api.DenyUnlock = true;
        using (var first = fixture.Manager())
        {
            Assert.True(await first.PrepareAndDownloadAsync());
            await first.ConfirmParticipationAsync();
            Assert.True(first.ExamStarted);
            Assert.Null(first.UnlockedPayload);
        }
        using var restored = fixture.Manager();
        Assert.False(restored.RequiresLocalAnswerRecovery);
        fixture.Api.DenyUnlock = false;
        Assert.True(await restored.PrepareAndDownloadAsync());
        await restored.ConfirmParticipationAsync();
        Assert.Equal(ExamPackageManager.Phase.Unlocked, restored.CurrentPhase);
        Assert.Equal(fixture.Api.Plaintext, restored.UnlockedPayload);
    }

    [Fact]
    public async Task WaitingRecoveryUsesServerStateToUnlockAfterAStartEventWasMissed()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        Assert.False(manager.ExamStarted);
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        fixture.Api.Lifecycle = ExamLifecycle.Running;
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        Assert.Equal(ExamPackageManager.Phase.Unlocked, manager.CurrentPhase);
        Assert.False(manager.CanLeaveWaitingPage);
    }

    [Fact]
    public async Task ElapsedScheduleCannotReleaseAnsweringParticipationButVerifiedEndCan()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        Assert.True((await manager.ConfirmParticipationAsync())?.IsSuccess);
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
        var past = DateTimeOffset.UtcNow.AddHours(-3);
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start() with
        {
            EventId = "past-schedule", EventType = CommandEventType.ScheduleChanged, StateVersion = 3,
            ProtectedUnlockReference = null, NewScheduledStart = past, NewScheduledEnd = past.AddHours(1)
        }));
        Assert.False(manager.ExamEnded);
        Assert.False(manager.CanLeaveWaitingPage);
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start() with
        {
            EventId = "verified-end", EventType = CommandEventType.End, StateVersion = 4,
            ProtectedUnlockReference = null
        }));
        Assert.True(manager.ExamEnded);
        Assert.True(manager.CanLeaveWaitingPage);
    }

    [Fact]
    public async Task ScheduleUpdates_AreVersionedDurableAndRejectChangedReplay()
    {
        using var fixture = await Fixture.CreateAsync();
        var start = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(8));
        var latest = fixture.Api.Start() with
        {
            EventId = "schedule-v3", EventType = CommandEventType.ScheduleChanged, StateVersion = 3,
            ProtectedUnlockReference = null, NewScheduledStart = start, NewScheduledEnd = start.AddHours(3)
        };
        using (var manager = fixture.Manager())
        {
            Assert.True(await manager.PrepareAndDownloadAsync());
            Assert.True((await manager.ConfirmParticipationAsync())?.IsSuccess);
            Assert.True(await manager.HandleCommandAsync(latest));
            Assert.Equal(start.AddHours(3), manager.OverriddenScheduledEnd);
            Assert.True(await manager.HandleCommandAsync(latest with
            { EventId = "schedule-v2", StateVersion = 2, NewScheduledEnd = start.AddHours(2) }));
            Assert.Equal(start.AddHours(3), manager.OverriddenScheduledEnd);
            Assert.Equal(CommandProcessingResult.RejectedAsStale, fixture.Api.Acks[^1].ProcessingResult);
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.HandleCommandAsync(latest with
            { NewScheduledEnd = start.AddHours(4) }));
        }
        using var restored = fixture.Manager();
        Assert.Equal(start, restored.OverriddenScheduledStart);
        Assert.Equal(start.AddHours(3), restored.OverriddenScheduledEnd);
        Assert.True(await restored.PrepareAndDownloadAsync());
        Assert.True((await restored.ConfirmParticipationAsync())?.IsSuccess);
        Assert.True(await restored.HandleCommandAsync(latest with
        { EventId = "schedule-v4", StateVersion = 4, NewScheduledStart = null, NewScheduledEnd = start.AddHours(4) }));
        Assert.Equal(start, restored.OverriddenScheduledStart);
        Assert.Equal(start.AddHours(4), restored.OverriddenScheduledEnd);
        Assert.Equal(CommandProcessingResult.Applied, fixture.Api.Acks[^1].ProcessingResult);
    }

    [Fact]
    public async Task ScheduleVersion_DoesNotInvalidateAnEarlierValidStartEvent()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        var start = DateTimeOffset.UtcNow;
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start() with
        {
            EventId = "schedule-after-start", EventType = CommandEventType.ScheduleChanged, StateVersion = 3,
            ProtectedUnlockReference = null, NewScheduledStart = start, NewScheduledEnd = start.AddHours(2)
        }));
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
        Assert.Equal(fixture.Api.Plaintext, manager.UnlockedPayload);
        Assert.Equal(CommandProcessingResult.Applied, fixture.Api.Acks[^1].ProcessingResult);
    }

    [Fact]
    public async Task InvalidSchedule_DoesNotOverwriteOrAcknowledge()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        var start = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.HandleCommandAsync(fixture.Api.Start() with
        {
            EventId = "invalid-time", EventType = CommandEventType.ScheduleChanged,
            ProtectedUnlockReference = null, NewScheduledStart = start, NewScheduledEnd = start.AddMinutes(-1)
        }));
        Assert.Null(manager.OverriddenScheduledStart);
        Assert.Empty(fixture.Api.Acks);
    }

    [Fact]
    public async Task WaitingStoresOnlyCiphertext_AndRealV2StartUnlocksThenAcknowledges()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        Assert.NotNull(manager.Instructions);
        Assert.False(await manager.UnlockAsync());
        Assert.Equal(0, fixture.Api.UnlockCalls);
        Assert.Null(manager.UnlockedPayload);
        Assert.Equal(fixture.Api.Ciphertext, await File.ReadAllBytesAsync(manager.PackageFilePath));
        Assert.True((await manager.ConfirmParticipationAsync())?.IsSuccess);
        Assert.False(manager.CanConfirmParticipation);
        Assert.Null(await manager.ConfirmParticipationAsync());
        Assert.Single(fixture.Api.Confirms);
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
        Assert.Equal(fixture.Api.Plaintext, manager.UnlockedPayload);
        Assert.Equal(CommandProcessingResult.Applied, Assert.Single(fixture.Api.Acks).ProcessingResult);
        Assert.All(fixture.Api.LastKey!, item => Assert.Equal(0, item));
        var recovery = await File.ReadAllBytesAsync(Directory.GetFiles(fixture.Root, "control.protected", SearchOption.AllDirectories).Single());
        Assert.False(recovery.AsSpan().IndexOf(fixture.Api.Plaintext) >= 0);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("instructions")]
    public async Task IncompleteOrWrongVersionResourcesNeverConfirm(string failure)
    {
        using var fixture = await Fixture.CreateAsync();
        if (failure == "truncated") fixture.Api.DownloadBytes = fixture.Api.Ciphertext[..^1];
        if (failure == "oversized") fixture.Api.DownloadBytes = [.. fixture.Api.Ciphertext, 0];
        if (failure == "instructions") fixture.Api.InstructionsVersion = 2;
        using var manager = fixture.Manager();
        Assert.False(await manager.PrepareAndDownloadAsync());
        Assert.False(manager.CanConfirmParticipation);
        Assert.Null(await manager.ConfirmParticipationAsync());
        Assert.Empty(fixture.Api.Confirms);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreparationAndConfirmationLostResponsesReplaySameRequestsAcrossRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Api.DropPrepare = true;
        using (var first = fixture.Manager())
            await Assert.ThrowsAsync<HttpRequestException>(() => first.PrepareAndDownloadAsync());
        using (var second = fixture.Manager())
        {
            Assert.True(await second.PrepareAndDownloadAsync());
            Assert.Equal(fixture.Api.Prepares[0], fixture.Api.Prepares[1]);
            fixture.Api.DropConfirm = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => second.ConfirmParticipationAsync());
            Assert.True(second.HasPendingConfirmation);
        }
        using var restored = fixture.Manager();
        Assert.True(restored.HasPendingConfirmation);
        Assert.True(await restored.PrepareAndDownloadAsync());
        Assert.Equal(fixture.Api.Confirms[0], fixture.Api.Confirms[1]);
        Assert.False(restored.HasPendingConfirmation);
    }

    [Fact]
    public async Task ExitResolvesAmbiguousConfirmation_ThenUsesCurrentVersionAndStableWithdrawalId()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var manager = fixture.Manager())
        {
            Assert.True(await manager.PrepareAndDownloadAsync());
            fixture.Api.DropConfirm = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => manager.ConfirmParticipationAsync());
            fixture.Api.DropWithdraw = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => manager.WithdrawAsync());
            Assert.Equal(fixture.Api.Confirms[0], fixture.Api.Confirms[1]);
            Assert.Equal(7, Assert.Single(fixture.Api.Withdrawals).ExpectedVersion);
        }
        using var restored = fixture.Manager();
        var result = await restored.WithdrawAsync();
        Assert.Equal(ParticipationBusinessState.NotParticipating, result?.Result?.BusinessState);
        Assert.Equal(fixture.Api.Withdrawals[0], fixture.Api.Withdrawals[1]);
        Assert.Equal(ExamPackageManager.Phase.Withdrawn, restored.CurrentPhase);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("epoch")]
    [InlineData("exam")]
    [InlineData("participation")]
    [InlineData("entry")]
    [InlineData("device")]
    [InlineData("authorization")]
    [InlineData("paper")]
    public async Task ForeignAndStaleStartCommandsCannotReleaseKeys(string mismatch)
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        var command = fixture.Api.Start();
        command = mismatch switch
        {
            "server" => command with { Server = command.Server with { ServerId = "another-server" } },
            "epoch" => command with { Server = command.Server with { ServerEpoch = 0 } },
            "exam" => command with { ExamId = "another-exam" },
            "participation" => command with { ParticipationId = "another-participant" },
            "entry" => command with { EntryVersion = 99 },
            "device" => command with { TargetDeviceId = "another-device" },
            "authorization" => command with { TargetAuthorizationVersion = 99 },
            _ => command with { ResourceVersion = 99 }
        };
        Assert.False(await manager.HandleCommandAsync(command));
        Assert.Equal(0, fixture.Api.UnlockCalls);
        Assert.Null(manager.UnlockedPayload);
        Assert.Empty(fixture.Api.Acks);
    }

    [Fact]
    public async Task CorruptCiphertextNeverAcknowledgesStart_AndCanActuallyBeRedownloaded()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Api.DownloadBytes = fixture.Api.Ciphertext.ToArray();
        fixture.Api.DownloadBytes[^1] ^= 0x42;
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        Assert.False(await manager.HandleCommandAsync(fixture.Api.Start()));
        Assert.False(manager.HasPackage);
        Assert.Null(manager.UnlockedPayload);
        Assert.Empty(fixture.Api.Acks);
        fixture.Api.DownloadBytes = fixture.Api.Ciphertext;
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        Assert.Equal(2, fixture.Api.DownloadCalls);
        Assert.Equal(fixture.Api.Plaintext, manager.UnlockedPayload);
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
        Assert.Single(fixture.Api.Acks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AckLossAndRestartPreserveAckId_AndPreviouslyAckedStartStillReauthorizesAndDecrypts(bool loseAck)
    {
        using var fixture = await Fixture.CreateAsync();
        var start = fixture.Api.Start();
        using (var manager = fixture.Manager())
        {
            await manager.PrepareAndDownloadAsync();
            await manager.ConfirmParticipationAsync();
            fixture.Api.DropAck = loseAck;
            if (loseAck) await Assert.ThrowsAsync<HttpRequestException>(() => manager.HandleCommandAsync(start));
            else Assert.True(await manager.HandleCommandAsync(start));
        }
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        using var restored = new ExamPackageManager(fixture.Api, fixture.Root, fixture.Device,
            fixture.Session with { SessionId = "new-login-session" }, FakePackageApi.ExamId);
        Assert.Null(restored.UnlockedPayload);
        Assert.False(await restored.UnlockAsync());
        Assert.Equal(1, fixture.Api.UnlockCalls);
        Assert.True(await restored.PrepareAndDownloadAsync());
        await restored.ConfirmParticipationAsync();
        Assert.Equal(2, fixture.Api.UnlockCalls);
        Assert.Equal(fixture.Api.Plaintext, restored.UnlockedPayload);
        Assert.True(await restored.HandleCommandAsync(start));
        Assert.Equal(fixture.Api.Acks[0], fixture.Api.Acks[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedRecoveryKeyCannotBypassRevokedAuthorization(bool missingErrorIdentity)
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        using (var first = fixture.Manager())
        {
            await first.PrepareAndDownloadAsync();
            await first.ConfirmParticipationAsync();
            Assert.NotNull(first.UnlockedPayload);
        }
        fixture.Api.DenyUnlock = true;
        fixture.Api.OmitErrorIdentity = missingErrorIdentity;
        using var restored = fixture.Manager();
        Assert.False(await restored.UnlockAsync());
        await restored.PrepareAndDownloadAsync();
        await restored.ConfirmParticipationAsync();
        Assert.Null(restored.UnlockedPayload);
        Assert.Equal(ExamPackageManager.Phase.Revoked, restored.CurrentPhase);
    }

    [Fact]
    public async Task HigherAuthorizationEventForThisDeviceStopsDisplay_ButOtherDevicesDoNot()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        using var manager = fixture.Manager();
        await manager.PrepareAndDownloadAsync();
        await manager.ConfirmParticipationAsync();
        var change = fixture.Api.Start() with
        {
            EventId = "decision",
            EventType = CommandEventType.DeviceDecided,
            TargetAuthorizationVersion = 2,
            ProtectedUnlockReference = null
        };
        Assert.False(await manager.HandleCommandAsync(change with { TargetDeviceId = "other-device" }));
        Assert.NotNull(manager.UnlockedPayload);
        Assert.False(await manager.HandleCommandAsync(change));
        Assert.Null(manager.UnlockedPayload);
        Assert.Equal(ExamPackageManager.Phase.Revoked, manager.CurrentPhase);
        Assert.Empty(fixture.Api.Acks);
    }

    [Fact]
    public async Task ControlStorageFailureCannotSendAnUnrecoverableMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(manager.PackageFilePath)!, "control.protected"));
        await Assert.ThrowsAnyAsync<IOException>(() => manager.PrepareAndDownloadAsync());
        Assert.Empty(fixture.Api.Prepares);
        Assert.False(manager.CanConfirmParticipation);
    }

    [Theory]
    [InlineData(false, 32)]
    [InlineData(true, 1175)]
    public async Task ControlSaveRetriesTransientFileOccupancy_AndConfirmationSurvivesRestart(bool holdBackup, int win32Error)
    {
        using var fixture = await Fixture.CreateAsync();
        using (var manager = fixture.Manager())
        {
            Assert.True(await manager.PrepareAndDownloadAsync());
            var statePath = Directory.GetFiles(fixture.Root, "control.protected", SearchOption.AllDirectories).Single();
            FileStream? held = null;
            var savingThreadId = 0;
            var observedFailures = 0;
            var expectedHResult = unchecked((int)0x80070000) | win32Error;
            fixture.Api.BeforeConfirmResponse = () =>
            {
                savingThreadId = Environment.CurrentManagedThreadId;
                held = new FileStream(holdBackup ? statePath + ".bak" : statePath,
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            };
            // Release only after the real replacement fails on this writer thread.
            // This avoids timing sleeps and proves that a retry, not the first attempt, succeeds.
            EventHandler<FirstChanceExceptionEventArgs> releaseAfterFailure = (_, args) =>
            {
                if (held is not null && Environment.CurrentManagedThreadId == savingThreadId &&
                    args.Exception is IOException error && error.HResult == expectedHResult)
                {
                    observedFailures++;
                    held.Dispose();
                    held = null;
                }
            };
            AppDomain.CurrentDomain.FirstChanceException += releaseAfterFailure;
            try
            {
                Assert.True((await manager.ConfirmParticipationAsync())?.IsSuccess);
                Assert.Equal(1, observedFailures);
                Assert.Single(fixture.Api.Confirms);
                Assert.NotNull(manager.Participation);
                Assert.False(manager.HasPendingConfirmation);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= releaseAfterFailure;
                held?.Dispose();
            }
        }

        using var restored = fixture.Manager();
        Assert.Equal("participation", restored.ParticipationId);
        Assert.Equal(ExamPackageManager.Phase.ConfirmedWaiting, restored.CurrentPhase);
        Assert.False(restored.HasPendingConfirmation);
    }

    [Theory]
    [InlineData(false, 32)]
    [InlineData(true, 1175)]
    public async Task ControlSavePersistentFileOccupancyFails_WithoutChangingStateOrBackup(bool holdBackup, int win32Error)
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        var statePath = Directory.GetFiles(fixture.Root, "control.protected", SearchOption.AllDirectories).Single();
        var state = File.ReadAllBytes(statePath);
        var backup = File.ReadAllBytes(statePath + ".bak");
        using (new FileStream(holdBackup ? statePath + ".bak" : statePath,
                   FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<IOException>(() => manager.ConfirmParticipationAsync());
            Assert.Equal(unchecked((int)0x80070000) | win32Error, error.HResult);
            Assert.Empty(fixture.Api.Confirms);
            Assert.False(manager.HasPendingConfirmation);
            Assert.Null(manager.Participation);
            Assert.Equal(state, File.ReadAllBytes(statePath));
            Assert.Equal(backup, File.ReadAllBytes(statePath + ".bak"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(statePath)!, "control-*.tmp"));
        }

        Assert.True((await manager.ConfirmParticipationAsync())?.IsSuccess);
        Assert.Single(fixture.Api.Confirms);
        manager.Dispose();
        using var restored = fixture.Manager();
        Assert.Equal("participation", restored.ParticipationId);
        Assert.False(restored.HasPendingConfirmation);
    }

    [Fact]
    public async Task AuthorizationLossClearsVisiblePlaintextEvenWhenItsJournalCannotBeSaved()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        using var manager = fixture.Manager();
        await manager.PrepareAndDownloadAsync();
        await manager.ConfirmParticipationAsync();
        var visibleBytes = Assert.IsType<byte[]>(manager.UnlockedPayload);
        var statePath = Directory.GetFiles(fixture.Root, "control.protected", SearchOption.AllDirectories).Single();
        File.Move(statePath, statePath + ".fault-test-original");
        Directory.CreateDirectory(statePath);
        await Assert.ThrowsAnyAsync<IOException>(() => manager.InvalidateAuthorizationAsync());
        Assert.Null(manager.UnlockedPayload);
        Assert.All(visibleBytes, value => Assert.Equal(0, value));
        Assert.Equal(ExamPackageManager.Phase.Revoked, manager.CurrentPhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationOrEndImmediatelyStopsAConcurrentUnlock_AndOldResultCannotPublish(bool end)
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        await manager.PrepareAndDownloadAsync();
        await manager.ConfirmParticipationAsync();
        fixture.Api.UnlockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.ContinueUnlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = manager.HandleCommandAsync(fixture.Api.Start());
        await fixture.Api.UnlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stop = end
            ? manager.HandleCommandAsync(fixture.Api.Start() with
            {
                EventId = "end-racing-unlock",
                EventType = CommandEventType.End,
                ProtectedUnlockReference = null,
                StateVersion = 2
            })
            : manager.InvalidateAuthorizationAsync();
        Assert.Equal(end ? ExamPackageManager.Phase.Ended : ExamPackageManager.Phase.Revoked, manager.CurrentPhase);
        Assert.Null(manager.UnlockedPayload);
        fixture.Api.ContinueUnlock.TrySetResult();
        Assert.False(await start);
        await stop;
        Assert.Null(manager.UnlockedPayload);
        Assert.DoesNotContain(fixture.Api.Acks, acknowledgement => acknowledgement.EventId == "start");
    }

    [Fact]
    public async Task EndIsDurableAndLaterStartNeverReopens_EndedRecoveryDoesNotFetchKey()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var first = fixture.Manager())
        {
            await first.PrepareAndDownloadAsync();
            await first.ConfirmParticipationAsync();
            Assert.True(await first.HandleCommandAsync(fixture.Api.Start() with
            { EventId = "end", EventType = CommandEventType.End, ProtectedUnlockReference = null, StateVersion = 2 }));
            Assert.True(await first.HandleCommandAsync(fixture.Api.Start()));
            Assert.True(first.ExamEnded);
            Assert.Null(first.UnlockedPayload);
            Assert.Equal(CommandProcessingResult.RejectedAsStale, fixture.Api.Acks.Last().ProcessingResult);
        }
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        fixture.Api.Lifecycle = ExamLifecycle.Ended;
        using var restored = fixture.Manager();
        Assert.True(restored.ExamEnded);
        Assert.True(await restored.PrepareAndDownloadAsync());
        await restored.ConfirmParticipationAsync();
        Assert.Equal(ExamPackageManager.Phase.Ended, restored.CurrentPhase);
        Assert.False(await restored.UnlockAsync());
        Assert.Equal(0, fixture.Api.UnlockCalls);
    }

    [Fact]
    public async Task FailedNewDownloadPreservesOldCompleteCiphertext()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        await manager.PrepareAndDownloadAsync();
        var originalPath = manager.PackageFilePath;
        fixture.Api.PaperVersion = 2;
        fixture.Api.DownloadBytes = fixture.Api.Ciphertext[..^1];
        Assert.False(await manager.PrepareAndDownloadAsync());
        Assert.Equal(fixture.Api.Ciphertext, await File.ReadAllBytesAsync(originalPath));
        Assert.NotEqual(originalPath, manager.PackageFilePath);
    }

    [Fact]
    public async Task StorageIsolationUsesLogicalServerCandidateAndExam_AndCannotTraversePaths()
    {
        using var fixture = await Fixture.CreateAsync();
        using var first = fixture.Manager();
        using var otherServer = new ExamPackageManager(fixture.Api, fixture.Root,
            fixture.Device with { ServerId = "../../other-server" }, fixture.Session, FakePackageApi.ExamId);
        using var otherCandidate = new ExamPackageManager(fixture.Api, fixture.Root,
            fixture.Device, fixture.Session with { Candidate = fixture.Session.Candidate with { CandidateId = "../another-candidate" } },
            FakePackageApi.ExamId);
        using var otherExam = new ExamPackageManager(fixture.Api, fixture.Root, fixture.Device,
            fixture.Session with { ExamIds = ["../../another-exam"] }, "../../another-exam");
        var paths = new[] { first.PackageFilePath, otherServer.PackageFilePath, otherCandidate.PackageFilePath, otherExam.PackageFilePath };
        Assert.Equal(4, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, path => Assert.StartsWith(Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar,
            Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"exam-package-tests-{Guid.NewGuid():N}");
        public required FakePackageApi Api { get; init; }
        public DeviceRecord Device { get; } = new("device", "test-credential", "20260922001", "127.0.0.1", 5443,
            DateTimeOffset.UtcNow, "server", ActivationStatus.Active, CredentialVersion: 1, ServerEpoch: 1);
        public StudentSessionResult Session { get; } = new("session", "device",
            new CandidateIdentity("candidate", "合成考生", "01", "001"), [FakePackageApi.ExamId], DateTimeOffset.UtcNow);
        public static async Task<Fixture> CreateAsync() => new() { Api = await FakePackageApi.CreateAsync() };
        public ExamPackageManager Manager() => new(Api, Root, Device, Session, FakePackageApi.ExamId);
        public void Dispose() { Api.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private sealed class FakePackageApi : IExamPackageApi, IDisposable
    {
        public const string ExamId = "exam";
        private readonly Dictionary<string, EntryPreparationResult> _preparations = [];
        private readonly Dictionary<string, ParticipationResult> _confirmations = [];
        private readonly Dictionary<string, ParticipationResult> _withdrawals = [];
        private X509Certificate2 _node = null!;
        private EncryptedPackageEnvelope _envelope = null!;
        public byte[] Plaintext { get; } = Encoding.UTF8.GetBytes("独立合成题包内容，用于真实v2加密链路测试。");
        public byte[] Ciphertext { get; private set; } = [];
        public byte[] DownloadBytes { get; set; } = [];
        public long PaperVersion { get; set; } = 1;
        public long InstructionsVersion { get; set; } = 1;
        public ParticipationBusinessState BusinessState { get; set; } = ParticipationBusinessState.Waiting;
        public ExamLifecycle Lifecycle { get; set; } = ExamLifecycle.Scheduled;
        public long ScheduleVersion { get; set; }
        public long ReopenedAtScheduleVersion { get; set; }
        public DateTimeOffset? ScheduledStart { get; set; }
        public DateTimeOffset? ScheduledEnd { get; set; }
        public bool DropPrepare { get; set; }
        public bool DropConfirm { get; set; }
        public bool DropWithdraw { get; set; }
        public bool DropAck { get; set; }
        public bool DenyUnlock { get; set; }
        public bool OmitErrorIdentity { get; set; }
        public int UnlockCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public byte[]? LastKey { get; private set; }
        public TaskCompletionSource? UnlockEntered { get; set; }
        public TaskCompletionSource? ContinueUnlock { get; set; }
        public Action? BeforeConfirmResponse { get; set; }
        public List<PrepareExamEntryRequest> Prepares { get; } = [];
        public List<ConfirmParticipationRequest> Confirms { get; } = [];
        public List<WithdrawParticipationRequest> Withdrawals { get; } = [];
        public List<AcknowledgeCommandRequest> Acks { get; } = [];

        public static async Task<FakePackageApi> CreateAsync()
        {
            using var rsa = RSA.Create(2048);
            var certificate = new CertificateRequest("CN=B02-test-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var api = new FakePackageApi { _node = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)) };
            using var encrypted = new MemoryStream();
            api._envelope = await new PackageEncryptionService(4096).EncryptAsync(new MemoryStream(api.Plaintext),
                encrypted, "package", 1, [new("node", api._node)]);
            api.Ciphertext = encrypted.ToArray();
            api.DownloadBytes = api.Ciphertext;
            return api;
        }

        public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default)
        {
            Prepares.Add(request);
            if (!_preparations.TryGetValue(request.RequestId, out var preparation))
                _preparations[request.RequestId] = preparation = new(request.RequestId, ExamId, EntryPreparationStatus.Ready,
                    new ExamResourceDescriptor("/api/v1/resources/package", Ciphertext.Length, PaperVersion, 1),
                    null, null, null, null, null, DateTimeOffset.UtcNow);
            if (DropPrepare) { DropPrepare = false; throw new HttpRequestException("Synthetic lost prepare response."); }
            return Task.FromResult(Ok(preparation));
        }
        public Task<ApiEnvelope<InstructionsDocument>> GetInstructionsAsync(string preparationId, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Ok(new InstructionsDocument(InstructionsVersion, ["合成考试说明，共1个题组。", "请等待服务器开考。"])));
        public async IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(ExamResourceDescriptor descriptor, string preparationId,
            string sessionId, [EnumeratorCancellation] CancellationToken ct = default)
        { DownloadCalls++; ct.ThrowIfCancellationRequested(); await Task.Yield(); yield return DownloadBytes; }
        public Task<ApiEnvelope<ParticipationResult>> ConfirmParticipationAsync(ConfirmParticipationRequest request, CancellationToken ct = default)
        {
            Confirms.Add(request);
            if (!_confirmations.TryGetValue(request.RequestId, out var result))
                _confirmations[request.RequestId] = result = new("participation", ExamId, "candidate", BusinessState,
                    "device", 1, 1, 7, BusinessState == ParticipationBusinessState.Answering ? "resource-envelope:package" : null,
                    Lifecycle: Lifecycle)
                {
                    ScheduleVersion = ScheduleVersion, ReopenedAtScheduleVersion = ReopenedAtScheduleVersion,
                    ScheduledStart = ScheduledStart, ScheduledEnd = ScheduledEnd
                };
            BeforeConfirmResponse?.Invoke();
            if (DropConfirm) { DropConfirm = false; throw new HttpRequestException("Synthetic lost confirmation response."); }
            return Task.FromResult(Ok(result));
        }
        public Task<ApiEnvelope<ParticipationResult>> WithdrawParticipationAsync(WithdrawParticipationRequest request, CancellationToken ct = default)
        {
            Withdrawals.Add(request);
            if (!_withdrawals.TryGetValue(request.RequestId, out var result))
                _withdrawals[request.RequestId] = result = new("participation", ExamId, "candidate",
                    ParticipationBusinessState.NotParticipating, "device", 1, 1, request.ExpectedVersion + 1, null);
            if (DropWithdraw) { DropWithdraw = false; throw new HttpRequestException("Synthetic lost withdrawal response."); }
            return Task.FromResult(Ok(result));
        }
        public async Task<ApiEnvelope<PackageUnlockMaterial>> ResolveUnlockAsync(PackageUnlockRequest request, CancellationToken ct = default)
        {
            UnlockCalls++;
            UnlockEntered?.TrySetResult();
            if (ContinueUnlock is not null) await ContinueUnlock.Task.WaitAsync(ct);
            if (DenyUnlock) return new ApiEnvelope<PackageUnlockMaterial>(
                OmitErrorIdentity ? string.Empty : "server", OmitErrorIdentity ? string.Empty : "node", OmitErrorIdentity ? 0 : 1,
                request.RequestId, null, new ApiError(ErrorCodes.AuthorizationRevoked, "测试设备授权已撤销。"));
            LastKey = PackageEncryptionService.UnwrapDataKey(_envelope, "node", _node);
            return Ok(new PackageUnlockMaterial(_envelope.PackageId, _envelope.PaperVersion,
                _envelope.ChunkSize, _envelope.NoncePrefix, LastKey));
        }
        public Task<ApiEnvelope<CommandAcknowledgement>> AcknowledgeCommandAsync(AcknowledgeCommandRequest request, CancellationToken ct = default)
        {
            Acks.Add(request);
            if (DropAck) { DropAck = false; throw new HttpRequestException("Synthetic lost ACK response."); }
            return Task.FromResult(Ok(new CommandAcknowledgement(request.EventId, request.RequestId,
                request.ProcessingResult, request.ActualPaperVersion, request.FailureReason, DateTimeOffset.UtcNow)));
        }
        public CommandDelivery Start() => new(new ServerNodeEnvelope("server", "node", 1), "start", ExamId,
            "participation", 1, CommandEventType.Start, 1, "device", 1, 1, "resource-envelope:package",
            new Dictionary<string, string>(), DateTimeOffset.UtcNow, 1);
        private static ApiEnvelope<T> Ok<T>(T value) => new("server", "node", 1, "correlation", value, null);
        public void Dispose() => _node.Dispose();
    }
}
