using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TlbbAgent.Contracts;
using TlbbAgent.Core;

namespace TlbbAgent.App.ViewModels;

public sealed class DisplayLogCollection : ObservableCollection<DisplayLogEntryViewModel>
{
    public void ReplaceAll(IEnumerable<DisplayLogEntryViewModel> entries)
    {
        Items.Clear();
        foreach (var entry in entries)
            Items.Add(entry);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// 日志抽屉的用户投影。窗口句柄、内部模块、原始消息和 DebugDetails 均不进入此类型。
/// </summary>
public sealed class DisplayLogEntryViewModel
{
    public DisplayLogEntryViewModel(LogEntry entry)
    {
        Time = entry.Time;
        Level = entry.Level;
        LevelText = UserLogPrivacyPolicy.LevelText(entry.Level);
        Message = UserLogPrivacyPolicy.TryGetUserMessage(entry, out var message)
            ? message
            : "运行状态已更新。";
    }

    public DateTime Time { get; }
    public LogLevel Level { get; }
    public string LevelText { get; }
    public string Message { get; }

    public string ToExportLine() => $"{Time:HH:mm:ss}  {LevelText,-4}  {Message}";

    public override string ToString() => $"{LevelText}：{Message}";

    public static bool IsUserVisible(LogEntry entry) =>
        UserLogPrivacyPolicy.TryGetUserMessage(entry, out _);
}

public partial class LogPanelViewModel : ObservableObject
{
    private const int MaxVisibleEntries = 800;
    private readonly ILogService _log;
    private readonly Dispatcher? _dispatcher;

    public LogPanelViewModel(ILogService log)
    {
        _log = log;
        _dispatcher = Application.Current?.Dispatcher;
        _log.Appended += OnAppended;
        _log.Cleared += (_, _) => Dispatch(() =>
        {
            Entries.Clear();
            LatestSummary = "暂无运行记录";
        });
        RebuildVisibleEntries();
    }

    public DisplayLogCollection Entries { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _latestSummary = "暂无运行记录";

    private void OnAppended(object? sender, LogEntry entry)
    {
        Dispatch(() =>
        {
            if (!DisplayLogEntryViewModel.IsUserVisible(entry))
                return;

            var display = new DisplayLogEntryViewModel(entry);
            LatestSummary = FormatSummary(display);

            while (Entries.Count >= MaxVisibleEntries)
                Entries.RemoveAt(0);

            Entries.Add(display);
        });
    }

    private void RebuildVisibleEntries()
    {
        Dispatch(() =>
        {
            var visible = _log.Entries
                .Where(DisplayLogEntryViewModel.IsUserVisible)
                .TakeLast(MaxVisibleEntries)
                .Select(entry => new DisplayLogEntryViewModel(entry))
                .ToArray();
            Entries.ReplaceAll(visible);
            LatestSummary = visible.Length == 0
                ? "暂无运行记录"
                : FormatSummary(visible[^1]);
        });
    }

    private static string FormatSummary(DisplayLogEntryViewModel entry) =>
        $"{entry.Time:HH:mm:ss}  {entry.LevelText}  {entry.Message}";

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Clear() => _log.Clear();

    [RelayCommand]
    private void Copy()
    {
        try
        {
            Clipboard.SetText(BuildVisibleExportText());
            LogUserEvent(LogLevel.Info, "运行记录已复制。");
        }
        catch
        {
            // 剪贴板被占用时不向用户暴露系统异常。
        }
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"TLBB-Agent-运行记录-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "文本文件 (*.txt)|*.txt"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            System.IO.File.WriteAllText(dialog.FileName, BuildVisibleExportText(), Encoding.UTF8);
            LogUserEvent(LogLevel.Info, "运行记录已导出。");
        }
        catch
        {
            LogUserEvent(LogLevel.Warn, "运行记录导出失败，请更换保存位置后重试。");
        }
    }

    /// <summary>
    /// 只导出当前抽屉中已经过用户投影的最多 800 条记录。
    /// </summary>
    public string BuildVisibleExportText()
    {
        var sb = new StringBuilder();
        foreach (var entry in Entries)
            sb.AppendLine(entry.ToExportLine());
        return sb.ToString();
    }

    private void LogUserEvent(LogLevel level, string message) =>
        _log.Log(new LogEntry
        {
            Level = level,
            Module = "Log",
            Message = message,
            Audience = LogAudience.User,
            UserSummary = message
        });
}
