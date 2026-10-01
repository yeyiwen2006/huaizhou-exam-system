using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Exam.Admin.Reporting;

/// <summary>
/// Read-only table renderer. A caller must clear Table when report authorization or
/// snapshot validity changes; this control never caches a previous result.
/// </summary>
public partial class ReportTableView : UserControl
{
    private static readonly Brush ReportRed = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

    public static readonly DependencyProperty TableProperty = DependencyProperty.Register(
        nameof(Table), typeof(ReportTable), typeof(ReportTableView),
        new PropertyMetadata(null, (owner, _) => ((ReportTableView)owner).RenderTable()));

    public static readonly DependencyProperty UnavailableReasonProperty = DependencyProperty.Register(
        nameof(UnavailableReason), typeof(string), typeof(ReportTableView),
        new PropertyMetadata("报表尚未开放", (owner, _) => ((ReportTableView)owner).RenderTable()));

    public ReportTable? Table
    {
        get => (ReportTable?)GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    public string UnavailableReason
    {
        get => (string)GetValue(UnavailableReasonProperty);
        set => SetValue(UnavailableReasonProperty, value);
    }

    public ReportTableView()
    {
        InitializeComponent();
        RenderTable();
    }

    private void RenderTable()
    {
        if (RowsGrid is null) return;
        RowsGrid.ItemsSource = null;
        RowsGrid.Columns.Clear();
        FooterGrid.Children.Clear();
        FooterGrid.ColumnDefinitions.Clear();
        FooterBorder.Visibility = Visibility.Collapsed;

        var table = Table;
        if (table is null)
        {
            HorizontalScroller.Visibility = Visibility.Collapsed;
            UnavailableText.Text = UnavailableReason;
            UnavailableText.Visibility = Visibility.Visible;
            return;
        }

        if (table.Columns.Count == 0 || table.Rows.Any(row => row.Cells.Count != table.Columns.Count)
            || table.MeanFooter is { } footer && footer.Count != table.Columns.Count)
            throw new ArgumentException("报表列数与数据不一致。", nameof(Table));

        var totalWidth = 0d;
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            if (!double.IsFinite(column.Width) || column.Width <= 0)
                throw new ArgumentException("报表列宽无效。", nameof(Table));
            totalWidth += column.Width;
            RowsGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = column.Header,
                HeaderTemplate = HeaderTemplate(),
                Width = new DataGridLength(column.Width),
                CellTemplate = CellTemplate(i),
                ClipboardContentBinding = new Binding($"Cells[{i}].Text"),
                CanUserSort = false
            });
            FooterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width) });
        }

        // Horizontal scrolling belongs to the outer viewer. Reserve space for the inner
        // vertical scrollbar or its disabled horizontal viewport clips the last score column.
        TableBody.Width = totalWidth + SystemParameters.VerticalScrollBarWidth;
        RowsGrid.ItemsSource = table.Rows;
        if (table.MeanFooter is not null)
        {
            if (table.Columns.Count < 4)
                throw new ArgumentException("固定均分行至少需要前四列。", nameof(Table));
            var label = FooterCell("平均分");
            Grid.SetColumn(label, 0);
            Grid.SetColumnSpan(label, 4);
            FooterGrid.Children.Add(label);
            for (var i = 4; i < table.MeanFooter.Count; i++)
            {
                var value = FooterCell(table.MeanFooter[i]);
                Grid.SetColumn(value, i);
                FooterGrid.Children.Add(value);
            }
            FooterBorder.Visibility = Visibility.Visible;
        }
        UnavailableText.Visibility = Visibility.Collapsed;
        HorizontalScroller.Visibility = Visibility.Visible;
    }

    private static Style CellStyle(int index)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        var red = new DataTrigger
        {
            Binding = new Binding($"Cells[{index}].IsRed"),
            Value = true
        };
        red.Setters.Add(new Setter(TextBlock.ForegroundProperty, ReportRed));
        style.Triggers.Add(red);
        return style;
    }

    private static DataTemplate CellTemplate(int index)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding($"Cells[{index}].Text"));
        text.SetBinding(FrameworkElement.ToolTipProperty, new Binding($"Cells[{index}].Text"));
        text.SetValue(FrameworkElement.StyleProperty, CellStyle(index));
        text.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 8, 0));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new DataTemplate { VisualTree = text };
    }

    private static DataTemplate HeaderTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetBinding(FrameworkElement.ToolTipProperty, new Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        return new DataTemplate { VisualTree = text };
    }

    private static Border FooterCell(string text) => new()
    {
        BorderBrush = Brushes.LightGray,
        BorderThickness = new Thickness(0, 0, 1, 0),
        Child = new TextBlock
        {
            Text = text,
            ToolTip = text,
            Foreground = Brushes.Black,
            Padding = new Thickness(8, 7, 8, 7),
            TextTrimming = TextTrimming.CharacterEllipsis
        }
    };
}
