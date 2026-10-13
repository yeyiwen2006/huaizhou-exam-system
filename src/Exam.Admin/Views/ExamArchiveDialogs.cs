using System.Windows;
using System.Windows.Controls;
using Exam.Admin.Services;
using Exam.Contracts.Archives;

namespace Exam.Admin.Views;

public static class ExamArchiveDialogs
{
    public static Task<AddExamChoice?> ShowAddAsync(Window owner)
    {
        var (dialog, body) = Create(owner, "添加考试");
        AddExamChoice? choice = null;
        AddText(body, "可以新建一场考试，也可以从此前导出的本地目录导入考试副本。");
        AddButton(dialog, body, "新建考试", "Btn.Primary", () => { choice = AddExamChoice.New; dialog.Close(); });
        AddButton(dialog, body, "从目录导入考试数据", "Btn.Secondary", () => { choice = AddExamChoice.Import; dialog.Close(); });
        AddCancel(dialog, body);
        dialog.ShowDialog();
        return Task.FromResult(choice);
    }

    public static Task<ArchiveImportDecision?> ShowConflictAsync(Window owner, ExamArchivePreview preview)
    {
        var (dialog, body) = Create(owner, "发现同一场考试");
        ArchiveImportDecision? choice = null;
        var selected = preview.Conflicts[0];
        var explanation = AddText(body, $"名为“{selected.ExamName}”的考试与此归档属于同一场考试。");
        if (preview.Conflicts.Count > 1)
        {
            AddText(body, "目标服务器上有多个同场副本；需要覆盖时，请选择其中一场：");
            var targets = new ComboBox { ItemsSource = preview.Conflicts, DisplayMemberPath = nameof(ExamArchiveConflict.ExamName),
                SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 8), MinWidth = 380 };
            targets.SelectionChanged += (_, _) =>
            {
                if (targets.SelectedItem is not ExamArchiveConflict target) return;
                selected = target;
                explanation.Text = $"名为“{target.ExamName}”的考试与此归档属于同一场考试。";
            };
            body.Children.Add(targets);
        }
        AddText(body, "覆盖会替换目标服务器上所选考试的全部本场数据；作为新副本导入会保留已有考试并自动区分名称。来源服务器上的考试与考生作答保持不变，导入副本使用本服务器的独立登录。");
        AddButton(dialog, body, "覆盖原考试", "Btn.Danger", () =>
        { choice = new(ArchiveImportChoice.Overwrite, selected); dialog.Close(); });
        AddButton(dialog, body, "作为新副本导入", "Btn.Primary", () =>
        { choice = new(ArchiveImportChoice.Copy); dialog.Close(); });
        AddButton(dialog, body, "跳过该考试导入", "Btn.Secondary", () =>
        { choice = new(ArchiveImportChoice.Skip); dialog.Close(); });
        AddCancel(dialog, body);
        dialog.ShowDialog();
        return Task.FromResult(choice);
    }

    private static (Window Dialog, StackPanel Body) Create(Window owner, string title)
    {
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = 540, SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height * .9, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        dialog.SetResourceReference(Control.BackgroundProperty, "Brush.Surface");
        dialog.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
        dialog.SetResourceReference(Control.FontFamilyProperty, "Font.Family");
        var body = new StackPanel { Margin = new Thickness(24) };
        var heading = new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        body.Children.Add(heading);
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return (dialog, body);
    }

    private static TextBlock AddText(Panel body, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 4, 0, 8) };
        body.Children.Add(block);
        return block;
    }
    private static void AddButton(Window dialog, Panel body, string text, string style, Action action)
    {
        var button = new Button { Content = text, Style = (Style)dialog.FindResource(style), Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 40 };
        button.Click += (_, args) => { args.Handled = true; action(); };
        body.Children.Add(button);
    }
    private static void AddCancel(Window dialog, Panel body)
    {
        var button = new Button { Content = "取消", Style = (Style)dialog.FindResource("Btn.Secondary"),
            IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 88, Margin = new Thickness(0, 18, 0, 0) };
        button.Click += (_, _) => dialog.Close();
        body.Children.Add(button);
    }
}
