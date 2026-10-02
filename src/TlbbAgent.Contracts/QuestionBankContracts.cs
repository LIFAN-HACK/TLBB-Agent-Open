namespace TlbbAgent.Contracts;

public sealed class QuestionBankEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public string Category { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Source { get; set; } = "本地";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed record QuestionBankSearchResult(
    QuestionBankEntry Entry,
    int Score,
    string MatchedText);

public sealed record QuestionBankImportResult(
    string Path,
    int Imported,
    int Updated,
    int Skipped,
    bool UnsupportedEncrypted,
    string Message);

public interface IQuestionBankService
{
    IReadOnlyList<QuestionBankEntry> Entries { get; }
    event EventHandler? Changed;

    void Load();
    void Save();
    QuestionBankEntry AddOrUpdate(QuestionBankEntry entry);
    bool Remove(string id);
    void Clear();
    IReadOnlyList<QuestionBankSearchResult> Search(string query, int limit = 20);
    QuestionBankImportResult ImportFile(string path);
    void ExportJson(string path);
    bool HasEncryptedSample(string importedRoot);
}
