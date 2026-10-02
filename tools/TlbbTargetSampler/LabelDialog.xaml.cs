using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TlbbTargetSampler;

public partial class LabelDialog : Window
{
    private readonly string _missingSummary;
    private readonly string _recommendedKind;

    public LabelDialog(string missingSummary = "", string recommendedKind = "unknown")
    {
        _missingSummary = string.IsNullOrWhiteSpace(missingSummary)
            ? "当前缺口：请优先采集玩家、普通怪、BOSS、宠物、无目标样本。"
            : missingSummary.Trim();
        _recommendedKind = NormalizeKind(recommendedKind);

        InitializeComponent();
        MissingTextBlock.Text = _missingSummary;
        SelectKind(_recommendedKind);
        UpdateKindHint();
        Loaded += (_, _) =>
        {
            LabelTextBox.Focus();
            LabelTextBox.SelectAll();
        };
    }

    public string Label => LabelTextBox.Text.Trim();
    public string ExpectedKind => KindComboBox.SelectedItem is ComboBoxItem item && item.Tag is string kind
        ? kind
        : "unknown";

    private void KindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateKindHint();

    private void QuickKind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind })
            SelectKind(kind);
    }

    private void Save_Click(object sender, RoutedEventArgs e) => TrySave();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void LabelTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TrySave();
            e.Handled = true;
        }
    }

    private void TrySave()
    {
        if (string.IsNullOrWhiteSpace(LabelTextBox.Text))
        {
            MessageBox.Show(this, "标注不能为空。", "TLBB Agent 目标采集器", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ExpectedKind == "unknown")
        {
            var confirm = MessageBox.Show(
                this,
                "当前类型是“未知 / 待复核”。该样本不会直接用于放行或保护分类。仍然保存吗？",
                "确认保存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        DialogResult = true;
        Close();
    }

    private void UpdateKindHint()
    {
        if (KindHintTextBlock is null)
            return;

        KindHintTextBlock.Text = ExpectedKind switch
        {
            "player" => "玩家目标：目标面板有人物头像、门派竖牌或玩家结构。用于默认不攻击玩家，宁可多采也不要漏采。",
            "monster" => "普通怪物：确认不是玩家、不是宠物、不是BOSS。用于打怪放行样本；误标会影响攻击判断。",
            "boss" => "首领 / BOSS：可有多条血，但不能有人物头像或门派竖牌。用于避免把BOSS误判成玩家。",
            "pet" => "宠物 / 宝宝：目标是宠物或宝宝。用于避免误打宠物目标。",
            "none" => "无目标：游戏目标面板为空或没有任何目标。用于避免空面板被误识别成可攻击目标。",
            _ => "未知 / 待复核：只保存证据，不作为稳定分类样本。后续人工复核后再提升。"
        };

        if (LabelTextBox is not null && string.IsNullOrWhiteSpace(LabelTextBox.Text))
            LabelTextBox.Text = ExpectedKind switch
            {
                "player" => "玩家目标_门派待确认",
                "monster" => "普通怪_待确认",
                "boss" => "首领_BOSS_待确认",
                "pet" => "宠物宝宝_待确认",
                "none" => "无目标_空面板",
                _ => "待复核目标"
            };
    }

    private void SelectKind(string kind)
    {
        var normalized = NormalizeKind(kind);
        for (var i = 0; i < KindComboBox.Items.Count; i++)
        {
            if (KindComboBox.Items[i] is ComboBoxItem { Tag: string tag }
                && string.Equals(tag, normalized, StringComparison.OrdinalIgnoreCase))
            {
                KindComboBox.SelectedIndex = i;
                LabelTextBox.Text = "";
                UpdateKindHint();
                return;
            }
        }

        KindComboBox.SelectedIndex = 0;
    }

    private static string NormalizeKind(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "player" or "monster" or "boss" or "pet" or "none" or "unknown"
            ? normalized
            : "unknown";
    }
}
