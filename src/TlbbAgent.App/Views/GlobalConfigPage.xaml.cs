using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TlbbAgent.App.ViewModels;

namespace TlbbAgent.App.Views;

public partial class GlobalConfigPage : UserControl
{
    private Point _skillDragStart;

    public GlobalConfigPage() => InitializeComponent();

    private void SkillDragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _skillDragStart = e.GetPosition(this);
    }

    private void SkillDragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: RoleSkillPresetViewModel item })
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _skillDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _skillDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
    }

    private void SkillList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(RoleSkillPresetViewModel))
            || e.Data.GetData(typeof(RoleSkillPresetViewModel)) is not RoleSkillPresetViewModel source
            || DataContext is not GlobalConfigPageViewModel viewModel)
        {
            return;
        }

        var target = FindSkillItem(e.OriginalSource as DependencyObject);
        viewModel.MoveSkill(source, target);
        e.Handled = true;
    }

    private void SkillList_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(RoleSkillPresetViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static RoleSkillPresetViewModel? FindSkillItem(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: RoleSkillPresetViewModel item })
                return item;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
