using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Perception;

public sealed class VisionRemoteResponseSafetyGate : IVisionRemoteResponseSafetyGate
{
    private const double MinimumClickConfidence = 0.80;
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(2);

    public VisionRemoteValidationResult Validate(
        VisionRemoteFrameDescriptor request,
        VisionRemoteInferenceResponse response,
        int currentClientWidth,
        int currentClientHeight,
        string expectedModelSha256,
        DateTimeOffset now)
    {
        if (!string.Equals(request.ApiVersion, VisionRemoteProtocol.ApiVersion, StringComparison.Ordinal)
            || !string.Equals(response.ApiVersion, VisionRemoteProtocol.ApiVersion, StringComparison.Ordinal))
        {
            return VisionRemoteValidationResult.Reject("视觉协议版本不匹配。");
        }

        if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal)
            || !string.Equals(request.SessionId, response.SessionId, StringComparison.Ordinal)
            || request.FrameSequence != response.FrameSequence)
        {
            return VisionRemoteValidationResult.Reject("响应不属于当前会话或当前截图帧。");
        }

        if (!string.Equals(request.ImageSha256, response.ImageSha256, StringComparison.OrdinalIgnoreCase))
            return VisionRemoteValidationResult.Reject("响应截图哈希与本地上传帧不一致。");

        if (string.IsNullOrWhiteSpace(expectedModelSha256)
            || !string.Equals(expectedModelSha256, response.ModelSha256, StringComparison.OrdinalIgnoreCase))
        {
            return VisionRemoteValidationResult.Reject("云端模型版本不在本地允许列表中。");
        }

        if (request.Width != response.Width
            || request.Height != response.Height
            || response.Width != currentClientWidth
            || response.Height != currentClientHeight)
        {
            return VisionRemoteValidationResult.Reject("截图尺寸或当前客户区尺寸已经变化。");
        }

        if (response.CapturedAt != request.CapturedAt)
            return VisionRemoteValidationResult.Reject("响应中的采集时间与本地帧不一致。");

        if (response.ProcessedAt > now + MaximumClockSkew)
            return VisionRemoteValidationResult.Reject("云端响应时间超出允许时钟偏差。");

        if (now > response.ValidUntil)
            return VisionRemoteValidationResult.Reject("云端响应已经过期。");

        if (response.ActionProposals.Count > 1)
            return VisionRemoteValidationResult.Reject("单帧返回了多个动作，已拒绝执行。");

        var authorized = new List<VisionActionDecision>();
        foreach (var proposal in response.ActionProposals)
        {
            if (proposal.Action == VisionActionKind.Noop)
                continue;

            if (proposal.Action != VisionActionKind.Click)
                return VisionRemoteValidationResult.Reject("云端返回了不允许直接执行的动作类型。");

            if (proposal.Confidence < MinimumClickConfidence)
                return VisionRemoteValidationResult.Reject("云端点击建议置信度不足。");

            if (proposal.ExpiresAt < now || proposal.ExpiresAt > response.ValidUntil)
                return VisionRemoteValidationResult.Reject("云端点击建议已过期或有效期异常。");

            if (proposal.ClientX is not int x
                || proposal.ClientY is not int y
                || x < 0
                || y < 0
                || x >= currentClientWidth
                || y >= currentClientHeight)
            {
                return VisionRemoteValidationResult.Reject("云端点击坐标超出当前客户区。");
            }

            authorized.Add(new VisionActionDecision(
                true,
                VisionActionKind.Click,
                x,
                y,
                proposal.Reason));
        }

        return new VisionRemoteValidationResult(true, "响应与动作建议通过本地安全校验。", authorized);
    }
}
