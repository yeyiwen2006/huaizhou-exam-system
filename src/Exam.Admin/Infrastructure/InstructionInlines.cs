using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Exam.Admin.ViewModels;

namespace Exam.Admin.Infrastructure;

/// <summary>字体分段保留在同一段文字内，使长说明按文字自然换行。</summary>
public static class InstructionInlines
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<InstructionSegment>), typeof(InstructionInlines),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static void SetSegments(DependencyObject target, IReadOnlyList<InstructionSegment>? value) => target.SetValue(SegmentsProperty, value);
    public static IReadOnlyList<InstructionSegment>? GetSegments(DependencyObject target) =>
        (IReadOnlyList<InstructionSegment>?)target.GetValue(SegmentsProperty);

    private static void OnSegmentsChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var segment in (IReadOnlyList<InstructionSegment>?)args.NewValue ?? [])
            block.Inlines.Add(new Run(segment.Text) { FontFamily = new FontFamily(segment.FontFamily) });
    }
}
