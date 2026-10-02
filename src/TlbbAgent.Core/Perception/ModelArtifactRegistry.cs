using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class ModelArtifactRegistry : IModelArtifactRegistry
{
    private const string L1LayoutKey = "l1_layout_v2";
    private const string TargetHpSlotKey = "target_hp_slot";
    private const string L2OpenTextKey = "l2_opentext";
    private const string RightSkillBarKey = "right_skill_bar_v2";
    private const string BuffAnchorKey = "buff_anchor_r3";
    private const string InteractionWindowsKey = "interaction_windows_v3";
    private const string QuestLogPanelKey = "quest_log_panel_l1_v3_candidate";
    private const string SystemFeedbackRegionKey = "system_feedback_region_l1_v2";
    private const string RightSkillBarSha256640 = "fb59e520924e6967311a2423b3223718a1b2b16e54596c8106956f548da623d3";
    private const string RightSkillBarSha2561280 = "efcbd2bb235f281fa7341f8452de2929e27cb77573ec35f4346c12c9aa022dc3";

    public ModelArtifactInfo FindL1LayoutModel()
    {
        var directory = ResolveVisionSlotDirectory(L1LayoutKey);
        return BuildModelArtifact(
            L1LayoutKey,
            directory,
            "layout_v2_7class.onnx",
            "classes.yaml",
            "inference_config.json",
            "L1 V2 seven-class layout model found.",
            "L1 V2 seven-class layout model is missing or incomplete.");
    }

    public ModelArtifactInfo FindTargetHpSlotModel()
    {
        var directory = ResolveVisionSlotDirectory(TargetHpSlotKey);
        return BuildModelArtifact(
            TargetHpSlotKey,
            directory,
            "target_hp_slot_detect.onnx",
            "classes.yaml",
            "inference_config.json",
            "Target HP slot model found.",
            "Target HP slot model is missing or incomplete.");
    }

    public ModelArtifactInfo FindL2OpenTextModel()
    {
        var directory = ResolveVisionSlotDirectory(L2OpenTextKey);
        var modelPath = Path.Combine(directory, "opentext_rec.onnx");
        var classesPath = Path.Combine(directory, "charset.txt");
        var configPath = Path.Combine(directory, "inference_config.json");
        var workerPath = Path.Combine(directory, "opentext_worker.py");
        var zeroShotMarker = Path.Combine(directory, "ZERO_SHOT_RAPIDOCR.json");
        var available = File.Exists(modelPath) || File.Exists(workerPath) || File.Exists(zeroShotMarker);

        return new ModelArtifactInfo(
            L2OpenTextKey,
            directory,
            File.Exists(modelPath) ? modelPath : workerPath,
            classesPath,
            configPath,
            available
                ? File.Exists(modelPath)
                    ? File.GetLastWriteTimeUtc(modelPath).Ticks.ToString("x")
                    : "rapidocr-zero-shot"
                : "",
            available,
            available
                ? "L2 OpenText runtime found."
                : "L2 OpenText runtime is missing.");
    }

    public RightSkillBarArtifactSet FindRightSkillBarModels()
    {
        var directory = ResolveVisionSlotDirectory(RightSkillBarKey);
        var model640 = Path.Combine(directory, "right_skill_bar_v2_640.onnx");
        var model1280 = Path.Combine(directory, "right_skill_bar_v2_1280.onnx");
        var config = Path.Combine(directory, "inference_config.json");
        var available = File.Exists(model640) && File.Exists(model1280) && File.Exists(config);
        return new RightSkillBarArtifactSet(
            directory,
            model640,
            model1280,
            config,
            RightSkillBarSha256640,
            RightSkillBarSha2561280,
            available,
            available
                ? "Right skill bar dual-profile models found."
                : "Right skill bar dual-profile models are missing or incomplete.");
    }

    public ModelArtifactInfo FindBuffAnchorModel()
    {
        var directory = ResolveVisionSlotDirectory(BuffAnchorKey);
        return BuildModelArtifact(
            BuffAnchorKey,
            directory,
            "buff_anchor_detect.onnx",
            "classes.yaml",
            "inference_config.json",
            "Buff anchor model found.",
            "Buff anchor model is missing or incomplete.");
    }

    public ModelArtifactInfo FindInteractionWindowModel()
    {
        var directory = ResolveVisionSlotDirectory(InteractionWindowsKey);
        return BuildModelArtifact(
            InteractionWindowsKey,
            directory,
            "interaction_window_l1_v3_5class.onnx",
            "classes.yaml",
            "inference_contract.json",
            "Interaction window V3 model found.",
            "Interaction window V3 model is missing or incomplete.");
    }

    public ModelArtifactInfo FindQuestLogPanelModel()
    {
        var directory = ResolveVisionSlotDirectory(QuestLogPanelKey);
        return BuildModelArtifact(
            QuestLogPanelKey,
            directory,
            "quest_log_panel_l1_v3.onnx",
            "classes.yaml",
            "inference_config.json",
            "Quest log panel L1 V3 candidate model found.",
            "Quest log panel L1 V3 candidate model is missing or incomplete.");
    }

    public ModelArtifactInfo FindSystemFeedbackRegionModel()
    {
        var directory = ResolveVisionSlotDirectory(SystemFeedbackRegionKey);
        return BuildModelArtifact(
            SystemFeedbackRegionKey,
            directory,
            "system_feedback_region_l1_v2.onnx",
            "classes.yaml",
            "inference_config.json",
            "System feedback region L1 V2 model found.",
            "System feedback region L1 V2 model is missing or incomplete.");
    }

    public VisionV2ArtifactSet FindVisionV2Artifacts()
    {
        var root = ResolveVisionV2Root();
        var slotWorker = Path.Combine(root, "l2_slots", "l2_slot_worker.py");
        var coordinateWorker = Path.Combine(root, "l2_slots", "minimap_coordinate_worker.py");
        var fixedUiWorker = Path.Combine(root, "fixed_ui_slots", "fixed_ui_slot_worker.py");
        var auxLocator = Path.Combine(root, "l1_ui_aux", "ui_aux_locator.py");
        var panelWorker = Path.Combine(root, "l2_ui_panels", "ui_panel_slot_worker.py");
        var actionPlanner = Path.Combine(root, "l2_slots", "slot_action_planner.py");
        var required = new[]
        {
            slotWorker,
            coordinateWorker,
            fixedUiWorker,
            auxLocator,
            panelWorker,
            actionPlanner
        };
        var available = required.All(File.Exists);
        var missing = required.Where(path => !File.Exists(path)).Select(Path.GetFileName).ToArray();

        return new VisionV2ArtifactSet(
            root,
            slotWorker,
            coordinateWorker,
            fixedUiWorker,
            auxLocator,
            panelWorker,
            actionPlanner,
            available,
            available
                ? "Vision V2 slot runtime found."
                : $"Vision V2 slot runtime is incomplete: {string.Join(", ", missing)}");
    }

    private static ModelArtifactInfo BuildModelArtifact(
        string key,
        string directory,
        string modelName,
        string classesName,
        string configName,
        string availableMessage,
        string unavailableMessage)
    {
        var modelPath = Path.Combine(directory, modelName);
        var classesPath = Path.Combine(directory, classesName);
        var configPath = Path.Combine(directory, configName);
        var available = File.Exists(modelPath) && File.Exists(classesPath) && File.Exists(configPath);
        return new ModelArtifactInfo(
            key,
            directory,
            modelPath,
            classesPath,
            configPath,
            available ? File.GetLastWriteTimeUtc(modelPath).Ticks.ToString("x") : "",
            available,
            available ? availableMessage : unavailableMessage);
    }

    private static string ResolveVisionSlotDirectory(string slotKey)
    {
        var fromEnv = Environment.GetEnvironmentVariable("TLBB_AGENT_VISION_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var direct = Path.Combine(fromEnv, slotKey);
            if (Directory.Exists(direct))
                return direct;
        }

        var packaged = Path.Combine(AppContext.BaseDirectory, "models", "vision", slotKey);
        if (Directory.Exists(packaged))
            return packaged;

        var repo = Path.Combine(Environment.CurrentDirectory, "src", "TlbbAgent.Core", "models", "vision", slotKey);
        if (Directory.Exists(repo))
            return repo;

        var desktopArtifacts = ResolveDesktopArtifactsRoot();
        if (slotKey == L1LayoutKey)
            return Path.Combine(desktopArtifacts, "l1_layout_v2_20260710_r6_pet_lowhp");
        return Path.Combine(desktopArtifacts, slotKey);
    }

    private static string ResolveVisionV2Root()
    {
        var fromEnv = Environment.GetEnvironmentVariable("TLBB_AGENT_VISION_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var direct = Path.Combine(fromEnv, "v2");
            if (Directory.Exists(direct))
                return direct;
            if (Directory.Exists(Path.Combine(fromEnv, "l2_slots")))
                return fromEnv;
        }

        var packaged = Path.Combine(AppContext.BaseDirectory, "models", "vision", "v2");
        if (Directory.Exists(packaged))
            return packaged;

        var repo = Path.Combine(Environment.CurrentDirectory, "src", "TlbbAgent.Core", "models", "vision", "v2");
        if (Directory.Exists(repo))
            return repo;

        return ResolveDesktopArtifactsRoot();
    }

    private static string ResolveDesktopArtifactsRoot()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, "TLBB 视觉模型", "train", "artifacts");
    }
}
