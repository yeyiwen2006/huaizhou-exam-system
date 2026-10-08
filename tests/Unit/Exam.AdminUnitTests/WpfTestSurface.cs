using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace Exam.AdminUnitTests;

/// <summary>A native test surface independent of the process-wide Application lifetime.</summary>
internal sealed class WpfTestSurface : IDisposable
{
    private readonly Border _root;
    private readonly HwndSource _source;
    private readonly Size _size;

    public WpfTestSurface(UIElement content, int width, int height)
    {
        _size = new Size(width, height);
        _root = new Border { Child = content, Width = width, Height = height };
        _root.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml")
        });
        _source = new HwndSource(new HwndSourceParameters("Synthetic scroll test surface")
        {
            Width = width, Height = height, PositionX = -10000, PositionY = -10000,
            WindowStyle = unchecked((int)0x80000000)
        });
        _source.RootVisual = _root;
        UpdateLayout();
    }

    public void UpdateLayout()
    {
        _root.Measure(_size);
        _root.Arrange(new Rect(_size));
        _root.UpdateLayout();
    }

    public void Dispose()
    {
        _source.RootVisual = null;
        _source.Dispose();
    }
}
