using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TlbbAgent.App.ViewModels;

namespace TlbbAgent.App.Views;

public partial class TaskAutomationPage : UserControl
{
    private Point _dragStart;

    public TaskAutomationPage() => InitializeComponent();

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
    }

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: TaskAutomationItemViewModel item })
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, item, DragDropEffects.Move);
    }

    private void TaskList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(TaskAutomationItemViewModel))
            || e.Data.GetData(typeof(TaskAutomationItemViewModel)) is not TaskAutomationItemViewModel source
            || DataContext is not TaskAutomationPageViewModel viewModel)
        {
            return;
        }

        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext
                     as TaskAutomationItemViewModel;
        viewModel.MoveTask(source, target);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
