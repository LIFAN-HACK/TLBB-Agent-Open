using System.Text;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class BagCleanupReportService : IBagCleanupReportService
{
    public string BuildTextReport(BagCleanupResult result, BagCleanupConfig config)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# TLBB Agent 清包候选报告");
        builder.AppendLine($"生成时间\t{DateTimeOffset.Now:O}");
        builder.AppendLine($"扫描状态\t{(result.Success ? "成功" : "失败")}");
        builder.AppendLine($"执行动作\t{(result.Executed ? "已执行" : "未执行")}");
        builder.AppendLine($"清包模式\t{config.Mode}");
        builder.AppendLine($"实际出售开关\t{config.AllowActualSell}");
        builder.AppendLine($"安全模式\t{Clean(result.Safety.Mode)}");
        builder.AppendLine($"只读预览\t{result.Safety.DryRun}");
        builder.AppendLine($"发送输入\t{result.Safety.SendsInput}");
        builder.AppendLine($"鼠标点击\t{result.Safety.ClicksMouse}");
        builder.AppendLine($"出售物品\t{result.Safety.SellsItems}");
        builder.AppendLine($"修改背包\t{result.Safety.ModifiesInventory}");
        builder.AppendLine($"二次确认\t{result.Safety.RequiresSecondConfirmation}");
        builder.AppendLine($"未知保护\t{result.Safety.UnknownItemsProtectedByDefault}");
        builder.AppendLine($"说明\t{Clean(result.Message)}");
        builder.AppendLine($"占用格\t{result.Scan.OccupiedCount}");
        builder.AppendLine($"空格\t{result.Scan.EmptyCount}");
        builder.AppendLine($"可卖候选\t{result.SellCandidateCount}");
        builder.AppendLine($"保护项\t{result.ProtectedCount}");
        builder.AppendLine();
        builder.AppendLine("格子\t行列\t物品\t状态\t判定\t分数\t来源\t原因");

        foreach (var candidate in result.Candidates.OrderBy(c => c.Slot.Index))
        {
            builder.Append(candidate.Slot.Index + 1).Append('\t');
            builder.Append(candidate.Slot.Row + 1).Append('-').Append(candidate.Slot.Column + 1).Append('\t');
            builder.Append(Clean(string.IsNullOrWhiteSpace(candidate.Slot.ItemName) ? "未识别" : candidate.Slot.ItemName)).Append('\t');
            builder.Append(Clean(ToDisplayStatus(candidate))).Append('\t');
            builder.Append(candidate.Decision).Append('\t');
            builder.Append(candidate.Slot.OccupancyScore.ToString("0.000")).Append('\t');
            builder.Append(Clean(candidate.Slot.Source)).Append('\t');
            builder.AppendLine(Clean(candidate.Reason));
        }

        builder.AppendLine();
        builder.AppendLine("安全结论\t本报告只用于候选审查；当前版本默认不出售、不丢弃、不点击物品。");
        return builder.ToString();
    }

    public void SaveTextReport(string path, BagCleanupResult result, BagCleanupConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, BuildTextReport(result, config), new UTF8Encoding(false));
    }

    private static string Clean(string value) =>
        value.Replace('\t', ' ')
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

    private static string ToDisplayStatus(ItemCleanupCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Slot.ItemName))
            return "待复核/未知保护";

        return candidate.Decision switch
        {
            ItemCleanupDecision.Keep => "保留",
            ItemCleanupDecision.Store => "存仓候选",
            ItemCleanupDecision.Trade => "交易候选",
            ItemCleanupDecision.SellCandidate => "出售候选",
            ItemCleanupDecision.BlockedUnknown => "待复核/未知保护",
            _ => "待复核/未知保护"
        };
    }
}
