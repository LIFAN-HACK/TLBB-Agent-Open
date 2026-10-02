using System.Text;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class QuestionBankService : IQuestionBankService
{
    private const string Module = "QuestionBank";
    private readonly IAppInitService _init;
    private readonly ILogService _log;
    private readonly List<QuestionBankEntry> _entries = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public QuestionBankService(IAppInitService init, ILogService log)
    {
        _init = init;
        _log = log;
    }

    public IReadOnlyList<QuestionBankEntry> Entries => _entries;
    public event EventHandler? Changed;

    private string StorePath => Path.Combine(_init.Paths.Data, "question-bank.json");

    public void Load()
    {
        _entries.Clear();
        if (!File.Exists(StorePath))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var json = File.ReadAllText(StorePath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<List<QuestionBankEntry>>(json, JsonOptions) ?? new();
            _entries.AddRange(loaded.Where(IsValid).Select(Normalize));
            _log.Info(Module, $"题库已加载：{_entries.Count} 条。");
        }
        catch
        {
            _log.Error(Module, "题库加载失败。");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath) ?? _init.Paths.Data);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(_entries, JsonOptions), new UTF8Encoding(false));
        _log.Info(Module, $"题库已保存：{_entries.Count} 条。");
    }

    public QuestionBankEntry AddOrUpdate(QuestionBankEntry entry)
    {
        var normalized = Normalize(entry);
        if (!IsValid(normalized))
            throw new ArgumentException("题目和答案不能为空。", nameof(entry));

        var existing = _entries.FirstOrDefault(e => SameQuestion(e.Question, normalized.Question));
        if (existing is null)
        {
            _entries.Add(normalized);
            existing = normalized;
        }
        else
        {
            existing.Answer = normalized.Answer;
            existing.Category = normalized.Category;
            existing.Tags = normalized.Tags;
            existing.Source = normalized.Source;
            existing.UpdatedAt = DateTime.Now;
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return existing;
    }

    public bool Remove(string id)
    {
        var removed = _entries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            return false;

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<QuestionBankSearchResult> Search(string query, int limit = 20)
    {
        query = NormalizeText(query);
        if (query.Length == 0)
            return _entries
                .OrderByDescending(e => e.UpdatedAt)
                .Take(limit)
                .Select(e => new QuestionBankSearchResult(e, 0, e.Question))
                .ToList();

        return _entries
            .Select(e => Score(e, query))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Entry.UpdatedAt)
            .Take(limit)
            .ToList();
    }

    public QuestionBankImportResult ImportFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException("题库文件不存在。", full);

        if (IsEncryptedSample(full))
        {
            var message = "文件格式不支持，请载入 JSON/CSV/TXT 题库。";
            _log.Warn(Module, message);
            return new QuestionBankImportResult(full, 0, 0, 0, true, message);
        }

        var incoming = ParsePlainFile(full).Where(IsValid).Select(Normalize).ToList();
        var imported = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var entry in incoming)
        {
            var existing = _entries.FirstOrDefault(e => SameQuestion(e.Question, entry.Question));
            if (existing is null)
            {
                _entries.Add(entry);
                imported++;
            }
            else if (!string.Equals(existing.Answer, entry.Answer, StringComparison.Ordinal)
                     || !string.Equals(existing.Category, entry.Category, StringComparison.Ordinal)
                     || !string.Equals(existing.Tags, entry.Tags, StringComparison.Ordinal))
            {
                existing.Answer = entry.Answer;
                existing.Category = entry.Category;
                existing.Tags = entry.Tags;
                existing.Source = entry.Source;
                existing.UpdatedAt = DateTime.Now;
                updated++;
            }
            else
            {
                skipped++;
            }
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        var msg = $"题库已载入：新增 {imported}，更新 {updated}，跳过 {skipped}。";
        _log.Info(Module, msg);
        return new QuestionBankImportResult(full, imported, updated, skipped, false, msg);
    }

    public void ExportJson(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? Environment.CurrentDirectory);
        File.WriteAllText(full, JsonSerializer.Serialize(_entries, JsonOptions), new UTF8Encoding(false));
        _log.Info(Module, $"题库已导出：{_entries.Count} 条。");
    }

    public bool HasEncryptedSample(string importedRoot)
    {
        var dir = Path.Combine(importedRoot, "question-bank");
        return Directory.Exists(dir)
               && Directory.EnumerateFiles(dir, "*.enc", SearchOption.TopDirectoryOnly).Any(IsEncryptedSample);
    }

    private static QuestionBankSearchResult Score(QuestionBankEntry entry, string query)
    {
        var question = NormalizeText(entry.Question);
        var answer = NormalizeText(entry.Answer);
        var tags = NormalizeText(entry.Tags);
        var category = NormalizeText(entry.Category);

        if (question == query)
            return new(entry, 1000, entry.Question);
        if (question.Contains(query, StringComparison.Ordinal))
            return new(entry, 700 + Math.Min(200, query.Length * 5), entry.Question);
        if (answer.Contains(query, StringComparison.Ordinal))
            return new(entry, 420, entry.Answer);
        if (tags.Contains(query, StringComparison.Ordinal))
            return new(entry, 320, entry.Tags);
        if (category.Contains(query, StringComparison.Ordinal))
            return new(entry, 240, entry.Category);

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length > 1)
        {
            var hit = terms.Count(t => question.Contains(t, StringComparison.Ordinal));
            if (hit > 0)
                return new(entry, hit * 120, entry.Question);
        }

        return new(entry, 0, "");
    }

    private static IReadOnlyList<QuestionBankEntry> ParsePlainFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var text = ReadText(path);

        if (ext == ".json")
            return ParseJson(text, path);
        if (ext == ".csv")
            return ParseDelimited(text, ',', path);
        if (ext == ".tsv")
            return ParseDelimited(text, '\t', path);

        return ParseTextLines(text, path);
    }

    private static IReadOnlyList<QuestionBankEntry> ParseJson(string text, string source)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("entries", out var entries) ? entries
            : root.TryGetProperty("questions", out var questions) ? questions
            : default;

        if (array.ValueKind != JsonValueKind.Array)
            return Array.Empty<QuestionBankEntry>();

        var result = new List<QuestionBankEntry>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var q = GetString(item, "question", "q", "title", "题目", "问题");
            var a = GetString(item, "answer", "a", "答案", "正确答案");
            var c = GetString(item, "category", "type", "分类", "类别");
            var tags = GetString(item, "tags", "tag", "关键词");
            result.Add(new QuestionBankEntry { Question = q, Answer = a, Category = c, Tags = tags, Source = PublicSource(source) });
        }

        return result;
    }

    private static IReadOnlyList<QuestionBankEntry> ParseDelimited(string text, char delimiter, string source)
    {
        var rows = SplitLines(text).Select(line => SplitCsv(line, delimiter).ToArray()).Where(r => r.Length >= 2).ToList();
        if (rows.Count == 0)
            return Array.Empty<QuestionBankEntry>();

        var start = 0;
        var qIndex = 0;
        var aIndex = 1;
        var cIndex = -1;
        var tIndex = -1;

        var header = rows[0].Select(NormalizeText).ToArray();
        if (header.Any(h => h is "question" or "题目" or "问题") && header.Any(h => h is "answer" or "答案" or "正确答案"))
        {
            start = 1;
            qIndex = Array.FindIndex(header, h => h is "question" or "题目" or "问题");
            aIndex = Array.FindIndex(header, h => h is "answer" or "答案" or "正确答案");
            cIndex = Array.FindIndex(header, h => h is "category" or "type" or "分类" or "类别");
            tIndex = Array.FindIndex(header, h => h is "tags" or "tag" or "关键词");
        }

        var result = new List<QuestionBankEntry>();
        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            result.Add(new QuestionBankEntry
            {
                Question = GetAt(row, qIndex),
                Answer = GetAt(row, aIndex),
                Category = GetAt(row, cIndex),
                Tags = GetAt(row, tIndex),
                Source = PublicSource(source)
            });
        }

        return result;
    }

    private static IReadOnlyList<QuestionBankEntry> ParseTextLines(string text, string source)
    {
        var result = new List<QuestionBankEntry>();
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var parts = SplitQuestionAnswer(line);
            if (parts is null)
                continue;

            result.Add(new QuestionBankEntry
            {
                Question = parts.Value.Question,
                Answer = parts.Value.Answer,
                Source = PublicSource(source)
            });
        }

        return result;
    }

    private static (string Question, string Answer)? SplitQuestionAnswer(string line)
    {
        foreach (var token in new[] { "\t", "=", "|", "：", ":" })
        {
            var index = line.IndexOf(token, StringComparison.Ordinal);
            if (index <= 0 || index >= line.Length - token.Length)
                continue;

            return (line[..index].Trim(), line[(index + token.Length)..].Trim());
        }

        return null;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static IEnumerable<string> SplitCsv(string line, char delimiter)
    {
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == delimiter && !quoted)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        yield return sb.ToString().Trim();
    }

    private static string GetAt(IReadOnlyList<string> values, int index) =>
        index >= 0 && index < values.Count ? values[index].Trim() : "";

    private static string GetString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                return value.ValueKind == JsonValueKind.Array
                    ? string.Join(";", value.EnumerateArray().Select(v => v.ToString()))
                    : value.ToString();
        }

        return "";
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    private static bool IsEncryptedSample(string path) =>
        Path.GetExtension(path).Equals(".enc", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".db", StringComparison.OrdinalIgnoreCase);

    private static QuestionBankEntry Normalize(QuestionBankEntry entry) =>
        new()
        {
            Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N")[..10] : entry.Id,
            Question = entry.Question.Trim(),
            Answer = entry.Answer.Trim(),
            Category = entry.Category.Trim(),
            Tags = entry.Tags.Trim(),
            Source = string.IsNullOrWhiteSpace(entry.Source) ? "本地" : entry.Source.Trim(),
            UpdatedAt = entry.UpdatedAt == default ? DateTime.Now : entry.UpdatedAt
        };

    private static bool IsValid(QuestionBankEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Question)
        && !string.IsNullOrWhiteSpace(entry.Answer);

    private static bool SameQuestion(string a, string b) =>
        string.Equals(NormalizeText(a), NormalizeText(b), StringComparison.Ordinal);

    private static string NormalizeText(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));

    private static string PublicSource(string path) => "导入";
}
