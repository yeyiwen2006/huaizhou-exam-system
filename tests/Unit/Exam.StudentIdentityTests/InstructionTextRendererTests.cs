using System.Windows.Threading;
using System.Windows.Documents;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class InstructionTextRendererTests
{
    [Fact]
    [Trait("Category", "Wpf")]
    public async Task Chinese_instructions_are_not_omitted_and_numeric_runs_use_the_required_font()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                const string line = "合成科目（满分12.5分）：共5小题，选对得2.5分。";
                var block = InstructionTextRenderer.Create(line);
                var runs = block.Inlines.OfType<Run>().ToArray();
                Assert.Equal(line, string.Concat(runs.Select(run => run.Text)));
                Assert.Equal("SimHei", block.FontFamily.Source);
                var numericRuns = runs.Where(run => run.FontFamily.Source == "Times New Roman").ToArray();
                Assert.Equal(new[] { "12.5", "5", "2.5" }, numericRuns.Select(run => run.Text));
                Assert.Contains(runs, run => run.Text.Contains("合成科目", StringComparison.Ordinal));
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                try { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }) { IsBackground = true, Name = "InstructionRenderingTest" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "说明渲染线程没有退出。");
    }
}
