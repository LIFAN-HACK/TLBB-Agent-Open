using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TlbbAgent.Contracts;

namespace TlbbAgent.App.ViewModels;

public sealed partial class QuestionBankResultViewModel : ObservableObject
{
    private readonly QuestionBankEntry _entry;

    public QuestionBankResultViewModel(QuestionBankSearchResult result)
    {
        _entry = result.Entry;
        Score = result.Score;
        MatchedText = result.MatchedText;
    }

    public string Id => _entry.Id;
    public string Question => _entry.Question;
    public string Answer => _entry.Answer;
    public string Category => string.IsNullOrWhiteSpace(_entry.Category) ? "-" : _entry.Category;
    public string Tags => string.IsNullOrWhiteSpace(_entry.Tags) ? "-" : _entry.Tags;
    public string Source => string.IsNullOrWhiteSpace(_entry.Source) ? "本地" : _entry.Source;
    public int Score { get; }
    public string MatchedText { get; }
}

public sealed partial class QuestionBankPageViewModel : PageViewModel
{
    private readonly ModuleDescriptor _descriptor;
    private readonly IQuestionBankService _service;
    private readonly IRuntimeDataService _runtimeData;
    private readonly ILogService _log;

    public QuestionBankPageViewModel(ModuleDescriptor descriptor, IQuestionBankService service, IRuntimeDataService runtimeData, ILogService log)
    {
        _descriptor = descriptor;
        _service = service;
        _runtimeData = runtimeData;
        _log = log;
        _service.Changed += (_, _) => UiThread.Dispatch(Refresh);
        _service.Load();
        Refresh();
        DetectEncryptedSample();
    }

    public override string Title => _descriptor.Title;
    public override string Subtitle => _descriptor.Description;
    public int EntryCount => _service.Entries.Count;
    public int ResultCount => Results.Count;
    public string BankSummary => $"题库 {EntryCount} 条 / 结果 {ResultCount} 条";
    public string ImportSummary => HasEncryptedSample ? "需整理" : "本地题库";
    public string EditSummary => string.IsNullOrWhiteSpace(NewQuestion) || string.IsNullOrWhiteSpace(NewAnswer)
        ? "题目和答案必填"
        : "可保存";
    public string ReadySummary => EntryCount > 0 ? "可查询" : "空题库";
    public bool IsReady => EntryCount > 0;
    public ObservableCollection<QuestionBankResultViewModel> Results { get; } = new();

    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private string _newQuestion = "";
    [ObservableProperty] private string _newAnswer = "";
    [ObservableProperty] private string _newCategory = "";
    [ObservableProperty] private string _newTags = "";
    [ObservableProperty] private string _statusText = "JSON / CSV / TSV / TXT";
    [ObservableProperty] private bool _hasEncryptedSample;

    partial void OnQueryTextChanged(string value) => Search();

    [RelayCommand]
    private void Search()
    {
        Results.Clear();
        foreach (var result in _service.Search(QueryText, 80))
            Results.Add(new QuestionBankResultViewModel(result));

        StatusText = string.IsNullOrWhiteSpace(QueryText)
            ? $"当前题库 {EntryCount} 条。"
            : $"查询到 {Results.Count} 条。";
        RefreshSummary();
    }

    [RelayCommand]
    private void AddEntry()
    {
        if (string.IsNullOrWhiteSpace(NewQuestion) || string.IsNullOrWhiteSpace(NewAnswer))
        {
            StatusText = "题目和答案不能为空。";
            return;
        }

        _service.AddOrUpdate(new QuestionBankEntry
        {
            Question = NewQuestion,
            Answer = NewAnswer,
            Category = NewCategory,
            Tags = NewTags,
            Source = "手动"
        });

        NewQuestion = "";
        NewAnswer = "";
        NewCategory = "";
        NewTags = "";
        QueryText = "";
        StatusText = "已保存。";
        Refresh();
    }

    [RelayCommand]
    private void Remove(QuestionBankResultViewModel? item)
    {
        if (item is null)
            return;

        if (_service.Remove(item.Id))
        {
            StatusText = "已删除。";
            Refresh();
        }
    }

    [RelayCommand]
    private void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "载入题库",
            Filter = "题库文件 (*.json;*.csv;*.tsv;*.txt)|*.json;*.csv;*.tsv;*.txt|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = _service.ImportFile(dialog.FileName);
            StatusText = $"已载入：{result.Imported + result.Updated}。";
            Refresh();
        }
        catch
        {
            StatusText = "载入失败。";
            _log.Error("QuestionBank", StatusText);
        }
    }

    [RelayCommand]
    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出题库",
            FileName = $"TLBB-Agent-题库-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "题库 JSON (*.json)|*.json|所有文件 (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _service.ExportJson(dialog.FileName);
            StatusText = $"已导出：{EntryCount}。";
        }
        catch
        {
            StatusText = "导出失败。";
            _log.Error("QuestionBank", StatusText);
        }
    }

    [RelayCommand]
    private void CheckEncryptedSample()
    {
        var path = _runtimeData.EnumerateFiles("question-bank", "*.enc", recursive: false).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText = "未找到可检查文件。";
            return;
        }

        var result = _service.ImportFile(path);
        StatusText = result.Message;
        HasEncryptedSample = result.UnsupportedEncrypted;
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(EntryCount));
        RefreshSummary();
        Search();
    }

    private void DetectEncryptedSample()
    {
        var importedRoot = _runtimeData.FindRoot();
        HasEncryptedSample = importedRoot is not null && _service.HasEncryptedSample(importedRoot);
        if (HasEncryptedSample && _service.Entries.Count == 0)
            StatusText = "格式不支持。";
        RefreshSummary();
    }

    partial void OnNewQuestionChanged(string value) => RefreshSummary();

    partial void OnNewAnswerChanged(string value) => RefreshSummary();

    partial void OnHasEncryptedSampleChanged(bool value) => RefreshSummary();

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(BankSummary));
        OnPropertyChanged(nameof(ImportSummary));
        OnPropertyChanged(nameof(EditSummary));
        OnPropertyChanged(nameof(ReadySummary));
        OnPropertyChanged(nameof(IsReady));
    }
}
