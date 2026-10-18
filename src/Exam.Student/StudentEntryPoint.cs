using System.Windows;
using Exam.Student.Core;

namespace Exam.Student;

public static class StudentEntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        // This runs before App's static services, SQLite or any WPF window exists.
        try { return ExamDesktopSession.Run(args, RunApplication); }
        catch (Exception)
        {
            MessageBox.Show("无法建立考试专用桌面，学生端尚未启动。请联系监考人员检查本机桌面权限后重试。",
                "考试防护未开启", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunApplication()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
