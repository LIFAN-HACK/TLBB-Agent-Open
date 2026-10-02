using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

internal enum CombatLoopPlanAction
{
    ExecuteGrabMode,
    HandleDeadOrLost,
    ReacquireNoDamage,
    ContinueWeakTarget,
    AcquireTarget,
    WaitTargetLost,
    ExecutePureKeyBaseline,
    ExecutePendingFallbackAttack,
    EvaluateTargetSafety
}

internal sealed record CombatLoopPlan(
    CombatLoopPlanAction Action,
    CombatLoopDecision Decision,
    string AcquireReason = "",
    bool BypassAcquireInterval = false,
    bool ArmFallbackAttack = true);

internal sealed record CombatLoopPlannerInput(
    TargetSwitchMode SwitchMode,
    bool IsTargetDead,
    bool IsUnconfirmedWeakTarget,
    bool CanContinueWeakTarget,
    bool ShouldForceWeakTargetReacquire,
    bool ShouldTreatTargetLost,
    bool HadTarget,
    bool HasActionableTarget,
    bool CanUsePureKeyBaseline,
    bool HasPendingFallbackAttack,
    bool RequiresRecognizedTarget,
    string NoActionReason);

internal sealed class CombatLoopPlanner
{
    public CombatLoopPlan Plan(CombatLoopPlannerInput input)
    {
        if (input.SwitchMode == TargetSwitchMode.Grab)
        {
            return new(
                CombatLoopPlanAction.ExecuteGrabMode,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.GrabMode,
                    "抢怪模式：按选怪节奏快速尝试普攻和技能。",
                    willAcquire: true,
                    willAttack: true,
                    willCastSkill: true));
        }

        if (input.IsTargetDead)
        {
            return new(
                CombatLoopPlanAction.HandleDeadOrLost,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.TargetDeadSwitch,
                    "目标血量为 0，准备换怪。",
                    willAcquire: true,
                    willSkipAttack: true));
        }

        if (input.IsUnconfirmedWeakTarget)
        {
            if (input.HasPendingFallbackAttack && !input.RequiresRecognizedTarget)
            {
                return new(
                    CombatLoopPlanAction.ExecutePendingFallbackAttack,
                    CombatLoopDecision.For(
                        CombatLoopDecisionKind.PendingFallbackAttack,
                        "选怪后出现弱血条，等待目标面板刷新或执行兜底普攻。",
                        willAttack: true,
                        willCastSkill: true));
            }

            if (input.CanContinueWeakTarget)
            {
                if (input.ShouldForceWeakTargetReacquire)
                {
                    return new(
                        CombatLoopPlanAction.ReacquireNoDamage,
                        CombatLoopDecision.For(
                            CombatLoopDecisionKind.NoDamageReacquire,
                            "弱目标长期未确认或未掉血，准备重新选怪。",
                            willAcquire: true,
                            willSkipAttack: true),
                        AcquireReason: "弱目标长期未确认或未掉血，重新选怪",
                        BypassAcquireInterval: true,
                        ArmFallbackAttack: false);
                }

                return new(
                    CombatLoopPlanAction.ContinueWeakTarget,
                    CombatLoopDecision.For(
                        CombatLoopDecisionKind.ContinueWeakTarget,
                        "只有弱血条但无明确保护信号，继续输出当前目标。",
                        willAttack: true,
                        willCastSkill: true));
            }

            // Ghost weak HP (no name): do NOT lock on OCR noise. Prefer pure-key
            // select + attack + always skills (UU-style), otherwise reacquire.
            if (input.CanUsePureKeyBaseline && !input.RequiresRecognizedTarget)
            {
                return new(
                    CombatLoopPlanAction.ExecutePureKeyBaseline,
                    CombatLoopDecision.For(
                        CombatLoopDecisionKind.PureKeyBaseline,
                        "弱血条无怪名，按纯按键链路选怪/普攻/技能，不锁定噪声目标。",
                        willAcquire: true,
                        willAttack: true,
                        willCastSkill: true));
            }

            return new(
                CombatLoopPlanAction.AcquireTarget,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.AcquireTarget,
                    "目标识别不足，准备重新选怪。",
                    willAcquire: true,
                    willSkipAttack: true),
                AcquireReason: "目标识别不足",
                ArmFallbackAttack: !input.RequiresRecognizedTarget);
        }

        if (input.ShouldTreatTargetLost)
        {
            return new(
                CombatLoopPlanAction.HandleDeadOrLost,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.TargetLostSwitch,
                    "目标持续丢失超过延迟，准备换怪。",
                    willAcquire: true,
                    willSkipAttack: true));
        }

        if (input.HadTarget && !input.HasActionableTarget)
        {
            return new(
                CombatLoopPlanAction.WaitTargetLost,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.TargetLostWait,
                    "已有目标但本轮未识别到可行动目标，等待二次确认。",
                    willSkipAttack: true));
        }

        if (!input.HasActionableTarget && input.CanUsePureKeyBaseline)
        {
            return new(
                CombatLoopPlanAction.ExecutePureKeyBaseline,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.PureKeyBaseline,
                    "OCR 未确认目标，执行基础选怪/普攻/始终技能链路。",
                    willAcquire: true,
                    willAttack: true,
                    willCastSkill: true));
        }

        if (!input.HasActionableTarget && input.HasPendingFallbackAttack)
        {
            return new(
                CombatLoopPlanAction.ExecutePendingFallbackAttack,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.PendingFallbackAttack,
                    "选怪后等待目标面板刷新或执行兜底普攻。",
                    willAttack: true,
                    willCastSkill: true));
        }

        if (!input.HasActionableTarget)
        {
            var reason = string.IsNullOrWhiteSpace(input.NoActionReason)
                ? "未识别到目标"
                : input.NoActionReason;
            return new(
                CombatLoopPlanAction.AcquireTarget,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.AcquireTarget,
                    reason,
                    willAcquire: true,
                    willSkipAttack: true),
                AcquireReason: reason);
        }

        if (input.ShouldForceWeakTargetReacquire)
        {
            return new(
                CombatLoopPlanAction.ReacquireNoDamage,
                CombatLoopDecision.For(
                    CombatLoopDecisionKind.NoDamageReacquire,
                    "弱目标信号持续无掉血，准备重新选怪。",
                    willAcquire: true,
                    willSkipAttack: true),
                AcquireReason: "弱目标信号未恢复，重新选怪",
                BypassAcquireInterval: true,
                ArmFallbackAttack: false);
        }

        return new(
            CombatLoopPlanAction.EvaluateTargetSafety,
            CombatLoopDecision.For(
                CombatLoopDecisionKind.AttackCurrentTarget,
                "目标进入安全检查，通过后执行普攻与技能循环。",
                willAttack: true,
                willCastSkill: true));
    }
}
