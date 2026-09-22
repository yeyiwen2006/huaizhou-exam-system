using System.Runtime.ExceptionServices;
using System.Windows.Documents;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class InstructionTextRendererTests
{
    [Fact]
    public void Chinese_instructions_are_not_omitted_and_numeric_runs_use_the_required_font()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
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
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "说明渲染未在规定时间内完成。");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
