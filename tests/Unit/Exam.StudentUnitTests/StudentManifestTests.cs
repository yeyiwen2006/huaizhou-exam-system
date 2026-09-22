using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Exam.StudentUnitTests;

public sealed class StudentManifestTests
{
    [Fact]
    public void Manifest_HasValidActivationContextAndOrdinaryUserDpiSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Huaizhou.ExamSystem.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "src", "Exam.Student", "app.manifest");
        var document = XDocument.Load(path);
        XNamespace dpi = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";
        XNamespace privileges = "urn:schemas-microsoft-com:asm.v3";
        Assert.Equal("PerMonitorV2", document.Descendants(dpi + "dpiAwareness").Single().Value);
        Assert.Equal("asInvoker", document.Descendants(privileges + "requestedExecutionLevel").Single().Attribute("level")?.Value);

        var context = new ActivationContext { Size = Marshal.SizeOf<ActivationContext>(), Source = path };
        var handle = CreateActCtx(ref context);
        Assert.True(handle != new IntPtr(-1), new Win32Exception(Marshal.GetLastWin32Error()).Message);
        ReleaseActCtx(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ActivationContext
    {
        public int Size;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Source;
        public ushort ProcessorArchitecture;
        public ushort LanguageId;
        public IntPtr AssemblyDirectory;
        public IntPtr ResourceName;
        public IntPtr ApplicationName;
        public IntPtr Module;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateActCtxW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateActCtx(ref ActivationContext context);

    [DllImport("kernel32.dll")]
    private static extern void ReleaseActCtx(IntPtr context);
}
