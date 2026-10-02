namespace TlbbAgent.Contracts;

public static class ProductIdentity
{
    public const string Name = "TLBB Agent";
    public const string Subtitle = "新一代的AI智能辅助系统";
    public const string DataFolderName = "TLBB Agent";

    // 仅用于从旧版本读取已有用户配置，不作为对外品牌显示。
    public const string LegacyDataFolderName = "龙伴行";
}
