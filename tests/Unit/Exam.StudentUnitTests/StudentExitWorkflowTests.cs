using System.IO;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed class StudentExitWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedExit_PreservesActivationAndCredentialBytes(bool loggedIn)
    {
        using var fixture = await StudentPaperFixture.CreateAsync();
        var directory = Path.Combine(Path.GetTempPath(), "student-exit-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new DeviceStore(directory);
            store.Save(fixture.Device);
            var before = File.ReadAllBytes(Path.Combine(directory, "devices.protected"));
            var session = new StudentSession();
            session.OnActivationChanged(store.Load());
            if (loggedIn) session.OnLoggedIn(fixture.Session);
            var closed = false;
            Assert.True(await new StudentExitWorkflow(session).ExitAsync(() => Task.FromResult(true), () => closed = true));
            Assert.True(closed);
            Assert.True(session.IsActivated);
            Assert.Equal(fixture.Device.ClientNumber, session.ClientNumber);
            Assert.Null(session.LoginResult);
            Assert.Null(session.Candidate);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory, "devices.protected")));
            var reopened = new DeviceStore(directory).Load();
            Assert.Equal(fixture.Device, reopened);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CancelledExit_DoesNotClearLoginOrClose()
    {
        var session = new StudentSession();
        session.OnActivated("20261002001");
        var login = new Exam.Contracts.Participation.StudentSessionResult("synthetic-session", "synthetic-device",
            new("synthetic-candidate", "合成考生", "001", "000001"), ["synthetic-exam"], DateTimeOffset.UtcNow);
        session.OnLoggedIn(login);
        var closed = false;
        Assert.False(await new StudentExitWorkflow(session).ExitAsync(() => Task.FromResult(false), () => closed = true));
        Assert.False(closed);
        Assert.Same(login, session.LoginResult);
        Assert.True(session.IsActivated);
    }

    [Fact]
    public async Task ConcurrentClicks_OpenOnlyOneConfirmation()
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new StudentSession();
        session.OnActivated("20261002001");
        var workflow = new StudentExitWorkflow(session);
        var closeCount = 0;
        var first = workflow.ExitAsync(() => confirmation.Task, () => closeCount++);
        Assert.False(await workflow.ExitAsync(() => throw new InvalidOperationException(), () => closeCount++));
        confirmation.SetResult(true);
        Assert.True(await first);
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public async Task LoginChangedWhileConfirming_DoesNotCloseNewSession()
    {
        var session = new StudentSession();
        session.OnActivated("20261002001");
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        var exit = new StudentExitWorkflow(session).ExitAsync(() => confirmation.Task, () => closed = true);
        session.OnLoggedIn(new("new-synthetic-session", "device", new("candidate", "合成考生", "001", "000001"), [], DateTimeOffset.UtcNow));
        confirmation.SetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => exit);
        Assert.False(closed);
        Assert.NotNull(session.LoginResult);
    }
}
