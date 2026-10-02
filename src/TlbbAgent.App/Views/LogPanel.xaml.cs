using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TlbbAgent.App.ViewModels;

namespace TlbbAgent.App.Views;

public partial class LogPanel : UserControl
{
    private LogPanelViewModel? _viewModel;

    public LogPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach(DataContext);
        DataContextChanged += (_, e) => Attach(e.NewValue);
    }

    private void Attach(object? dataContext)
    {
        if (ReferenceEquals(_viewModel, dataContext))
            return;

        if (_viewModel is not null)
        {
            _viewModel.Entries.CollectionChanged -= OnEntriesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = dataContext as LogPanelViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Entries.CollectionChanged += OnEntriesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            QueueScrollToLatest();
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
            QueueScrollToLatest();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogPanelViewModel.IsExpanded))
            QueueScrollToLatest();
    }

    private void QueueScrollToLatest()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollToLatest));
    }

    private void ScrollToLatest()
    {
        if (_viewModel is null || _viewModel.Entries.Count == 0)
            return;

        var latest = _viewModel.Entries[^1];
        EntriesList.ScrollIntoView(latest);
        EntriesList.UpdateLayout();
        FindVisualChild<ScrollViewer>(EntriesList)?.ScrollToEnd();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}
