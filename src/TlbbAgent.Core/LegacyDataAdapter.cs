using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class LegacyDataAdapter(ILogService log) : ILegacyDataAdapter
{
    private const string Module = "DataImport";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] AuthKeywords =
    [
        "root",
        "账号",
        "帐号",
        "密码",
        "会员",
        "授权",
        "登录",
        "登陆",
        "注册",
        "充值",
        "绑定",
        "改密",
        "到期",
        "过期",
        "P_" + "UserLogin",
        "xx" + ".dll",
        "zhucema",
        "Cloud"
    ];

    public void RegisterImport(string sourceModule, string sourcePath, string dataType, string targetModule, string notes) =>
        log.Info(Module, "数据已登记。");

    public LegacyImportResult ImportKnownData(LegacyImportOptions options)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var sourceRoot = Path.GetFullPath(options.SourceRoot);
        var importedRoot = Path.GetFullPath(options.ImportedRoot);
        Directory.CreateDirectory(importedRoot);

        var items = new List<LegacyImportIndexItem>();
        var auditItems = new List<LegacyImportIndexItem>();
        foreach (var spec in BuildSpecs(sourceRoot))
        {
            var imported = ImportSpec(spec, importedRoot, options.Overwrite);
            items.Add(imported.Public);
            auditItems.Add(imported.Audit);
        }

        var indexPath = Path.Combine(importedRoot, "index.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(items, JsonOptions), new UTF8Encoding(false));
        var internalSourceIndexPath = Path.Combine(importedRoot, "internal-source-index.json");
        File.WriteAllText(internalSourceIndexPath, JsonSerializer.Serialize(auditItems, JsonOptions), new UTF8Encoding(false));

        var result = new LegacyImportResult
        {
            SourceRoot = sourceRoot,
            ImportedRoot = importedRoot,
            TotalItems = items.Count,
            CopiedItems = auditItems.Count(i => i.Status is "copied" or "copied-sanitized"),
            SanitizedItems = auditItems.Count(i => i.Status == "copied-sanitized"),
            MissingItems = auditItems.Count(i => i.Status == "missing"),
            DirectoryItems = auditItems.Count(i => i.DataType.EndsWith("-directory", StringComparison.OrdinalIgnoreCase)),
            IndexPath = indexPath,
            InternalSourceIndexPath = internalSourceIndexPath,
            Items = items
        };

        log.Info(Module, $"数据准备完成：{result.CopiedItems}/{result.TotalItems}，缺失 {result.MissingItems}");
        return result;
    }

    private static LegacyImportPair ImportSpec(LegacyImportSpec spec, string importedRoot, bool overwrite)
    {
        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        var source = spec.SourcePath;
        var target = Path.Combine(importedRoot, spec.TargetRelativePath);
        var indexTarget = ToPublicTargetPath(spec.TargetRelativePath);

        if (!File.Exists(source) && !Directory.Exists(source))
        {
            return spec.ToIndexPair(indexTarget, "missing", now, null, null, null, null);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? importedRoot);

        if (spec.IsDirectory)
        {
            var count = CopyTextDirectory(source, target, overwrite);
            return spec.ToIndexPair(indexTarget, "copied", now, null, null, count, null);
        }

        if (spec.SkipIfAuthReference && FileContainsAuthReference(source))
        {
            if (File.Exists(target))
                File.Delete(target);
            return spec.ToIndexPair(indexTarget, "skipped-auth-reference", now, new FileInfo(source).Length, null, null, null);
        }

        if (spec.SanitizeIni)
        {
            SanitizeIni(source, target);
            return spec.ToIndexPair(indexTarget, "copied-sanitized", now, new FileInfo(source).Length, new FileInfo(target).Length, null, Sha256(target));
        }

        if (overwrite || !File.Exists(target))
            File.Copy(source, target, overwrite: true);
        File.SetLastWriteTime(target, File.GetLastWriteTime(source));

        return spec.ToIndexPair(indexTarget, "copied", now, new FileInfo(source).Length, new FileInfo(target).Length, null, Sha256(target));
    }

    private static string ToPublicTargetPath(string targetRelativePath) =>
        Path.Combine("data", "imported", targetRelativePath).Replace('\\', '/');

    private static int CopyTextDirectory(string sourceDir, string targetDir, bool overwrite)
    {
        Directory.CreateDirectory(targetDir);
        var count = 0;
        foreach (var source in Directory.EnumerateFiles(sourceDir, "*.txt", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, source);
            var target = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? targetDir);
            if (overwrite || !File.Exists(target))
                File.Copy(source, target, overwrite: true);
            File.SetLastWriteTime(target, File.GetLastWriteTime(source));
            count++;
        }
        return count;
    }

    private static void SanitizeIni(string source, string target)
    {
        var encoding = Encoding.GetEncoding("GB18030");
        var lines = File.ReadAllLines(source, encoding);
        var output = new List<string>();
        var inRootSection = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                var section = line.Trim('[', ']', ' ', '\t');
                inRootSection = section.Equals("root", StringComparison.OrdinalIgnoreCase);
                if (inRootSection)
                    continue;
            }

            if (inRootSection || ContainsAuthKeyword(line))
                continue;

            output.Add(raw);
        }

        File.WriteAllLines(target, output, new UTF8Encoding(false));
    }

    private static bool ContainsAuthKeyword(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return AuthKeywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool FileContainsAuthReference(string path)
    {
        var ext = Path.GetExtension(path);
        if (!ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".ini", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        var encoding = Encoding.GetEncoding("GB18030");
        return File.ReadLines(path, encoding).Any(ContainsAuthKeyword);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static IReadOnlyList<LegacyImportSpec> BuildSpecs(string root)
    {
        var specs = new List<LegacyImportSpec>
        {
            new("核心字库", @"C:\sys\aj.txt", @"ocr\core-aj.txt", "ocr-dictionary-aj", "TemplateOcrService", "P0", "核心字库；只复制，不修改原文件。"),

            new("坐标源A", Path.Combine(root, @"马2.31\bmp\zk.txt"), @"ocr\horse-zk.txt", "ocr-dictionary-zk", "TemplateOcrService / 马贼模式", "P0", "马贼与坐标识别字库。"),
            new("坐标源A", Path.Combine(root, @"马2.31\bmp\数字.txt"), @"ocr\horse-number.txt", "ocr-dictionary-number", "TemplateOcrService", "P0", "数字点阵字库。"),
            new("坐标源A", Path.Combine(root, @"马2.31\bmp\数字1.txt"), @"ocr\horse-number-alt.txt", "ocr-dictionary-number", "TemplateOcrService", "P0", "数字点阵字库。"),
            new("坐标源A", Path.Combine(root, @"马2.31\自定义坐标.ini"), @"routes\route-points-a.ini", "route-points", "挂机返点 / 马贼模式 / 反贼 / 和尚", "P0", "多地图坐标路线。"),
            new("坐标源A", Path.Combine(root, @"马2.31\bmp\config.ini"), @"config-samples\horse-a.sanitized.ini", "config-sample-sanitized", "ProfileConfigService / HorseBanditModeController", "P0", "配置。", SanitizeIni: true),
            new("坐标源A", Path.Combine(root, @"马2.31\保留物品.txt"), @"text-samples\keep-items-a.txt", "text-sample", "清包 / 背包处理", "P0", "清包保留物品样本。"),

            new("跟随源A", Path.Combine(root, @"队员马 - 双开版\bmp\zk.txt"), @"ocr\follow-zk.txt", "ocr-dictionary-zk", "TemplateOcrService / 马贼模式", "P0", "跟随与马贼相关字库。"),
            new("跟随源A", Path.Combine(root, @"队员马 - 双开版\bmp\数字.txt"), @"ocr\follow-number.txt", "ocr-dictionary-number", "TemplateOcrService", "P0", "数字点阵字库。"),
            new("跟随源A", Path.Combine(root, @"队员马 - 双开版\bmp\数字1.txt"), @"ocr\follow-number-alt.txt", "ocr-dictionary-number", "TemplateOcrService", "P0", "数字点阵字库。"),
            new("跟随源A", Path.Combine(root, @"队员马 - 双开版\bmp\config.ini"), @"config-samples\follow-a.sanitized.ini", "config-sample-sanitized", "ProfileConfigService / HorseBanditModeController", "P0", "配置。", SanitizeIni: true),

            new("任务源A", Path.Combine(root, @"单师门普通版 -1.37\bmp\001.txt"), @"ocr\task-001.txt", "ocr-dictionary-001", "TemplateOcrService / 单师门(P1)", "P1", "001 字段顺序字库。"),
            new("任务源A", Path.Combine(root, @"单师门普通版 -1.37\bmp\config.ini"), @"config-samples\task-a.sanitized.ini", "config-sample-sanitized", "ProfileConfigService / 单师门(P1)", "P1", "配置。", SanitizeIni: true),
            new("任务源A", Path.Combine(root, @"单师门普通版 -1.37\存放物品.txt"), @"text-samples\task-store-items.txt", "text-sample", "单师门 / 背包处理(P1)", "P1", "师门物品处理样本。"),
            new("任务源A", Path.Combine(root, @"单师门普通版 -1.37\交易物品.txt"), @"text-samples\task-trade-items.txt", "text-sample", "单师门 / 交易处理(P1)", "P1", "师门交易物品样本。"),

            new("图形源A", Path.Combine(root, @"挖图-1.91\bmp\001.txt"), @"ocr\map-001.txt", "ocr-dictionary-001", "TemplateOcrService / 挖图(P1)", "P1", "001 字段顺序字库。"),
            new("图形源A", Path.Combine(root, @"挖图-1.91\bmp\config.ini"), @"config-samples\map-a.sanitized.ini", "config-sample-sanitized", "ProfileConfigService / 挖图(P1)", "P1", "配置。", SanitizeIni: true),

            new("采集源A", Path.Combine(root, @"挖矿2.15\bmp\zk.txt"), @"ocr\mine-zk.txt", "ocr-dictionary-zk", "TemplateOcrService / 挖矿(P1)", "P1", "挖矿识别字库。"),
            new("采集源A", Path.Combine(root, @"挖矿2.15\bmp\数字.txt"), @"ocr\mine-number.txt", "ocr-dictionary-number", "TemplateOcrService", "P0", "数字点阵字库。"),
            new("采集源A", Path.Combine(root, @"挖矿2.15\bmp\数字1.txt"), @"ocr\mine-number-alt.txt", "ocr-dictionary-number", "TemplateOcrService", "P0", "数字点阵字库。"),
            new("采集源A", Path.Combine(root, @"挖矿2.15\bmp\config.ini"), @"config-samples\mine-a.sanitized.ini", "config-sample-sanitized", "ProfileConfigService / 挖矿(P1)", "P1", "配置。", SanitizeIni: true),

            new("生活源A", Path.Combine(root, @"生活王1.4.0\A窗口\pz.ini"), @"life\life-a.ini", "life-config", "生活(P1)", "P1", "采集、挖矿、采药映射与技能间隔参数。"),
            new("生活源A", Path.Combine(root, @"生活王1.4.0\A窗口\图_pic\fuben.txt"), @"ocr\dungeon-a.txt", "ocr-dictionary-fuben", "TemplateOcrService / 副本", "P0", "副本/NPC 字库。"),
            new("生活源A", Path.Combine(root, @"生活王1.4.0\B窗口\图_pic\fuben.txt"), @"ocr\dungeon-b.txt", "ocr-dictionary-fuben", "TemplateOcrService / 副本", "P0", "B 窗口副本/NPC 字库。"),
            new("生活源A", Path.Combine(root, @"生活王1.4.0\A窗口\图_pic\无量山.txt"), @"ocr\map-sample-a.txt", "ocr-dictionary-map", "TemplateOcrService / 生活(P1)", "P1", "地图/场景识别样本。"),
            new("生活源A", Path.Combine(root, @"生活王1.4.0\A窗口\挖矿采药自填坐标"), @"routes\life-a", "route-points-directory", "挖矿 / 采药 / 采集(P1)", "P1", "坐标文本目录，按地图拆分；只复制 txt 文件。", IsDirectory: true),
            new("生活源A", Path.Combine(root, @"生活王1.4.0\B窗口\挖矿采药自填坐标"), @"routes\life-b", "route-points-directory", "挖矿 / 采药 / 采集(P1)", "P1", "B 窗口坐标文本目录，按地图拆分；只复制 txt 文件。", IsDirectory: true),

            new("题库源A", Path.Combine(root, @"授权答题\bmp\bmp.db"), @"question-bank\question-bank-a.db.enc", "question-bank-encrypted", "答题题库(P1)", "P1", "加密/非标准题库文件，仅作为自有题库后续研究样本。"),
            new("题库源A", Path.Combine(root, @"授权答题\bmp\config.ini"), @"config-samples\question-a.sanitized.ini", "config-sample-sanitized", "答题题库(P1)", "P1", "配置。", SanitizeIni: true),

            new("跑商源A", Path.Combine(root, @"跑商OL\5.txt"), @"trade\trade-seed-a.txt", "trade-route-seed", "跑商(P1)", "P1", "跑商模块文本样本；敏感组件引用会跳过，不复制 EXE 或组件。", SkipIfAuthReference: true),
        };

        return specs;
    }

    private sealed record LegacyImportSpec(
        string SourceModule,
        string SourcePath,
        string TargetRelativePath,
        string DataType,
        string TargetModule,
        string Priority,
        string Notes,
        bool SanitizeIni = false,
        bool IsDirectory = false,
        bool SkipIfAuthReference = false)
    {
        public LegacyImportPair ToIndexPair(
            string targetPath,
            string status,
            string importedAt,
            long? sourceBytes,
            long? targetBytes,
            int? copiedFileCount,
            string? targetSha256)
        {
            var publicItem = new LegacyImportIndexItem
            {
                SourceModule = "运行数据",
                SourcePath = "",
                TargetPath = targetPath,
                DataType = DataType,
                TargetModule = "TLBB Agent",
                Priority = Priority,
                Status = PublicStatus(status),
                Notes = PublicNotes(status),
                SourceBytes = sourceBytes,
                TargetBytes = targetBytes,
                CopiedFileCount = copiedFileCount,
                TargetSha256 = targetSha256,
                ImportedAt = importedAt
            };

            var auditItem = new LegacyImportIndexItem
            {
                SourceModule = SourceModule,
                SourcePath = SourcePath,
                TargetPath = targetPath,
                DataType = DataType,
                TargetModule = TargetModule,
                Priority = Priority,
                Status = status,
                Notes = Notes,
                SourceBytes = sourceBytes,
                TargetBytes = targetBytes,
                CopiedFileCount = copiedFileCount,
                TargetSha256 = targetSha256,
                ImportedAt = importedAt
            };

            return new LegacyImportPair(publicItem, auditItem);
        }

        private static string PublicStatus(string status) =>
            status switch
            {
                "copied" or "copied-sanitized" => "available",
                "skipped-auth-reference" => "skipped",
                _ => status
            };

        private static string PublicNotes(string status) =>
            status switch
            {
                "missing" => "未找到数据。",
                "skipped-auth-reference" => "已跳过。",
                _ => "运行数据。"
            };
    }

    private sealed record LegacyImportPair(LegacyImportIndexItem Public, LegacyImportIndexItem Audit);
}
