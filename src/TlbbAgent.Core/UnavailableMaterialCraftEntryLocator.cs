using TlbbAgent.Contracts;

namespace TlbbAgent.Core;

public sealed class UnavailableMaterialCraftEntryLocator : IMaterialCraftEntryLocator
{
    public bool IsAvailable => false;
    public string EngineId => "unavailable";

    public MaterialCraftEntryDetection Locate(CaptureResult frame) =>
        MaterialCraftEntryDetection.Unavailable("材料加工入口模型尚未接入。");
}
