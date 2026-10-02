using System.Text.Json;
using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public sealed class NormalHangController : AutomationControllerBase<NormalHangState>, INormalHangController
{
    private const int VisualSkillFastEvidenceWaitMs = 1200;
    private const int VisualSkillAsyncFeedbackMaxWaitMs = 3000;
    private const int CustomSkillNoFailureCommitMs = 650;
    private const int CustomSkillAsyncFeedbackMaxWaitMs = 1200;
    private const int RecoveryVerificationWaitMs = 1800;
    private const int RecoveryFailureBackoffMs = 60000;
    private readonly IGlobalConfigService _global;
    private readonly ICombatBackendSelector _backendSelector;
    private readonly ITargetSafetyService _targetSafety;
    private readonly IGameBuiltInHangSwitchService? _gameBuiltInHangSwitch;
    private readonly ISkillCooldownRecognitionService? _skillCooldown;
    private readonly ICombatEnvironmentGateService? _environmentGate;
    private readonly IPerceptionHub? _perceptionHub;
    private readonly IVisionActionPlanner? _visionActionPlanner;
    private readonly IVisionActionExecutor? _visionActionExecutor;
    private readonly IMinimapTemporalFusionService? _minimapFusion;
    private readonly IGameHotkeyProfileService? _gameHotkeys;
    private readonly IReturnPointRuntimeService _returnPointRuntime;
    private readonly IStateMachineEngine _engine;
    private readonly ICombatLoopTelemetryService? _telemetry;
    private readonly CombatLoopPlanner _combatLoopPlanner = new();
    private readonly Dictionary<string, long> _lastSkillTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _visualSkillBackoffUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _customSkillRetryUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _visualSkillPolicyObservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastRecoveryWarningTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastRecoveryInfoTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _recoveryBackoffUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRecoveryVerification> _pendingRecoveryVerifications = new(StringComparer.Ordinal);
    private IDisposable? _runner;
    private ICombatBackend? _backend;
    private bool _conditionWarningWritten;
    private bool _targetStrategyWarningWritten;
    private bool _hadTarget;
    private string _lastTargetText = "";
    /// <summary>
    /// KillThenSwitch lock: after we have actually engaged (普攻/技能), do not re-press 选怪
    /// until death / empty-bar / no-progress timeout. Prevents Ctrl+Tab thrashing mid-fight.
    /// </summary>
    private bool _combatTargetLocked;
    /// <summary>Consecutive scans seeing dead/empty target HP while locked (死后空血).</summary>
    private int _deadOrEmptyHpStreak;
    private long _combatLockStartedTicks;
    /// <summary>Simplified kill-then-switch phase (pure-key hang).</summary>
    private KillSwitchPhase _killSwitchPhase = KillSwitchPhase.Seeking;
    private long _fightStartedTicks;
    private long _lastHpProgressTicks;
    private int? _bestFightHpPercent;
    private long _awaitingTargetSinceTicks;
    /// <summary>
    /// After death/empty switch: briefly ignore name-only re-lock so residual corpse
    /// panel OCR does not re-enter Fighting and stall ~5s before the next 选怪.
    /// </summary>
    private long _postDeathIgnoreNameLockUntil;
    private string _attackProgressTargetKey = "";

    private enum KillSwitchPhase
    {
        Seeking,
        AwaitingTarget,
        Fighting
    }
    private int? _bestObservedTargetHp;
    private int _attackActionsOnCurrentTarget;
    private bool _attackSentForCurrentTarget;
    private int _noDamageTimeoutsOnCurrentTarget;
    private int _plannedAttackNoActionScans;
    private long _attackProgressStartTicks;
    private long _lastUnattackableSwitchTicks;
    private long _lastCombatSnapshotTicks;
    private long _lastInputFailureTicks;
    private long _lastNoActionWatchdogTicks;
    private long _lastAcquireTicks;
    private long _lastAttackTicks;
    private long _pendingAcquireAttackTicks;
    private bool _firstAttackPendingAfterAcquire;
    private long _targetLostStartedTicks;

    private long _lastBackendRecheckTicks;
    private long _lastDecisionLogTicks;
    private long _lastTargetNameListDiagnosticTicks;
    private string _lastUserVisibleTargetName = "";
    private string _targetPolicyNameCandidate = "";
    private int _targetPolicyNameCandidateFrames;
    private long _targetPolicyNameWaitStartedTicks;
    private CombatLoopDecisionKind _lastDecisionKind;
    private string _lastDecisionReason = "";
    private readonly Queue<int> _recentTargetHpDrops = new();
    private int? _lastPredictionHp;
    private int _predictionActionsOnCurrentTarget;
    private int _predictionActionsSinceHpSample;
    private long _lastPredictiveSwitchTicks;
    private long _lastEmHealTicks;
    private long _lastPillTicks;
    private long _lastPetDeployTicks;
    private long _lastBloodManaTicks;
    private long _lastSymbiosisTicks;
    private long _lastPetFoodTicks;
    private bool _petFoodRecoveryActive;
    private long _lastPlayerTargetWarningTicks;
    private long _lastProtectedTargetClearTicks;
    private string _lastProtectedTargetKey = "";
    private int _protectedTargetRejectScans;
    private int _missingPetHpScans;
    private bool _actionSentThisScan;
    private PendingVisualSkillVerification? _pendingVisualSkillVerification;
    private PendingCustomSkillVerification? _pendingCustomSkillVerification;
    private PendingOpeningSkillDispatch? _pendingOpeningSkillDispatch;

    private sealed record PendingRecoveryVerification(int BeforePercent, long SentAtTicks);

    private enum AttackHotkeyResult
    {
        Sent,
        AlreadySentForTarget,
        WaitingInterval,
        MissingHotkey,
        InvalidHotkey,
        SendFailed,
        NoTarget
    }

    public NormalHangController(
        ILogService log,
        IGlobalConfigService global,
        ICombatBackendSelector backendSelector,
        IReturnPointRuntimeService returnPointRuntime,
        IStateMachineEngine engine,
        ITargetSafetyService? targetSafety = null,
        IGameBuiltInHangSwitchService? gameBuiltInHangSwitch = null,
        ISkillCooldownRecognitionService? skillCooldown = null,
        ICombatLoopTelemetryService? telemetry = null,
        ICombatEnvironmentGateService? environmentGate = null,
        IPerceptionHub? perceptionHub = null,
        IVisionActionPlanner? visionActionPlanner = null,
        IVisionActionExecutor? visionActionExecutor = null,
        IMinimapTemporalFusionService? minimapFusion = null,
        IGameHotkeyProfileService? gameHotkeys = null)
        : base(NormalHangState.Idle, log)
    {
        _global = global;
        _backendSelector = backendSelector;
        _targetSafety = targetSafety ?? new TargetSafetyService();
        _gameBuiltInHangSwitch = gameBuiltInHangSwitch;
        _skillCooldown = skillCooldown;
        _telemetry = telemetry;
        _environmentGate = environmentGate;
        _perceptionHub = perceptionHub;
        _visionActionPlanner = visionActionPlanner;
        _visionActionExecutor = visionActionExecutor;
        _minimapFusion = minimapFusion;
        _gameHotkeys = gameHotkeys;
        _returnPointRuntime = returnPointRuntime;
        _engine = engine;
    }

    public override string Key => "hang.normal";
    public override string Name => "按键打怪";
    public override string Group => "挂机";

    private ICombatBackend Backend => _backend ?? throw new InvalidOperationException("Combat backend has not been resolved.");

    protected override void OnStart()
    {
        Machine.Goto(NormalHangState.BindWindow);
        ResetRuntimeState();

        if (Target is null)
        {
            RunState = RunState.Error;
            Machine.Goto(NormalHangState.ErrorRecovery);
            Log.Warn(Name, "未绑定窗口，按键打怪无法启动。");
            RaiseStateChanged();
            return;
        }

        _minimapFusion?.Reset(Target.Id);

        _backend = _backendSelector.Resolve(Target, _global.Current, out var backendStatus);
        if (!backendStatus.IsAvailable)
        {
            RunState = RunState.Error;
            Machine.Goto(NormalHangState.ErrorRecovery);
            Log.Warn(Name, $"战斗方式不可用：{backendStatus.Message}", Target.Id);
            RaiseStateChanged();
            return;
        }

        var builtInMonitorMode = IsGameBuiltInMonitorMode(_global.Current, _backend);
        var skills = builtInMonitorMode ? new List<SkillPreset>() : GetExecutableSkills();
        var effectiveAttackHotkey = GetAttackHotkey(_global.Current);
        if (!builtInMonitorMode && skills.Count == 0 && string.IsNullOrWhiteSpace(effectiveAttackHotkey))
        {
            RunState = RunState.Error;
            Machine.Goto(NormalHangState.ErrorRecovery);
            Log.Warn(Name, "未配置可执行技能或攻击热键，按键打怪未启动。", Target.Id);
            RaiseStateChanged();
            return;
        }

        if (builtInMonitorMode)
        {
            Log.Info(Name, "游戏内置挂机监控已启动：本程序只监控回点、死亡和卡点，不发送选怪/普攻/技能。", Target.Id);
            TryTriggerGameBuiltInHangSwitch(_global.Current);
        }
        else
        {
            Log.Info(Name, $"按键打怪已启动：技能 {skills.Count} 个。", Target.Id);
            Log.Info(Name, $"启动自检：模式={TargetSwitchModeText(_global.Current.Targeting.SwitchMode)}，输入={_global.Current.InputDispatchMode}，选怪={Blank(GetAcquireHotkey(_global.Current))}，普攻={Blank(effectiveAttackHotkey)}，普攻间隔={_global.Current.AttackIntervalMs / 1000.0:0.##}秒。", Target.Id);
        }

        _runner?.Dispose();
        _runner = _engine.Run(Name, RunLoopAsync, MarkFaulted);
    }

    public override void Pause()
    {
        base.Pause();
        Machine.Goto(NormalHangState.Stop);
    }

    public override void Stop()
    {
        _runner?.Dispose();
        _runner = null;
        base.Stop();
    }

    private void ResetRuntimeState()
    {
        _lastSkillTicks.Clear();
        _visualSkillBackoffUntil.Clear();
        _customSkillRetryUntil.Clear();
        _pendingVisualSkillVerification = null;
        _pendingCustomSkillVerification = null;
        _pendingOpeningSkillDispatch = null;
        _visualSkillPolicyObservations.Clear();
        _lastRecoveryWarningTicks.Clear();
        _lastRecoveryInfoTicks.Clear();
        _recoveryBackoffUntil.Clear();
        _pendingRecoveryVerifications.Clear();
        _conditionWarningWritten = false;
        _targetStrategyWarningWritten = false;
        _hadTarget = false;
        _lastTargetText = "";
        ResetTargetPolicyNameState();
        _combatTargetLocked = false;
        _deadOrEmptyHpStreak = 0;
        _combatLockStartedTicks = 0;
        _killSwitchPhase = KillSwitchPhase.Seeking;
        _fightStartedTicks = 0;
        _lastHpProgressTicks = 0;
        _bestFightHpPercent = null;
        _awaitingTargetSinceTicks = 0;
        _postDeathIgnoreNameLockUntil = 0;
        _attackProgressTargetKey = "";
        _bestObservedTargetHp = null;
        _attackActionsOnCurrentTarget = 0;
        _attackSentForCurrentTarget = false;
        _noDamageTimeoutsOnCurrentTarget = 0;
        _plannedAttackNoActionScans = 0;
        _attackProgressStartTicks = 0;
        _lastUnattackableSwitchTicks = 0;
        _lastCombatSnapshotTicks = 0;
        _lastInputFailureTicks = 0;
        _lastNoActionWatchdogTicks = 0;
        _lastAcquireTicks = 0;
        _lastAttackTicks = 0;
        _pendingAcquireAttackTicks = 0;
        _firstAttackPendingAfterAcquire = false;
        _targetLostStartedTicks = 0;

        _lastBackendRecheckTicks = 0;
        _lastDecisionLogTicks = 0;
        _lastTargetNameListDiagnosticTicks = 0;
        _lastUserVisibleTargetName = "";
        _lastDecisionKind = CombatLoopDecisionKind.Unknown;
        _lastDecisionReason = "";
        ResetKillPrediction(null);
        _lastPredictiveSwitchTicks = 0;
        _lastEmHealTicks = 0;
        _lastPillTicks = 0;
        _lastPetDeployTicks = 0;
        _lastBloodManaTicks = 0;
        _lastSymbiosisTicks = 0;
        _lastPetFoodTicks = 0;
        _petFoodRecoveryActive = false;
        _lastPlayerTargetWarningTicks = 0;
        _lastProtectedTargetClearTicks = 0;
        _lastProtectedTargetKey = "";
        _protectedTargetRejectScans = 0;
        _missingPetHpScans = 0;
        _actionSentThisScan = false;
        _returnPointRuntime.Reset();
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        Machine.Goto(NormalHangState.CheckRoleStatus);

        while (!token.IsCancellationRequested)
        {
            if (RunState == RunState.Paused)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                continue;
            }

            if (Target is null)
            {
                Machine.Goto(NormalHangState.ErrorRecovery);
                Log.Warn(Name, "目标窗口丢失，按键打怪循环停止。");
                break;
            }

            var cfg = _global.Current;
            _backend ??= _backendSelector.Resolve(Target, cfg, out _);
            RefreshAutoBackendIfDue(cfg);
            if (IsGameBuiltInMonitorMode(cfg, _backend))
            {
                Machine.Goto(NormalHangState.CheckRoleStatus);
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.GameBuiltInMonitor,
                    "当前为游戏内置挂机监控模式，本程序不发送选怪/普攻/技能。"));
                await DelayForNextScan(cfg, token).ConfigureAwait(false);
                continue;
            }

            // Opening normal attack and skill dispatch use separate action slots. Do not
            // wait for the next full perception pass: ONNX/OCR can take seconds on a busy
            // machine, while the game only needs the configured public-CD gap.
            if (await TryDispatchPendingOpeningSkillAsync(cfg, token).ConfigureAwait(false))
            {
                await DelayForNextScan(cfg, token).ConfigureAwait(false);
                continue;
            }

            var environmentGate = _environmentGate?.Evaluate(Target, cfg);
            if (environmentGate?.ShouldSkipCombat == true)
            {
                Machine.Goto(NormalHangState.CheckRoleStatus);
                ResetTargetAttackProgress();
                _pendingAcquireAttackTicks = 0;
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.EnvironmentGate,
                    environmentGate.Reason,
                    willSkipAttack: true));
                InfoThrottled("环境门禁", environmentGate.Reason, Target.Id, 5000);
                await DelayForNextScan(cfg, token).ConfigureAwait(false);
                continue;
            }

            var combat = _backend.Read(Target, cfg);
            LogRecognizedTargetName(cfg, combat);
            _actionSentThisScan = false;

            // Return-point runtime retired — never suspend combat for legacy 返点.

            WriteUnsupportedConditionWarningIfNeeded(cfg, combat);

            if (cfg.Targeting.SwitchMode != TargetSwitchMode.Grab
                && combat.NearbyMonsterState == VisionTriState.Absent
                && !IsLiveTargetHp(combat.TargetHpPercent)
                && _killSwitchPhase != KillSwitchPhase.Fighting)
            {
                _pendingAcquireAttackTicks = 0;
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.NoActionDiagnosis,
                    "小地图时序确认附近没有怪物，本轮不发送选怪或攻击输入；死亡目标残留姓名框不视为新目标。",
                    willSkipAttack: true));
                InfoThrottled(
                    "小地图:附近无怪",
                    "连续无怪帧且没有存活目标，已暂停选怪轮询。",
                    Target.Id,
                    3000);
                TryRunLowPrioritySupportActions(cfg, combat, allowDuringIdle: true);
                await DelayForNextScan(cfg, token).ConfigureAwait(false);
                continue;
            }

            // --- Simplified kill-then-switch pure-key loop (default hang) ---
            // Bypasses the OCR-heavy planner that previously stuck on "锁定不重选"
            // while TargetHp OCR stayed at 40%~98% after the mob was already dead.
            if (ShouldUseSimplifiedKillSwitchLoop(cfg))
            {
                RunSimplifiedKillSwitchLoop(cfg, combat);
                TryRunLowPrioritySupportActions(cfg, combat);
                await DelayForNextScan(cfg, token).ConfigureAwait(false);
                continue;
            }

            Machine.Goto(NormalHangState.SelectTarget);
            var hasActionableTarget = HasActionableTarget(combat);
            var isUnconfirmedWeakTarget = IsUnconfirmedWeakTarget(combat);
            var canContinueWeakTarget = isUnconfirmedWeakTarget && ShouldContinueWeakTargetCombat(cfg, combat);
            var shouldForceWeakTargetReacquire =
                (isUnconfirmedWeakTarget && canContinueWeakTarget) || hasActionableTarget
                    ? ShouldForceWeakTargetReacquire(cfg, combat)
                    : false;
            var noActionReason = combat.HasTargetHp
                ? "仅识别到目标血条弱信号，未确认可攻击目标"
                : "未识别到目标";
            var plan = _combatLoopPlanner.Plan(new CombatLoopPlannerInput(
                cfg.Targeting.SwitchMode,
                IsTargetDead(combat),
                isUnconfirmedWeakTarget,
                canContinueWeakTarget,
                shouldForceWeakTargetReacquire,
                ShouldTreatTargetLost(cfg, combat),
                _hadTarget,
                hasActionableTarget,
                CanUsePureKeyBaseline(cfg),
                _pendingAcquireAttackTicks != 0,
                RequiresRecognizedTarget(cfg),
                noActionReason));

            switch (plan.Action)
            {
                case CombatLoopPlanAction.ExecuteGrabMode:
                    TraceDecision(plan.Decision, combat);
                    ExecuteGrabModeCycle(cfg, combat);
                    await DelayForNextScan(cfg, token).ConfigureAwait(false);
                    continue;

                case CombatLoopPlanAction.HandleDeadOrLost:
                    TraceDecision(plan.Decision, combat);
                    await HandleTargetDeadOrLostAsync(cfg, combat, token).ConfigureAwait(false);
                    continue;

                case CombatLoopPlanAction.ReacquireNoDamage:
                    TraceDecision(plan.Decision, combat);
                    TryAcquireTarget(
                        cfg,
                        plan.AcquireReason,
                        bypassGlobalCooldown: true,
                        bypassAcquireInterval: plan.BypassAcquireInterval,
                        armFallbackAttack: plan.ArmFallbackAttack);
                    await DelayForNextScan(cfg, token).ConfigureAwait(false);
                    continue;

                case CombatLoopPlanAction.ContinueWeakTarget:
                    ContinueWeakTargetCombat(cfg, combat);
                    TryRunLowPrioritySupportActions(cfg, combat);
                    await DelayForNextScan(cfg, token).ConfigureAwait(false);
                    continue;

                case CombatLoopPlanAction.AcquireTarget:
                    ResetTargetAttackProgress();
                    LogCombatSnapshotIfDue(combat, noActionReason);
                    TraceDecision(plan.Decision, combat);
                    TryAcquireTarget(cfg, plan.AcquireReason, armFallbackAttack: plan.ArmFallbackAttack);
                    await DelayForNextScan(cfg, token).ConfigureAwait(false);
                    continue;

                case CombatLoopPlanAction.WaitTargetLost:
                    TraceDecision(plan.Decision, combat);
                    await DelayForNextScan(cfg, token).ConfigureAwait(false);
                    continue;

                case CombatLoopPlanAction.ExecutePureKeyBaseline:
                    if (TryRunPureKeyBaselineCycle(cfg, combat))
                    {
                        await DelayForNextScan(cfg, token).ConfigureAwait(false);
                        continue;
                    }
                    break;

                case CombatLoopPlanAction.ExecutePendingFallbackAttack:
                    if (TrySendPendingAcquireAttack(cfg, combat))
                    {
                        await DelayForNextScan(cfg, token).ConfigureAwait(false);
                        continue;
                    }
                    break;
            }

            LogCombatSnapshotIfDue(combat, "已识别到目标");

            var targetDecision = EvaluateTarget(cfg, combat);
            if (!targetDecision.Allowed)
            {
                TraceDecision(CombatLoopDecision.For(
                    targetDecision.IsProtectedTarget ? CombatLoopDecisionKind.SkipProtectedTarget : CombatLoopDecisionKind.AcquireTarget,
                    targetDecision.Reason,
                    willAcquire: targetDecision.ShouldAcquire,
                    willSkipAttack: true,
                    isProtection: targetDecision.IsProtectedTarget), combat);
                Log.Trace(Name, $"目标策略未通过：{targetDecision.Reason}，跳过攻击。", Target.Id);
                if (targetDecision.ShouldAcquire && ShouldAcquireAfterRejectedTarget(cfg))
                    RecoverRejectedTarget(cfg, combat, targetDecision);
                await DelayForNextScan(cfg, token).ConfigureAwait(false);
                continue;
            }

            ResetProtectedTargetRecovery();
            TrackTargetAttackProgress(combat);
            if (ShouldSwitchProbablyUnattackableTarget(cfg, combat))
            {
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.NoDamageReacquire,
                    "目标疑似不可攻击或长期无掉血，准备重新选怪。",
                    willAcquire: true,
                    willSkipAttack: true), combat);
                TryAcquireTarget(cfg, "目标疑似不可攻击，重新选怪", bypassGlobalCooldown: true, bypassAcquireInterval: true);
                await DelayForNextScan(cfg, token).ConfigureAwait(false);
                continue;
            }

            _hadTarget = true;
            if (!string.IsNullOrWhiteSpace(combat.TargetNameText))
                _lastTargetText = combat.TargetNameText;

            if (TryRunEstablishedCombatRecoveryActions(cfg, combat))
            {
                await DelayForNextScan(cfg, token).ConfigureAwait(false);
                continue;
            }

            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.AttackCurrentTarget,
                "目标允许攻击，执行普攻与技能循环。",
                willAttack: true,
                willCastSkill: true), combat);
            // Named/strong target: keep pressing attack on interval so DPS does not stall after first hit.
            var attackResult = TrySendAttackHotkey(
                cfg,
                allowRepeatOnSameTarget: true,
                bypassIntervalIfFirstAttack: true);

            Machine.Goto(NormalHangState.AttackLoop);
            var skillSent = TrySendDueSkillAfterAttack(cfg, combat, attackResult);
            NoteCombatEngagement(attackResult, skillSent);
            NotePlannedAttackOutcome("攻击当前目标", attackResult, skillSent, cfg, combat);
            TryRunLowPrioritySupportActions(cfg, combat);

            await DelayForNextScan(cfg, token).ConfigureAwait(false);
        }
    }

    private void ExecuteGrabModeCycle(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (IsUnconfirmedWeakTarget(combat))
        {
            if (combat.TargetLooksLikePlayer
                || IsAmbiguousPlayerProtectionEnabled(cfg, combat)
                || combat.TargetLooksLikePet
                || combat.TargetIsAttackable == false)
            {
                ResetTargetAttackProgress();
                var decision = EvaluateTarget(cfg, combat);
                if (!decision.Allowed && decision.ShouldAcquire && ShouldAcquireAfterRejectedTarget(cfg))
                    RecoverRejectedTarget(cfg, combat, decision);
                return;
            }

            ResetTargetAttackProgress();
            LogCombatSnapshotIfDue(combat, "抢怪模式弱目标：切到目标打一发后立即切换");
            var weakAcquired = TryAcquireTarget(cfg, "抢怪模式切目标", bypassGlobalCooldown: true, bypassAcquireInterval: true, armFallbackAttack: false);
            if (!weakAcquired)
                return;

            var weakAttackResult = TrySendAttackHotkey(cfg, allowRepeatOnSameTarget: true, bypassIntervalIfFirstAttack: true);
            Machine.Goto(NormalHangState.AttackLoop);
            var weakSkillSent = TrySendDueSkillAfterAttack(cfg, combat, weakAttackResult);
            NotePlannedAttackOutcome("抢怪弱目标", weakAttackResult, weakSkillSent, cfg, combat);
            TrySwitchAfterGrabAttack(cfg, weakAttackResult, weakSkillSent);
            return;
        }

        var targetDecision = EvaluateTarget(cfg, combat);
        if (!targetDecision.Allowed && (RequiresRecognizedTarget(cfg) || targetDecision.IsProtectedTarget))
        {
            Log.Trace(Name, $"当前策略需要识别通过：{targetDecision.Reason}，跳过本轮攻击。", Target?.Id ?? "-");
            if (ShouldAcquireAfterRejectedTarget(cfg))
                RecoverRejectedTarget(cfg, combat, targetDecision);
            return;
        }

        ResetProtectedTargetRecovery();
        ResetTargetAttackProgress();
        var acquired = TryAcquireTarget(cfg, "抢怪模式切目标", bypassGlobalCooldown: true, bypassAcquireInterval: true, armFallbackAttack: false);
        if (!acquired)
            return;

        if (HasActionableTarget(combat))
        {
            _hadTarget = true;
            if (!string.IsNullOrWhiteSpace(combat.TargetNameText))
                _lastTargetText = combat.TargetNameText;
            LogCombatSnapshotIfDue(combat, "抢怪模式识别到目标");
        }
        else
        {
            LogCombatSnapshotIfDue(combat, "抢怪模式未识别目标，继续按键执行");
        }

        var attackResult = TrySendAttackHotkey(cfg, allowRepeatOnSameTarget: true, bypassIntervalIfFirstAttack: true);

        Machine.Goto(NormalHangState.AttackLoop);
        var skillSent = TrySendDueSkillAfterAttack(cfg, combat, attackResult);
        NotePlannedAttackOutcome("抢怪模式", attackResult, skillSent, cfg, combat);
        TrySwitchAfterGrabAttack(cfg, attackResult, skillSent);
    }

    private void TrySwitchAfterGrabAttack(GlobalConfig cfg, AttackHotkeyResult attackResult, bool skillSent)
    {
        if (attackResult != AttackHotkeyResult.Sent && !skillSent)
            return;

        TryAcquireTarget(
            cfg,
            "抢怪模式已出手，立即切换下一目标",
            bypassGlobalCooldown: true,
            bypassAcquireInterval: true,
            armFallbackAttack: false);
    }

    private async Task HandleTargetDeadOrLostAsync(GlobalConfig cfg, CombatRecognitionResult combat, CancellationToken token)
    {
        Machine.Goto(NormalHangState.LootOrNextTarget);
        var targetDead = combat.TargetHpPercent == 0;
        var reason = targetDead ? "目标血量为 0" : "目标丢失已确认";
        Log.Info(Name, $"{reason}，进入换怪。", Target?.Id ?? "-");
        _hadTarget = false;
        _lastTargetText = "";
        ClearCombatTargetLock();
        _targetLostStartedTicks = 0;
        ResetTargetAttackProgress();

        var delayMs = targetDead ? 80 : 100;
        await Task.Delay(delayMs, token).ConfigureAwait(false);
        TryAcquireTarget(cfg, "换怪");
    }


    private void TrackTargetAttackProgress(CombatRecognitionResult combat)
    {
        _targetLostStartedTicks = 0;
        var key = TargetProgressKey(combat);
        var now = Environment.TickCount64;
        if (!string.Equals(_attackProgressTargetKey, key, StringComparison.Ordinal))
        {
            _attackProgressTargetKey = key;
            _bestObservedTargetHp = combat.TargetHpPercent;
            ResetKillPrediction(combat.TargetHpPercent);
            _attackActionsOnCurrentTarget = 0;
            _attackSentForCurrentTarget = false;
            _noDamageTimeoutsOnCurrentTarget = 0;
            _attackProgressStartTicks = now;
            return;
        }

        if (combat.TargetHpPercent is null)
            return;

        if (_bestObservedTargetHp is not null && IsLikelyNewSameNameTarget(combat.TargetHpPercent.Value, _bestObservedTargetHp.Value))
        {
            Log.Trace(Name, $"目标血量从 {_bestObservedTargetHp.Value}% 回升到 {combat.TargetHpPercent.Value}%，按同名新目标重新起算。", Target?.Id ?? "-");
            _bestObservedTargetHp = combat.TargetHpPercent;
            ResetKillPrediction(combat.TargetHpPercent);
            _attackActionsOnCurrentTarget = 0;
            _attackSentForCurrentTarget = false;
            _noDamageTimeoutsOnCurrentTarget = 0;
            _attackProgressStartTicks = now;
            return;
        }

        TrackKillPredictionDrop(combat.TargetHpPercent);

        if (_bestObservedTargetHp is null || combat.TargetHpPercent < _bestObservedTargetHp.Value)
        {
            _bestObservedTargetHp = combat.TargetHpPercent;
            // 从未确认怪名时，OCR 假血条会在 7%~22% 间乱跳，不能当成真实掉血去重置无伤害计时，
            // 否则「弱目标续打」会永久占住循环、永远不选怪。
            if (HasConfirmedTargetIdentity())
            {
                _attackActionsOnCurrentTarget = 0;
                _noDamageTimeoutsOnCurrentTarget = 0;
                _attackProgressStartTicks = now;
            }
        }
    }

    private static bool IsLikelyNewSameNameTarget(int currentHpPercent, int bestObservedHpPercent) =>
        bestObservedHpPercent <= 35
        && currentHpPercent >= 60
        && currentHpPercent - bestObservedHpPercent >= 35;

    private bool ShouldContinueWeakTargetCombat(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        // Dead / empty corpse bar: never "continue", must switch.
        if (IsDeadOrEmptyTargetHp(combat))
            return false;

        if (!combat.HasTargetHp || combat.TargetHpPercent == 0)
            return false;

        if (combat.TargetLooksLikePlayer
            || IsAmbiguousPlayerProtectionEnabled(cfg, combat)
            || combat.TargetLooksLikePet
            || combat.TargetIsAttackable == false)
        {
            return false;
        }

        // KillThenSwitch combat lock: already engaged — keep output on this HP bar,
        // do NOT keep pressing 选怪 (would switch before the mob dies).
        if (IsCombatTargetLocked(cfg) && combat.TargetHpPercent is > 0)
            return true;

        // Ghost weak (never had a real name, not locked): pure-key will select once then lock.
        if (!HasConfirmedTargetIdentity())
            return false;

        // Named target later degraded to HP-only OCR: keep outputting until death/lost.
        if (_hadTarget || _attackSentForCurrentTarget || _attackActionsOnCurrentTarget > 0)
            return true;

        if (IsWithinAcquireGraceWindow(cfg))
            return true;

        return false;
    }

    private bool IsWithinAcquireGraceWindow(GlobalConfig cfg)
    {
        if (_lastAcquireTicks == 0)
            return false;

        var now = Environment.TickCount64;
        var acquireGraceMs = Math.Max(
            600,
            Math.Min(1800, cfg.Targeting.AcquireIntervalMs + ResolveAcquireFallbackAttackDelayMs(cfg) + cfg.Targeting.ScanIntervalMs * 3));
        return now - _lastAcquireTicks <= acquireGraceMs;
    }

    private bool ShouldUseSimplifiedKillSwitchLoop(GlobalConfig cfg) =>
        IsKillStyleMode(cfg.Targeting.SwitchMode)
        && (!string.IsNullOrWhiteSpace(cfg.AttackHotkey)
            || cfg.Skills.Any(skill => skill.Enabled && !string.IsNullOrWhiteSpace(skill.Hotkey)))
        && !IsGameBuiltInMonitorMode(cfg, _backend);

    /// <summary>
    /// Minimal UU-style kill-then-switch loop:
    /// Seeking = 选怪 + 出手 → Fighting = 只输出；
    /// 换怪条件 = 0%空血 / 无正血条持续 / 交战中血条长期不下降(OCR卡死) / 单次交战超时。
    /// 不依赖「面板彻底消失」。
    /// </summary>
    private void RunSimplifiedKillSwitchLoop(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        Machine.Goto(NormalHangState.SelectTarget);

        if (combat.TargetLooksLikePlayer
            || IsAmbiguousPlayerProtectionEnabled(cfg, combat)
            || combat.TargetLooksLikePet
            || combat.TargetIsAttackable == false)
        {
            EnterKillSwitchSeeking("保护目标，放弃当前交战");
            var decision = EvaluateTarget(cfg, combat);
            TraceDecision(CombatLoopDecision.For(
                decision.IsProtectedTarget ? CombatLoopDecisionKind.SkipProtectedTarget : CombatLoopDecisionKind.AcquireTarget,
                decision.Reason,
                willAcquire: decision.ShouldAcquire,
                willSkipAttack: true,
                isProtection: decision.IsProtectedTarget), combat);
            if (!decision.Allowed && decision.ShouldAcquire && ShouldAcquireAfterRejectedTarget(cfg))
                RecoverRejectedTarget(cfg, combat, decision);
            return;
        }

        if (TryHandleNameListTargetRejection(cfg, combat))
            return;

        var now = Environment.TickCount64;
        UpdateZeroOrEmptyStreak(combat);

        if (_killSwitchPhase != KillSwitchPhase.Fighting
            && combat.TargetHpPercent is not 0
            && RequiresRecognizedTarget(cfg))
        {
            var strategyDecision = EvaluateTarget(cfg, combat);
            if (!strategyDecision.Allowed)
            {
                EnterKillSwitchAwaitingTarget(strategyDecision.Reason, markPostDeath: false);
                var switched = cfg.Targeting.AutoAcquireWhenNoTarget
                               && TryAcquireTarget(
                                   cfg,
                                   strategyDecision.AcquireReason,
                                   bypassGlobalCooldown: true,
                                   bypassAcquireInterval: true,
                                   armFallbackAttack: true);
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.AcquireTarget,
                    strategyDecision.Reason,
                    willAcquire: switched,
                    willSkipAttack: true), combat);
                return;
            }
        }

        // Only auto-lock Seeking→Fighting on a live HP fill.
        // Name-only does NOT full-lock here: residual corpse name after kill would re-lock
        // and stall ~5s. Chase is held in Seeking (skip F7 + keep Q) until attack engages.
        var enteredFightingThisFrame = false;
        if (_killSwitchPhase != KillSwitchPhase.Fighting
            && IsLiveTargetHp(combat.TargetHpPercent)
            && combat.TargetHpPercent is not 0)
        {
            _killSwitchPhase = KillSwitchPhase.Fighting;
            _awaitingTargetSinceTicks = 0;
            _fightStartedTicks = now;
            _lastHpProgressTicks = now;
            _bestFightHpPercent = combat.TargetHpPercent;
            _deadOrEmptyHpStreak = 0;
            _combatTargetLocked = true;
            _hadTarget = true;
            if (!string.IsNullOrWhiteSpace(combat.TargetNameText))
                _lastTargetText = combat.TargetNameText;
            enteredFightingThisFrame = true;
            Log.Info(Name, $"检测到存活目标血条({combat.TargetHpPercent}%)，直接进入交战，不重复选怪。", Target?.Id ?? "-");
        }

        if (_killSwitchPhase == KillSwitchPhase.AwaitingTarget)
        {
            if (TrySendPendingAcquireAttack(cfg, combat))
                return;

            var waitAgeMs = Math.Max(0, now - _awaitingTargetSinceTicks);
            var retryIntervalMs = ResolveTargetResponseRetryMs(cfg);
            var responseTimeoutMs = Math.Max(1800, retryIntervalMs + 600);
            if (waitAgeMs >= responseTimeoutMs)
            {
                EnterKillSwitchSeeking($"新目标血条未在 {responseTimeoutMs}ms 内刷新，退化为基础追击");
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.TargetLostWait,
                    "切怪后未确认新目标血条，下一帧退化为基础追击。",
                    willSkipAttack: true), combat);
                return;
            }

            var retried = cfg.Targeting.AutoAcquireWhenNoTarget
                          && (_lastAcquireTicks == 0 || now - _lastAcquireTicks >= retryIntervalMs)
                          && TryAcquireTarget(
                              cfg,
                              "等待新目标血条，重试选怪",
                              bypassGlobalCooldown: true,
                              bypassAcquireInterval: true,
                              armFallbackAttack: true);
            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.TargetLostWait,
                "已切换死亡目标，等待新目标正血帧；本帧禁止攻击旧尸体。",
                willAcquire: retried,
                willSkipAttack: true), combat,
                minIntervalMs: 300);
            return;
        }

        var switchReason = ResolveKillSwitchExitReason(cfg, combat, now);
        if (switchReason is not null)
        {
            EnterKillSwitchAwaitingTarget(switchReason, markPostDeath: true);
            // Always 选怪 on confirmed death/empty — do not wait for panel to vanish.
            var switched = false;
            if (cfg.Targeting.AutoAcquireWhenNoTarget)
            {
                switched = TryAcquireTarget(
                    cfg,
                    switchReason,
                    bypassGlobalCooldown: true,
                    bypassAcquireInterval: true,
                    armFallbackAttack: true);
            }

            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.TargetDeadSwitch,
                switchReason + (switched ? "，已发送选怪。" : "，选怪未发出。"),
                willAcquire: switched,
                willSkipAttack: true), combat);
            return;
        }

        if (_killSwitchPhase == KillSwitchPhase.Fighting)
        {
            if (ShouldSwitchStalledKillTarget(cfg, combat, now, out var stalledReason))
            {
                EnterKillSwitchAwaitingTarget(stalledReason, markPostDeath: false);
                var switched = cfg.Targeting.AutoAcquireWhenNoTarget
                               && TryAcquireTarget(
                                   cfg,
                                   stalledReason,
                                   bypassGlobalCooldown: true,
                                   bypassAcquireInterval: true,
                                   armFallbackAttack: true);
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.NoDamageReacquire,
                    stalledReason,
                    willAcquire: switched,
                    willSkipAttack: true), combat);
                return;
            }

            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.ContinueWeakTarget,
                $"交战中持续输出（phase=Fighting，hp={PercentText(combat.TargetHpPercent)}），不选怪。",
                willAttack: true,
                willCastSkill: true), combat);
            TrackTargetAttackProgress(combat);
            if (TryRunEstablishedCombatRecoveryActions(cfg, combat))
                return;

            var shouldPredictSwitch = ShouldPredictSwitchAfterFinalAction(cfg, combat, out var predictReason);
            AttackHotkeyResult fightAttack;
            bool fightSkill;
            if (_attackSentForCurrentTarget)
            {
                // After the opening normal attack, Ready skills own the next scan. Only
                // repeat Q when no visual/timer skill was dispatched, otherwise Q would
                // win every slow inference scan and permanently starve the skill loop.
                fightSkill = TrySendDueSkill(cfg, combat, SkillCastScope.CombatOnly);
                fightAttack = fightSkill
                    ? AttackHotkeyResult.AlreadySentForTarget
                    : TrySendAttackHotkey(
                        cfg,
                        allowRepeatOnSameTarget: true,
                        bypassIntervalIfFirstAttack: true,
                        forceImmediate: enteredFightingThisFrame || ShouldForceOpeningAttack());
            }
            else
            {
                fightAttack = TrySendAttackHotkey(
                    cfg,
                    allowRepeatOnSameTarget: true,
                    bypassIntervalIfFirstAttack: true,
                    forceImmediate: enteredFightingThisFrame || ShouldForceOpeningAttack());
                fightSkill = TrySendDueSkillAfterAttack(cfg, combat, fightAttack);
            }
            Machine.Goto(NormalHangState.AttackLoop);
            NoteKillSwitchEngagement(fightAttack, fightSkill, combat, now);
            if (shouldPredictSwitch && (fightAttack == AttackHotkeyResult.Sent || fightSkill))
            {
                _lastPredictiveSwitchTicks = Environment.TickCount64;
                EnterKillSwitchAwaitingTarget(predictReason, markPostDeath: true);
                var switched = cfg.Targeting.AutoAcquireWhenNoTarget
                               && TryAcquireTarget(
                                   cfg,
                                   predictReason,
                                   bypassGlobalCooldown: true,
                                   bypassAcquireInterval: true,
                                   armFallbackAttack: true);
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.TargetDeadSwitch,
                    predictReason + (switched ? "，已提前切换。" : "，但选怪未发出。"),
                    willAcquire: switched,
                    willAttack: true,
                    willCastSkill: true), combat);
            }
            NotePlannedAttackOutcome("交战输出", fightAttack, fightSkill, cfg, combat);
            return;
        }

        // Seeking: live bar = hold. Name hold only outside post-death window (corpse OCR lag).
        // Far mobs: F7 once + Q walk-in; do not F7-thrash while name still shows.
        var acquired = false;
        var hasLiveBar = IsLiveTargetHp(combat.TargetHpPercent);
        var inPostDeath = now < _postDeathIgnoreNameLockUntil;
        var hasNamedHold = !inPostDeath && combat.HasTargetName && combat.TargetHpPercent is not 0;
        var needsAcquire = cfg.Targeting.AutoAcquireWhenNoTarget && !hasLiveBar && !hasNamedHold;
        if (needsAcquire)
        {
            acquired = TryAcquireTarget(cfg, "寻找目标", armFallbackAttack: true);
            if (!acquired && IsAcquireHotkeyMissing(cfg))
            {
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.AcquireTarget,
                    "需要选怪但选怪热键为空，本轮不发送普攻。",
                    willSkipAttack: true), combat);
                return;
            }
        }
        else if (hasLiveBar || hasNamedHold)
            Log.Trace(Name, hasLiveBar
                ? "寻找目标：已有存活血条，跳过选怪直接输出。"
                : "寻找目标：已有目标名，跳过选怪（追击进攻击距离）。", Target?.Id ?? "-");

        TraceDecision(CombatLoopDecision.For(
            CombatLoopDecisionKind.PureKeyBaseline,
            acquired ? "寻找目标：已选怪，开始输出。" : "寻找目标：输出并等待锁定。",
            willAcquire: acquired,
            willAttack: true,
            willCastSkill: true), combat);
        var seekAttack = TrySendAttackHotkey(
            cfg,
            allowRepeatOnSameTarget: true,
            bypassIntervalIfFirstAttack: true,
            forceImmediate: ShouldForceOpeningAttack());
        Machine.Goto(NormalHangState.AttackLoop);
        var seekSkill = TrySendDueSkillAfterAttack(cfg, combat, seekAttack);
        // Only lock when we actually see a live bar or just successfully engaged.
        if (IsLiveTargetHp(combat.TargetHpPercent) || seekAttack == AttackHotkeyResult.Sent || seekSkill)
            NoteKillSwitchEngagement(seekAttack, seekSkill, combat, now);
        NotePlannedAttackOutcome("寻找目标出手", seekAttack, seekSkill, cfg, combat);
    }

    /// <summary>Any positive fill is alive. Confirmed empty HP-slot frames are fused to 0% upstream.</summary>
    private static bool IsLiveTargetHp(int? hp) => hp is > 0;

    /// <summary>Only confirmed empty or missing HP is dead-ish; low positive HP remains attackable.</summary>
    private static bool IsDeadishTargetHp(int? hp) => hp is null or <= 0;

    private bool ShouldSwitchStalledKillTarget(
        GlobalConfig cfg,
        CombatRecognitionResult combat,
        long now,
        out string reason)
    {
        reason = string.Empty;
        if (!cfg.Targeting.AutoAcquireWhenNoTarget
            || _killSwitchPhase != KillSwitchPhase.Fighting
            || combat.TargetHpPercent is not > 0
            || _lastHpProgressTicks == 0
            || _attackActionsOnCurrentTarget < 3)
        {
            return false;
        }

        var timeoutMs = ResolveNoDamageReacquireMs(cfg);
        if (combat.TargetSampleKind.Equals("boss", StringComparison.OrdinalIgnoreCase)
            || combat.TargetSampleKind.Equals("elite", StringComparison.OrdinalIgnoreCase))
        {
            timeoutMs = Math.Min(120000, timeoutMs * 2);
        }

        var stalledMs = now - _lastHpProgressTicks;
        if (stalledMs < timeoutMs)
            return false;

        reason = $"目标连续 {stalledMs / 1000.0:0.#} 秒未掉血，疑似被障碍物卡住，切换其他目标。";
        return true;
    }

    private string? ResolveKillSwitchExitReason(GlobalConfig cfg, CombatRecognitionResult combat, long now)
    {
        if (_killSwitchPhase != KillSwitchPhase.Fighting)
            return null;

        // 死后空血 / 明确 0%：本帧立刻换怪，不看目标头像框是否还在。
        if (combat.TargetHpPercent is 0)
            return "目标血量为 0（死后空血），立即换怪";

        var sawLiveHp = _bestFightHpPercent is > 0;
        var noPositiveHp = combat.TargetHpPercent is not > 0;

        // Confirmed empty is mapped to 0 upstream and handled above. A null value means
        // the model did not produce a life result, so require two scans to avoid jitter.
        if (sawLiveHp && noPositiveHp && _deadOrEmptyHpStreak >= 2)
            return "目标血条连续两帧不可用，立即重新选怪";

        // 从未见过正血（追击进距离）：仅在框彻底没了或超时才重选，避免走路时狂 F7。
        if (!sawLiveHp && noPositiveHp && _fightStartedTicks != 0)
        {
            var fightAgeMs = now - _fightStartedTicks;
            if (!combat.HasTargetName && fightAgeMs >= 200 && _deadOrEmptyHpStreak >= 1)
                return "目标框已消失，切换下一怪";
            if (fightAgeMs >= 1500 && _deadOrEmptyHpStreak >= 3)
                return "追击超时仍无血条，重新选怪";
        }

        return null;
    }

    private void UpdateZeroOrEmptyStreak(CombatRecognitionResult combat)
    {
        // Count only confirmed empty (0%) or missing HP. Positive low HP is still alive.
        if (IsDeadishTargetHp(combat.TargetHpPercent))
            _deadOrEmptyHpStreak++;
        else
            _deadOrEmptyHpStreak = 0;
    }

    private bool TryHandleNameListTargetRejection(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (cfg.Targeting.Policy?.Enabled == true)
            return TryHandleUnifiedTargetPolicy(cfg, combat);

        var hasBlacklist = !string.IsNullOrWhiteSpace(cfg.Targeting.TargetNameBlacklist);
        var bossWhitelistMode = cfg.Targeting.SwitchMode == TargetSwitchMode.BossWhitelist;
        if (!hasBlacklist && !bossWhitelistMode)
            return false;

        if (_killSwitchPhase == KillSwitchPhase.AwaitingTarget
            && !IsLiveTargetHp(combat.TargetHpPercent))
        {
            return false;
        }

        LogTargetNameListDiagnosticIfDue(cfg, combat);

        if (MatchesAnyTargetName(cfg.Targeting.TargetNameBlacklist, combat.TargetNameText))
        {
            EnterKillSwitchAwaitingTarget("目标命中黑名单，切换目标", markPostDeath: false);
            var retryIntervalMs = ResolveTargetResponseRetryMs(cfg);
            var now = Environment.TickCount64;
            var switched = cfg.Targeting.AutoAcquireWhenNoTarget
                           && (_lastAcquireTicks == 0 || now - _lastAcquireTicks >= retryIntervalMs)
                           && TryAcquireTarget(
                               cfg,
                               "目标命中黑名单，重新选怪",
                               bypassGlobalCooldown: true,
                               bypassAcquireInterval: true,
                               armFallbackAttack: false);
            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.AcquireTarget,
                $"目标名称命中黑名单：{Blank(combat.TargetNameText)}",
                willAcquire: switched,
                willSkipAttack: true), combat);
            return true;
        }

        if (bossWhitelistMode && !MatchesAnyTargetName(cfg.Targeting.TargetNameWhitelist, combat.TargetNameText))
        {
            EnterKillSwitchAwaitingTarget("BOSS白名单未命中，切换目标", markPostDeath: false);
            var retryIntervalMs = ResolveTargetResponseRetryMs(cfg);
            var now = Environment.TickCount64;
            var switched = cfg.Targeting.AutoAcquireWhenNoTarget
                           && (_lastAcquireTicks == 0 || now - _lastAcquireTicks >= retryIntervalMs)
                           && TryAcquireTarget(
                               cfg,
                               "BOSS白名单未命中，重新选怪",
                               bypassGlobalCooldown: true,
                               bypassAcquireInterval: true,
                               armFallbackAttack: false);
            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.AcquireTarget,
                $"BOSS白名单未命中：当前={Blank(combat.TargetNameText)}，白名单={cfg.Targeting.TargetNameWhitelist}",
                willAcquire: switched,
                willSkipAttack: true), combat);
            return true;
        }

        return false;
    }

    private bool TryHandleUnifiedTargetPolicy(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (_killSwitchPhase == KillSwitchPhase.AwaitingTarget
            && !IsLiveTargetHp(combat.TargetHpPercent)
            && !combat.HasTargetName)
        {
            return false;
        }

        var requiresName = TargetPolicyRuntime.RequiresTargetName(cfg.Targeting);
        if (requiresName && !combat.HasTargetName && !HasConfirmedTargetIdentity())
        {
            if (!combat.HasTarget)
            {
                ResetTargetPolicyNameState();
                return false;
            }

            var now = Environment.TickCount64;
            if (_targetPolicyNameWaitStartedTicks == 0)
                _targetPolicyNameWaitStartedTicks = now;

            var waitMs = Math.Clamp(Math.Max(1, cfg.Targeting.ScanIntervalMs) * 4, 240, 500);
            if (now - _targetPolicyNameWaitStartedTicks < waitMs)
            {
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.NoActionDiagnosis,
                    "目标名称尚未确认，名单判定前禁止首攻。",
                    willSkipAttack: true), combat, minIntervalMs: 150);
                return true;
            }

            EnterKillSwitchAwaitingTarget("目标名称识别超时，按名单保护切换目标", markPostDeath: false);
            var switched = cfg.Targeting.AutoAcquireWhenNoTarget
                           && TryAcquireTarget(
                               cfg,
                               "目标名称识别超时，重新选怪",
                               bypassGlobalCooldown: true,
                               bypassAcquireInterval: true,
                               armFallbackAttack: false);
            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.AcquireTarget,
                "目标名称未确认，禁止攻击并切换目标。",
                willAcquire: switched,
                willSkipAttack: true), combat);
            return true;
        }

        var policyCombat = combat;
        if (!combat.HasTargetName && HasConfirmedTargetIdentity())
            policyCombat = combat with { TargetNameText = _lastTargetText };

        var decision = EvaluateTarget(cfg, policyCombat);
        if (!decision.Allowed)
        {
            EnterKillSwitchAwaitingTarget(decision.Reason, markPostDeath: false);
            var switched = decision.ShouldAcquire
                           && cfg.Targeting.AutoAcquireWhenNoTarget
                           && TryAcquireTarget(
                               cfg,
                               decision.AcquireReason,
                               bypassGlobalCooldown: true,
                               bypassAcquireInterval: true,
                               armFallbackAttack: false);
            TraceDecision(CombatLoopDecision.For(
                decision.IsProtectedTarget
                    ? CombatLoopDecisionKind.SkipProtectedTarget
                    : CombatLoopDecisionKind.AcquireTarget,
                decision.Reason,
                willAcquire: switched,
                willSkipAttack: true,
                isProtection: decision.IsProtectedTarget), combat);
            return true;
        }

        if (!combat.HasTargetName)
            return false;

        var normalizedName = TargetNamePolicyMatcher.Normalize(combat.TargetNameText);
        if (!HasConfirmedTargetIdentity() && requiresName)
        {
            if (!string.Equals(normalizedName, _targetPolicyNameCandidate, StringComparison.OrdinalIgnoreCase))
            {
                _targetPolicyNameCandidate = normalizedName;
                _targetPolicyNameCandidateFrames = 1;
            }
            else
            {
                _targetPolicyNameCandidateFrames++;
            }

            if (_targetPolicyNameCandidateFrames < 2)
            {
                TraceDecision(CombatLoopDecision.For(
                    CombatLoopDecisionKind.NoActionDiagnosis,
                    $"目标名称首次识别为“{combat.TargetNameText.Trim()}”，等待下一帧确认后再攻击。",
                    willSkipAttack: true), combat, minIntervalMs: 100);
                return true;
            }
        }

        _lastTargetText = combat.TargetNameText.Trim();
        ResetTargetPolicyNameState();
        return false;
    }

    private void ResetTargetPolicyNameState()
    {
        _targetPolicyNameCandidate = "";
        _targetPolicyNameCandidateFrames = 0;
        _targetPolicyNameWaitStartedTicks = 0;
    }

    private void LogTargetNameListDiagnosticIfDue(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        var now = Environment.TickCount64;
        if (_lastTargetNameListDiagnosticTicks != 0 && now - _lastTargetNameListDiagnosticTicks < 1500)
            return;

        _lastTargetNameListDiagnosticTicks = now;
        var name = string.IsNullOrWhiteSpace(combat.TargetNameText) ? "未读到" : combat.TargetNameText.Trim();
        Log.Info(
            Name,
            $"目标名称识别：{name}；黑名单={Blank(cfg.Targeting.TargetNameBlacklist)}；白名单={Blank(cfg.Targeting.TargetNameWhitelist)}。",
            Target?.Id ?? "-");
    }

    private static bool MatchesAnyTargetName(string list, string targetName)
    {
        if (string.IsNullOrWhiteSpace(list) || string.IsNullOrWhiteSpace(targetName))
            return false;

        var normalizedTarget = NormalizeTargetName(targetName);
        return SplitTargetNamesForMatch(list)
            .Any(name => normalizedTarget.Contains(NormalizeTargetName(name), StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitTargetNamesForMatch(string list) =>
        list.Split(new[] { ',', '，', ';', '；', '|', '/', '\\', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);

    private static string NormalizeTargetName(string value) =>
        new(value.Where(ch => !char.IsWhiteSpace(ch) && ch is not '·' and not '•' and not '。').ToArray());

    private void NoteKillSwitchEngagement(
        AttackHotkeyResult attackResult,
        bool skillSent,
        CombatRecognitionResult combat,
        long now)
    {
        if (attackResult != AttackHotkeyResult.Sent && !skillSent)
            return;

        // Refuse lock on confirmed 0% corpse.
        if (combat.TargetHpPercent is 0)
            return;

        // Right after death-switch: residual corpse name + null HP must not re-lock Fighting.
        // Only a real live bar may re-engage during this window.
        if (now < _postDeathIgnoreNameLockUntil && !IsLiveTargetHp(combat.TargetHpPercent))
            return;

        if (_killSwitchPhase != KillSwitchPhase.Fighting)
        {
            _killSwitchPhase = KillSwitchPhase.Fighting;
            _fightStartedTicks = now;
            _lastHpProgressTicks = now;
            _bestFightHpPercent = combat.TargetHpPercent is > 0 ? combat.TargetHpPercent : null;
            _deadOrEmptyHpStreak = 0;
            if (!string.IsNullOrWhiteSpace(combat.TargetNameText))
                _lastTargetText = combat.TargetNameText;
            Log.Info(
                Name,
                combat.TargetHpPercent is > 0
                    ? "进入交战锁定（Fighting）：停止选怪，直到空血/0% 换怪。"
                    : "进入交战锁定（Fighting/追击）：暂无血条，停止选怪，走近攻击距离后再判定空血。",
                Target?.Id ?? "-");
        }
        else if (combat.TargetHpPercent is int hp && IsLiveTargetHp(hp))
        {
            if (_bestFightHpPercent is null || hp < _bestFightHpPercent.Value)
            {
                _bestFightHpPercent = hp;
                _lastHpProgressTicks = now;
            }
        }

        _combatTargetLocked = true;
        _combatLockStartedTicks = _fightStartedTicks;
        _hadTarget = true;
    }

    private void EnterKillSwitchSeeking(string reason, bool markPostDeath = false)
    {
        if (_killSwitchPhase != KillSwitchPhase.Seeking)
            Log.Info(Name, $"退出交战锁定 → 寻找目标：{reason}", Target?.Id ?? "-");

        _killSwitchPhase = KillSwitchPhase.Seeking;
        _fightStartedTicks = 0;
        _lastHpProgressTicks = 0;
        _bestFightHpPercent = null;
        _awaitingTargetSinceTicks = 0;
        _deadOrEmptyHpStreak = 0;
        _combatTargetLocked = false;
        _combatLockStartedTicks = 0;
        _hadTarget = false;
        _lastTargetText = "";
        ResetTargetPolicyNameState();
        if (markPostDeath)
        {
            // ~0.9s: ignore name-only hold/lock so corpse panel OCR cannot stall next 选怪.
            _postDeathIgnoreNameLockUntil = Environment.TickCount64 + 900;
        }
        else
        {
            _postDeathIgnoreNameLockUntil = 0;
        }

        ResetTargetAttackProgress();
    }

    private void EnterKillSwitchAwaitingTarget(string reason, bool markPostDeath)
    {
        EnterKillSwitchSeeking(reason, markPostDeath);
        _killSwitchPhase = KillSwitchPhase.AwaitingTarget;
        _awaitingTargetSinceTicks = Environment.TickCount64;
    }

    private static int ResolveTargetResponseRetryMs(GlobalConfig cfg) =>
        Math.Clamp(
            Math.Max(cfg.Targeting.AcquireIntervalMs, cfg.Targeting.ScanIntervalMs + 250),
            900,
            1200);

    private bool TryRunPureKeyBaselineCycle(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        // Grab / fallback path when simplified kill-switch is not used.
        if (!CanUsePureKeyBaseline(cfg))
            return false;

        if (!HasActionableTarget(combat)
            && cfg.Targeting.AutoAcquireWhenNoTarget
            && IsAcquireHotkeyMissing(cfg))
        {
            TryAcquireTarget(cfg, "纯按键选怪", armFallbackAttack: false);
            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.AcquireTarget,
                "需要选怪但选怪热键为空，本轮不发送普攻。",
                willSkipAttack: true), combat);
            return true;
        }

        if (combat.TargetLooksLikePlayer
            || IsAmbiguousPlayerProtectionEnabled(cfg, combat)
            || combat.TargetLooksLikePet
            || combat.TargetIsAttackable == false)
        {
            var decision = EvaluateTarget(cfg, combat);
            if (!decision.Allowed && decision.ShouldAcquire && ShouldAcquireAfterRejectedTarget(cfg))
                RecoverRejectedTarget(cfg, combat, decision);
            return true;
        }

        if (HasActionableTarget(combat) && HasStrongTargetSignal(combat))
            return false;

        var acquired = cfg.Targeting.AutoAcquireWhenNoTarget
                       && TryAcquireTarget(cfg, "纯按键选怪", armFallbackAttack: false);
        TraceDecision(CombatLoopDecision.For(
            CombatLoopDecisionKind.PureKeyBaseline,
            "OCR 未确认强目标，执行纯按键选怪/普攻/技能。",
            willAcquire: acquired,
            willAttack: true,
            willCastSkill: true), combat);
        var attackResult = TrySendAttackHotkey(
            cfg,
            allowRepeatOnSameTarget: true,
            bypassIntervalIfFirstAttack: true,
            forceImmediate: ShouldForceOpeningAttack());
        Machine.Goto(NormalHangState.AttackLoop);
        var skillSent = TrySendDueSkillAfterAttack(cfg, combat, attackResult);
        NotePlannedAttackOutcome("基础按键", attackResult, skillSent, cfg, combat);
        return true;
    }

    private bool IsGhostWeakHpOnly(CombatRecognitionResult combat) =>
        combat.HasTargetHp
        && !HasStrongTargetSignal(combat)
        && !HasConfirmedTargetIdentity()
        && _killSwitchPhase != KillSwitchPhase.Fighting;

    private bool IsCombatTargetLocked(GlobalConfig cfg) =>
        IsKillStyleMode(cfg.Targeting.SwitchMode)
        && (_combatTargetLocked || _killSwitchPhase == KillSwitchPhase.Fighting);

    private static bool IsDeadOrEmptyTargetHp(CombatRecognitionResult combat) =>
        combat.TargetHpPercent is 0;

    private void NoteCombatEngagement(AttackHotkeyResult attackResult, bool skillSent)
    {
        if (attackResult == AttackHotkeyResult.Sent || skillSent)
        {
            if (!_combatTargetLocked)
                _combatLockStartedTicks = Environment.TickCount64;
            _combatTargetLocked = true;
            _hadTarget = true;
            _deadOrEmptyHpStreak = 0;
        }
    }

    private void ClearCombatTargetLock()
    {
        _combatTargetLocked = false;
        _combatLockStartedTicks = 0;
        _deadOrEmptyHpStreak = 0;
        if (_killSwitchPhase == KillSwitchPhase.Fighting)
            EnterKillSwitchSeeking("清除锁定");
    }

    private bool CanUsePureKeyBaseline(GlobalConfig cfg) =>
        !IsGameBuiltInMonitorMode(cfg, _backend)
        && !RequiresRecognizedTarget(cfg)
        && (!string.IsNullOrWhiteSpace(cfg.AttackHotkey)
            || cfg.Skills.Any(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Hotkey) && s.Condition == SkillCondition.Always));

    private void ContinueWeakTargetCombat(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        // Sticky while named OR combat-locked (kill-then-switch mid-fight without OCR name).
        if (HasConfirmedTargetIdentity() || IsCombatTargetLocked(cfg))
            _hadTarget = true;
        if (combat.TargetHpPercent is not null && _bestObservedTargetHp is null)
            _bestObservedTargetHp = combat.TargetHpPercent;

        LogCombatSnapshotIfDue(combat, "弱识别目标，按当前目标继续输出");
        TraceDecision(CombatLoopDecision.For(
            CombatLoopDecisionKind.ContinueWeakTarget,
            IsCombatTargetLocked(cfg) && !HasConfirmedTargetIdentity()
                ? "打死换怪锁定中：弱血条继续输出，不重选。"
                : "曾确认怪名后仅剩弱血条，继续输出当前目标。",
            willAttack: true,
            willCastSkill: true), combat);
        TrackTargetAttackProgress(combat);
        if (TryRunEstablishedCombatRecoveryActions(cfg, combat))
            return;

        var attackResult = TrySendAttackHotkey(
            cfg,
            allowRepeatOnSameTarget: ShouldRepeatAttackOnWeakTarget(cfg),
            bypassIntervalIfFirstAttack: true);
        Machine.Goto(NormalHangState.AttackLoop);
        var skillSent = TrySendDueSkillAfterAttack(cfg, combat, attackResult);
        NoteCombatEngagement(attackResult, skillSent);
        NotePlannedAttackOutcome("弱目标续打", attackResult, skillSent, cfg, combat);
    }

    private bool ShouldRepeatAttackOnWeakTarget(GlobalConfig cfg) =>
        // 抢怪：每轮出手。锁定输出/无怪名交战时也要按间隔普攻，否则只放技能。
        cfg.Targeting.SwitchMode == TargetSwitchMode.Grab
        || IsCombatTargetLocked(cfg)
        || !HasConfirmedTargetIdentity();

    private bool ShouldForceWeakTargetReacquire(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (!cfg.Targeting.AutoAcquireWhenNoTarget)
            return false;

        if (!IsWeakTargetContinuation(combat))
            return false;

        // 打死换怪 + 已交战锁定：等血条死亡/面板丢失，不因 OCR 不掉血就切怪。
        if (IsKillStyleMode(cfg.Targeting.SwitchMode)
            && (HasConfirmedTargetIdentity() || IsCombatTargetLocked(cfg)))
        {
            return false;
        }

        // 仅在「曾确认怪名」时相信掉血；纯弱血条的百分比抖动不能阻止重选。
        var damageObserved = HasConfirmedTargetIdentity()
            && _attackActionsOnCurrentTarget > 0
            && combat.TargetHpPercent is not null
            && _bestObservedTargetHp is not null
            && combat.TargetHpPercent.Value < _bestObservedTargetHp.Value;
        if (damageObserved)
            return false;

        if (_attackActionsOnCurrentTarget <= 0 && !_attackSentForCurrentTarget)
            return false;

        var now = Environment.TickCount64;
        var timeoutMs = ResolveWeakTargetReacquireMs(cfg, combat);
        if (_attackProgressStartTicks == 0 || now - _attackProgressStartTicks < timeoutMs)
            return false;

        if (ShouldHoldForNoDamageDiagnosis(cfg, now, timeoutMs, "弱血条目标"))
            return false;

        Log.Warn(
            Name,
            $"目标持续 {timeoutMs / 1000.0:0.#} 秒只有弱血条且未观察到掉血；按目标无效处理并重新选怪。",
            Target?.Id ?? "-");
        _hadTarget = false;
        _lastTargetText = "";
        ClearCombatTargetLock();
        ResetTargetAttackProgress();
        return true;
    }

    private bool HasConfirmedTargetIdentity() =>
        !string.IsNullOrWhiteSpace(_lastTargetText);

    private bool IsWeakTargetContinuation(CombatRecognitionResult combat) =>
        _hadTarget && combat.HasTargetHp && !HasStrongTargetSignal(combat);

    private bool IsUnconfirmedWeakTarget(CombatRecognitionResult combat) =>
        combat.HasTargetHp
        && !HasStrongTargetSignal(combat);

    private bool ShouldSwitchProbablyUnattackableTarget(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (IsKillStyleMode(cfg.Targeting.SwitchMode))
            return false;

        if (!cfg.Targeting.AutoAcquireWhenNoTarget || !HasActionableTarget(combat) || combat.TargetHpPercent is null || _bestObservedTargetHp is null)
            return false;

        if (!_attackSentForCurrentTarget && _attackActionsOnCurrentTarget == 0)
            return false;

        var now = Environment.TickCount64;
        var timeoutMs = ResolveNoDamageReacquireMs(cfg);
        if (now - _attackProgressStartTicks < timeoutMs)
            return false;
        if (_lastUnattackableSwitchTicks != 0 && now - _lastUnattackableSwitchTicks < Math.Min(5000, timeoutMs / 2))
            return false;

        if (combat.TargetHpPercent < _bestObservedTargetHp.Value)
            return false;

        if (ShouldHoldForNoDamageDiagnosis(cfg, now, timeoutMs, "当前目标"))
            return false;

        _lastUnattackableSwitchTicks = now;
        Log.Warn(Name, $"目标疑似不可攻击：普攻/技能后 {timeoutMs / 1000.0:0.#} 秒血条无变化，已重新选怪。", Target?.Id ?? "-");
        ResetTargetAttackProgress();
        return true;
    }

    private bool ShouldHoldForNoDamageDiagnosis(GlobalConfig cfg, long now, int timeoutMs, string targetLabel)
    {
        // P0 简化：不再保留目标"诊断一轮"，直接允许重选。
        // 保留日志输出用于事后诊断。
        if (_noDamageTimeoutsOnCurrentTarget > 0)
            return false;

        _noDamageTimeoutsOnCurrentTarget++;

        var inputReason = cfg.InputDispatchMode switch
        {
            InputDispatchMode.PostMessage =>
                "仅后台输入疑似未被客户端接收：请切到自动兼容或仅前台验证，或查看输入诊断里的发送结果。",
            InputDispatchMode.Adaptive =>
                "自动兼容模式下打怪按键仍为后台投递：优先检查客户端是否接收按键、距离、目标状态和血条识别。",
            _ =>
                "前台输入仍无伤害：优先检查距离、目标是否不可攻击、技能是否命中、目标血条识别是否稳定。"
        };

        Log.Warn(
            Name,
            $"{targetLabel}已连续 {timeoutMs / 1000.0:0.#} 秒未观察到掉血；直接重选目标。{inputReason}",
            Target?.Id ?? "-");
        return false;
    }

    private static int ResolveNoDamageReacquireMs(GlobalConfig cfg) =>
        Math.Clamp(cfg.Targeting.NoDamageReacquireMs <= 0 ? 15000 : cfg.Targeting.NoDamageReacquireMs, 3000, 120000);

    private int ResolveWeakTargetReacquireMs(GlobalConfig cfg, CombatRecognitionResult? combat = null)
    {
        if (cfg.Targeting.SwitchMode == TargetSwitchMode.Grab)
        {
            return Math.Clamp(
                cfg.Targeting.AcquireIntervalMs + ResolveAcquireFallbackAttackDelayMs(cfg) + cfg.Targeting.ScanIntervalMs * 5,
                900,
                2500);
        }

        var baseMs = ResolveNoDamageReacquireMs(cfg);

        // 从未确认名字的纯弱血条：用更短超时（默认最多 5s），避免假 1% 血条卡死整轮挂机。
        if (!HasConfirmedTargetIdentity())
        {
            var ghostCapMs = combat?.TargetHpPercent is <= 5 ? 4000 : 5000;
            return Math.Clamp(Math.Min(baseMs, ghostCapMs), 3000, 120000);
        }

        return baseMs;
    }

    private void ResetTargetAttackProgress()
    {
        _attackProgressTargetKey = string.Empty;
        _bestObservedTargetHp = null;
        ResetKillPrediction(null);
        _attackActionsOnCurrentTarget = 0;
        _attackSentForCurrentTarget = false;
        _noDamageTimeoutsOnCurrentTarget = 0;
        _plannedAttackNoActionScans = 0;
        _attackProgressStartTicks = 0;
        _pendingAcquireAttackTicks = 0;
        _firstAttackPendingAfterAcquire = false;
        _pendingOpeningSkillDispatch = null;
    }

    private void ResetKillPrediction(int? currentHp)
    {
        _recentTargetHpDrops.Clear();
        _lastPredictionHp = currentHp;
        _predictionActionsOnCurrentTarget = 0;
        _predictionActionsSinceHpSample = 0;
    }

    private void TrackKillPredictionDrop(int? currentHp)
    {
        if (currentHp is null)
            return;

        if (_lastPredictionHp is int previous && currentHp.Value != previous)
        {
            var drop = previous - currentHp.Value;
            if (drop is > 0 and <= 80 && _predictionActionsSinceHpSample > 0)
            {
                var perActionDrop = Math.Max(1, (int)Math.Round(drop / (double)_predictionActionsSinceHpSample));
                _recentTargetHpDrops.Enqueue(perActionDrop);
                while (_recentTargetHpDrops.Count > 5)
                    _recentTargetHpDrops.Dequeue();
            }

            _predictionActionsSinceHpSample = 0;
        }

        _lastPredictionHp = currentHp;
    }

    private bool ShouldPredictSwitchAfterFinalAction(GlobalConfig cfg, CombatRecognitionResult combat, out string reason)
    {
        reason = string.Empty;
        if (cfg.Targeting.DeathSwitchStrategy != DeathSwitchStrategy.Predictive)
            return false;

        if (cfg.Targeting.SwitchMode == TargetSwitchMode.BossWhitelist
            || string.Equals(combat.TargetSampleKind, "boss", StringComparison.OrdinalIgnoreCase)
            || string.Equals(combat.TargetSampleKind, "elite", StringComparison.OrdinalIgnoreCase))
            return false;

        if (combat.TargetHpPercent is null or <= 0)
            return false;

        if (_recentTargetHpDrops.Count < 2 || _predictionActionsOnCurrentTarget < 2)
            return false;

        var now = Environment.TickCount64;
        if (_lastPredictiveSwitchTicks != 0 && now - _lastPredictiveSwitchTicks < 1000)
            return false;

        var averageDrop = _recentTargetHpDrops.Average();
        var predictedNextDamage = Math.Max(1, averageDrop * 1.15);
        var threshold = Math.Min(35, Math.Ceiling(predictedNextDamage) + 2);
        if (combat.TargetHpPercent.Value > threshold)
            return false;

        reason = $"预测换怪：当前血={combat.TargetHpPercent.Value}%，最近平均掉血={averageDrop:0.#}%，预计下一轮可击杀";
        return true;
    }

    private string TargetProgressKey(CombatRecognitionResult combat)
    {
        var name = string.IsNullOrWhiteSpace(combat.TargetNameText) ? "-" : combat.TargetNameText.Trim();
        if (name == "-" && _hadTarget && !string.IsNullOrWhiteSpace(_lastTargetText))
            return _lastTargetText.Trim();

        return name;
    }

    private void NoteAttackAction()
    {
        if (_attackProgressStartTicks == 0)
            _attackProgressStartTicks = Environment.TickCount64;
        _attackActionsOnCurrentTarget++;
        _predictionActionsOnCurrentTarget++;
        _predictionActionsSinceHpSample++;
        _plannedAttackNoActionScans = 0;
    }

    private void NotePlannedAttackOutcome(
        string context,
        AttackHotkeyResult attackResult,
        bool skillSent,
        GlobalConfig cfg,
        CombatRecognitionResult combat)
    {
        if (Target is null)
            return;

        if (attackResult == AttackHotkeyResult.Sent || skillSent)
        {
            RecordCombatTelemetry(
                "action",
                combat,
                cfg,
                context,
                attackResult: attackResult,
                skillSent: skillSent);
            _plannedAttackNoActionScans = 0;
            return;
        }

        if (attackResult is AttackHotkeyResult.WaitingInterval or AttackHotkeyResult.AlreadySentForTarget)
        {
            _plannedAttackNoActionScans = 0;
            return;
        }

        _plannedAttackNoActionScans++;
        // Hard send failures diagnose faster than soft "waiting" paths.
        var threshold = attackResult is AttackHotkeyResult.SendFailed or AttackHotkeyResult.InvalidHotkey or AttackHotkeyResult.MissingHotkey
            ? 3
            : Math.Max(3, Math.Min(8, 1000 / Math.Max(50, cfg.Targeting.ScanIntervalMs)));
        if (_plannedAttackNoActionScans < threshold)
            return;

        var now = Environment.TickCount64;
        if (_lastNoActionWatchdogTicks != 0 && now - _lastNoActionWatchdogTicks < 1500)
            return;

        _lastNoActionWatchdogTicks = now;
        Machine.Goto(NormalHangState.DiagnoseNoDamage);
        TraceDecision(CombatLoopDecision.For(
            CombatLoopDecisionKind.NoActionDiagnosis,
            $"{context}连续 {_plannedAttackNoActionScans} 轮计划攻击但没有成功普攻或技能。",
            willAttack: true,
            willCastSkill: true), combat);

        var executableSkills = GetExecutableSkills();
        var dueSkillCount = executableSkills.Count(skill => IsCombatSkillDueForDiagnostics(skill, cfg, combat, now));
        Log.Warn(
            Name,
            $"{context}连续 {_plannedAttackNoActionScans} 轮计划攻击但没有成功动作。普攻={Blank(cfg.AttackHotkey)}，普攻结果={AttackHotkeyResultText(attackResult)}，已发送首攻={_attackSentForCurrentTarget}，动作数={_attackActionsOnCurrentTarget}，可执行战斗技能={executableSkills.Count(s => s.Kind != SkillKind.Buff)}，疑似到期技能={dueSkillCount}，输入={cfg.InputDispatchMode}，目标血={PercentText(combat.TargetHpPercent)}。请检查热键、公共CD、输入通道或技能条件。",
            Target.Id);
        RecordCombatTelemetry(
            "no-action-diagnostic",
            combat,
            cfg,
            context,
            decision: CombatLoopDecision.For(
                CombatLoopDecisionKind.NoActionDiagnosis,
                $"{context}连续 {_plannedAttackNoActionScans} 轮计划攻击但没有成功动作。",
                willAttack: true,
                willCastSkill: true),
            attackResult: attackResult,
            skillSent: skillSent);
    }

    private void RecordCombatTelemetry(
        string eventName,
        CombatRecognitionResult? combat,
        GlobalConfig cfg,
        string context = "",
        CombatLoopDecision? decision = null,
        AttackHotkeyResult? attackResult = null,
        bool skillSent = false,
        string actions = "")
    {
        if (_telemetry is null || Target is null)
            return;

        _telemetry.Record(new CombatLoopTelemetryEntry
        {
            SchemaVersion = 2,
            Event = eventName,
            Controller = Name,
            Window = Target.Id,
            Pid = Target.Pid,
            ClientSize = Target.ClientSizeText,
            Mode = TargetSwitchModeText(cfg.Targeting.SwitchMode),
            InputMode = cfg.InputDispatchMode.ToString(),
            Decision = decision is null ? "" : DecisionKindText(decision.Kind),
            Reason = decision?.Reason ?? "",
            Actions = actions,
            Context = context,
            AttackResult = attackResult is null ? "" : AttackHotkeyResultText(attackResult.Value),
            SkillSent = skillSent,
            AttackSentForCurrentTarget = _attackSentForCurrentTarget,
            AttackActionsOnCurrentTarget = _attackActionsOnCurrentTarget,
            PlannedAttackNoActionScans = _plannedAttackNoActionScans,
            TargetName = combat?.TargetNameText ?? "",
            TargetHp = combat?.TargetHpPercent,
            PlayerHp = combat?.PlayerHpPercent,
            PlayerMp = combat?.PlayerMpPercent,
            PetHp = combat?.PetHpPercent,
            TargetLooksLikePlayer = combat?.TargetLooksLikePlayer ?? false,
            TargetProtectionAmbiguous = combat?.TargetProtectionAmbiguous ?? false,
            TargetLooksLikePet = combat?.TargetLooksLikePet ?? false,
            TargetIsAttackable = combat?.TargetIsAttackable,
            TargetHasSchoolMarker = combat?.TargetHasSchoolMarker ?? false,
            TargetHasPlayerPortrait = combat?.TargetHasPlayerPortrait ?? false,
            TargetHasPlayerStatusBars = combat?.TargetHasPlayerStatusBars ?? false,
            TargetSampleKind = combat?.TargetSampleKind ?? "",
            TargetSampleLabel = combat?.TargetSampleLabel ?? "",
            TargetSampleSource = combat?.TargetSampleSource ?? "",
            TargetSampleScore = combat?.TargetSampleScore ?? 0
        });
    }

    private bool IsCombatSkillDueForDiagnostics(SkillPreset skill, GlobalConfig cfg, CombatRecognitionResult combat, long now)
    {
        if (skill.Kind == SkillKind.Buff || !IsConditionExecutable(skill.Condition, cfg, combat))
            return false;

        if (!_lastSkillTicks.TryGetValue(skill.Id, out var last))
            return true;

        return now - last >= Math.Max(0, skill.IntervalMs);
    }

    private static string AttackHotkeyResultText(AttackHotkeyResult result) => result switch
    {
        AttackHotkeyResult.Sent => "已发送",
        AttackHotkeyResult.AlreadySentForTarget => "本目标已普攻",
        AttackHotkeyResult.WaitingInterval => "普攻间隔等待",
        AttackHotkeyResult.MissingHotkey => "未配置普攻",
        AttackHotkeyResult.InvalidHotkey => "普攻热键无效",
        AttackHotkeyResult.SendFailed => "输入发送失败",
        AttackHotkeyResult.NoTarget => "未绑定窗口",
        _ => result.ToString()
    };

    private bool IsTargetDead(CombatRecognitionResult combat)
    {
        // Planner death branch: engaged + 0% (empty corpse bar reads as 0 after OCR fix).
        if (combat.TargetHpPercent is not 0)
            return false;

        return HasConfirmedTargetIdentity()
               || HasStrongTargetSignal(combat)
               || IsCombatTargetLocked(_global.Current)
               || _attackSentForCurrentTarget
               || _attackActionsOnCurrentTarget > 0
               || _hadTarget;
    }

    private bool ShouldTreatTargetLost(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        // Kill-then-switch: still allow panel-lost after we had a real name
        // (name/panel gone while HP OCR is also gone). Ghost weak never uses this path.
        if (!_hadTarget && !HasConfirmedTargetIdentity())
            return false;

        if (HasActionableTarget(combat) || HasStrongTargetSignal(combat))
        {
            _targetLostStartedTicks = 0;
            return false;
        }

        // Still seeing HP-only after a confirmed name: keep fighting (weak continue),
        // do not treat as lost.
        if (HasConfirmedTargetIdentity() && combat.HasTargetHp)
        {
            _targetLostStartedTicks = 0;
            return false;
        }

        var now = Environment.TickCount64;
        var lostDelayMs = Math.Max(100, cfg.Targeting.TargetLostDelayMs);
        if (_targetLostStartedTicks == 0)
        {
            _targetLostStartedTicks = now;
            LogCombatSnapshotIfDue(combat, $"目标暂时丢失，等待 {lostDelayMs}ms 二次确认");
            return false;
        }

        return now - _targetLostStartedTicks >= lostDelayMs;
    }

    private TargetSafetyDecision EvaluateTarget(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        var decision = _targetSafety.Evaluate(cfg, combat, CurrentTargetSafetyOptions(cfg));
        if (decision.ResetTargetProgress)
            ResetTargetAttackProgress();

        if (!string.IsNullOrWhiteSpace(decision.WarningLabel))
            WarnProtectedTargetSkipped(decision.WarningLabel, decision.WarningDetail, combat);

        if (!string.IsNullOrWhiteSpace(decision.UnsupportedStrategyWarning))
            WarnUnsupportedTargetStrategyOnce(decision.UnsupportedStrategyWarning);

        if (!decision.Allowed && decision.Reason == "目标名称不匹配")
            Log.Trace(Name, "目标不匹配。", Target?.Id ?? "-");

        return decision;
    }

    private bool HasActionableTarget(CombatRecognitionResult combat) =>
        _targetSafety.HasActionableTarget(combat, CurrentTargetSafetyOptions(_global.Current));

    private bool HasStrongTargetSignal(CombatRecognitionResult combat) =>
        _targetSafety.HasStrongTargetSignal(combat);

    private bool RequiresRecognizedTarget(GlobalConfig cfg) =>
        _targetSafety.RequiresRecognizedTarget(cfg);

    private TargetSafetyOptions CurrentTargetSafetyOptions(GlobalConfig cfg)
    {
        var hasConfirmedIdentity = HasConfirmedTargetIdentity();
        // Weak HP continuation only after a real name was confirmed — never for noise bars.
        var allowWeakKnownTarget = hasConfirmedIdentity && !RequiresRecognizedTarget(cfg);
        return new(
            AllowWeakKnownTarget: allowWeakKnownTarget,
            HadTarget: _hadTarget || hasConfirmedIdentity,
            HasConfirmedIdentity: hasConfirmedIdentity,
            IncludeStrategyFilters: true);
    }

    private static bool ShouldAcquireAfterRejectedTarget(GlobalConfig cfg) =>
        cfg.Targeting.AutoAcquireWhenNoTarget;

    private void RecoverRejectedTarget(GlobalConfig cfg, CombatRecognitionResult combat, TargetSafetyDecision targetDecision)
    {
        if (!targetDecision.IsProtectedTarget)
        {
            ResetProtectedTargetRecovery();
            TryAcquireTarget(cfg, targetDecision.AcquireReason, armFallbackAttack: false);
            return;
        }

        TrackProtectedTargetRejection(combat);
        TryClearProtectedTargetIfNeeded(cfg, targetDecision);
        TryAcquireTarget(
            cfg,
            targetDecision.AcquireReason,
            bypassGlobalCooldown: true,
            bypassAcquireInterval: true,
            armFallbackAttack: false);
    }

    private void TrackProtectedTargetRejection(CombatRecognitionResult combat)
    {
        var key = ProtectedTargetKey(combat);
        if (!string.Equals(_lastProtectedTargetKey, key, StringComparison.Ordinal))
        {
            _lastProtectedTargetKey = key;
            _protectedTargetRejectScans = 0;
        }

        _protectedTargetRejectScans++;
    }

    private void TryClearProtectedTargetIfNeeded(GlobalConfig cfg, TargetSafetyDecision targetDecision)
    {
        if (Target is null)
            return;

        var clearHotkey = cfg.Targeting.ProtectedTargetClearHotkey?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(clearHotkey))
            return;

        var requiredScans = Math.Max(1, cfg.Targeting.ProtectedTargetClearAfterScans);
        if (_protectedTargetRejectScans < requiredScans)
            return;

        if (!Backend.TryParseHotkey(clearHotkey, out var error))
        {
            WarnThrottled("保护目标清除", $"保护目标清除键无效：{clearHotkey} | {error}", Target.Id);
            return;
        }

        var now = Environment.TickCount64;
        if (_lastProtectedTargetClearTicks != 0 && now - _lastProtectedTargetClearTicks < 1000)
            return;

        _lastProtectedTargetClearTicks = now;
        Log.Warn(
            Name,
            $"{targetDecision.Reason} 连续 {_protectedTargetRejectScans} 轮未切出，发送清目标键：{clearHotkey}，随后重新选怪。",
            Target.Id);
        CastHotkeyWithLog(clearHotkey, "保护目标清除");
        _protectedTargetRejectScans = 0;
    }

    private void ResetProtectedTargetRecovery()
    {
        _lastProtectedTargetKey = "";
        _protectedTargetRejectScans = 0;
    }

    private static string ProtectedTargetKey(CombatRecognitionResult combat)
    {
        var name = string.IsNullOrWhiteSpace(combat.TargetNameText) ? "-" : combat.TargetNameText.Trim();
        return $"{name}|player={combat.TargetLooksLikePlayer}|ambiguous={combat.TargetProtectionAmbiguous}|pet={combat.TargetLooksLikePet}|attackable={combat.TargetIsAttackable}";
    }

    private void WarnUnsupportedTargetStrategyOnce(string message)
    {
        if (_targetStrategyWarningWritten)
            return;

        _targetStrategyWarningWritten = true;
        Log.Warn(Name, message, Target?.Id ?? "-");
    }

    private void WarnProtectedTargetSkipped(string label, string detail, CombatRecognitionResult combat)
    {
        var now = Environment.TickCount64;
        if (_lastPlayerTargetWarningTicks != 0 && now - _lastPlayerTargetWarningTicks < 3000)
            return;

        _lastPlayerTargetWarningTicks = now;
        Log.Warn(Name, $"{label}：{detail}，已跳过攻击并重新选怪。判定={ProtectedTargetKey(combat)}", Target?.Id ?? "-");
    }

    private bool TryAcquireTarget(
        GlobalConfig cfg,
        string reason,
        bool bypassGlobalCooldown = false,
        bool bypassAcquireInterval = false,
        bool armFallbackAttack = true)
    {
        if (Target is null || !cfg.Targeting.AutoAcquireWhenNoTarget)
            return false;

        var hotkey = GetAcquireHotkey(cfg);
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            Log.Warn(Name, "已启用自动选怪，但选怪热键为空。", Target.Id);
            return false;
        }

        if (!Backend.TryParseHotkey(hotkey, out var error))
        {
            Log.Warn(Name, $"选怪热键无效：{hotkey} | {error}", Target.Id);
            return false;
        }

        var now = Environment.TickCount64;
        var acquireIntervalMs = bypassAcquireInterval ? 0 : Math.Max(0, cfg.Targeting.AcquireIntervalMs);
        if (_lastAcquireTicks != 0 && now - _lastAcquireTicks < acquireIntervalMs)
            return false;

        // Target switching has its own AcquireIntervalMs. Do not consume the skill/attack
        // global cooldown here, otherwise frequent target switching can starve attacks.
        Machine.Goto(NormalHangState.SelectTarget);
        _lastAcquireTicks = now;
        var cooldownNote = bypassGlobalCooldown || bypassAcquireInterval
            ? $"（强制换目标，间隔{acquireIntervalMs}ms）"
            : "";
        var inputVerb = cfg.InputDispatchMode == InputDispatchMode.SendInput ? "发送" : "投递";
        Log.Info(Name, $"{reason}，{inputVerb}选怪热键：{hotkey}{cooldownNote}", Target.Id);
        if (!CastHotkeyWithLog(hotkey, "选怪"))
        {
            _lastAcquireTicks = 0;
            return false;
        }

        _minimapFusion?.ReportTargetSearchAttempt(Target.Id);
        ResetTargetPolicyNameState();
        ResetTargetAttackProgress();
        _firstAttackPendingAfterAcquire = true;
        var fallbackDelayMs = 0;
        if (armFallbackAttack && HasAcquireAttackHotkey(cfg))
        {
            fallbackDelayMs = ResolveAcquireFallbackAttackDelayMs(cfg);
            _pendingAcquireAttackTicks = Environment.TickCount64 + fallbackDelayMs;
        }

        UserDiagnosticThrottled(
            "战斗链路:选怪后首攻",
            $"选怪已发送：{hotkey}；首攻={Blank(GetAttackHotkey(cfg))}；"
            + $"首攻兜底={(fallbackDelayMs > 0 ? $"{fallbackDelayMs}毫秒后" : "未启用")}；"
            + $"名单门禁={(RequiresRecognizedTarget(cfg) ? "需确认名称" : "不要求名称")}。",
            $"reason={reason}; armFallbackAttack={armFallbackAttack}; "
            + $"fallbackDelayMs={fallbackDelayMs}; input={cfg.InputDispatchMode}",
            Target.Id,
            1500);

        return true;
    }

    private bool TrySendPendingAcquireAttack(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (_pendingAcquireAttackTicks == 0)
            return false;

        if (!HasAcquireAttackHotkey(cfg))
        {
            _pendingAcquireAttackTicks = 0;
            return false;
        }

        if (combat.TargetLooksLikePlayer
            || IsAmbiguousPlayerProtectionEnabled(cfg, combat)
            || combat.TargetLooksLikePet
            || combat.TargetIsAttackable == false)
        {
            _pendingAcquireAttackTicks = 0;
            WarnProtectedTargetSkipped("基础选怪攻击已取消", "选怪后检测到保护目标特征，取消兜底普攻。", combat);
            return false;
        }

        var targetDecision = EvaluateTarget(cfg, combat);
        var confirmedSafeLiveTarget = IsLiveTargetHp(combat.TargetHpPercent) && targetDecision.Allowed;
        var now = Environment.TickCount64;
        if (!confirmedSafeLiveTarget && RequiresRecognizedTarget(cfg))
        {
            // Keep the pending first attack armed, but never blind-attack while a
            // whitelist/blacklist or identity-dependent policy is active. The first
            // safe live target frame will bypass the timer and attack immediately.
            UserDiagnosticThrottled(
                "战斗门禁:等待目标身份",
                $"首攻暂缓：当前名单策略要求确认目标名称；判定={targetDecision.Reason}；"
                + $"{BuildCombatStateSummary(combat)}",
                BuildCombatDebugDetails(cfg, combat, targetDecision),
                Target?.Id ?? "-",
                1500);
            return false;
        }

        if (!confirmedSafeLiveTarget && now < _pendingAcquireAttackTicks)
        {
            var remainingMs = Math.Max(0, _pendingAcquireAttackTicks - now);
            UserDiagnosticThrottled(
                "战斗链路:等待首攻兜底",
                $"首攻等待目标面板刷新，剩余约 {remainingMs} 毫秒；{BuildCombatStateSummary(combat)}",
                BuildCombatDebugDetails(cfg, combat, targetDecision),
                Target?.Id ?? "-",
                1000);
            TraceDecision(CombatLoopDecision.For(
                CombatLoopDecisionKind.PendingFallbackAttack,
                "选怪后等待目标面板刷新，再执行兜底普攻。",
                willAttack: true,
                willCastSkill: true), combat);
            return true;
        }

        Log.Info(
            Name,
            confirmedSafeLiveTarget
                ? "新目标已确认可攻击，跳过兜底计时并立即发送普攻。"
                : "视觉尚未确认目标，按基础选怪-攻击链发送一次普攻。",
            Target?.Id ?? "-");
        TraceDecision(CombatLoopDecision.For(
            CombatLoopDecisionKind.PendingFallbackAttack,
            confirmedSafeLiveTarget
                ? "新目标已确认可攻击，立即执行首轮普攻。"
                : "选怪后视觉尚未确认目标，执行一次兜底普攻。",
            willAttack: true,
            willCastSkill: true), combat);
        var beforeActions = _attackActionsOnCurrentTarget;
        var attackResult = TrySendAttackHotkey(cfg, allowRepeatOnSameTarget: true, bypassIntervalIfFirstAttack: true);
        Machine.Goto(NormalHangState.AttackLoop);
        var skillSent = TrySendDueSkillAfterAttack(cfg, combat, attackResult);
        NotePlannedAttackOutcome("选怪后兜底", attackResult, skillSent, cfg, combat);
        if (_attackSentForCurrentTarget || _attackActionsOnCurrentTarget > beforeActions)
            _pendingAcquireAttackTicks = 0;

        return true;
    }

    private bool HasAcquireAttackHotkey(GlobalConfig cfg) =>
        !string.IsNullOrWhiteSpace(GetAttackHotkey(cfg));

    private static bool IsAmbiguousPlayerProtectionEnabled(GlobalConfig cfg, CombatRecognitionResult combat) =>
        cfg.Targeting.BlockPlayerTargets && combat.TargetProtectionAmbiguous;

    private static int ResolveAcquireFallbackAttackDelayMs(GlobalConfig cfg) =>
        Math.Clamp(cfg.Targeting.ScanIntervalMs <= 0 ? 40 : cfg.Targeting.ScanIntervalMs, 40, 60);

    private string GetAcquireHotkey(GlobalConfig cfg)
    {
        // The key explicitly recorded in the Agent UI is authoritative. The
        // client profile is only a fallback for profiles that have not set it.
        var configured = cfg.Targeting.AcquireHotkey?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return TryResolveCurrentMapping(GameInputSemanticIds.TargetSelect, out var binding)
            ? binding.Hotkey
            : string.Empty;
    }

    private string GetAttackHotkey(GlobalConfig cfg)
    {
        // 普通攻击是用户显式配置的独立动作，不能假定技能栏第 1 槽就是普攻。
        // 只有旧配置没有填写普攻键时，才用当前角色的第 1 槽映射兜底。
        var configured = cfg.AttackHotkey?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return TryResolveCurrentMapping(GameInputSemanticIds.SkillSlot(1), out var binding)
            ? binding.Hotkey
            : string.Empty;
    }

    private bool IsAcquireHotkeyMissing(GlobalConfig cfg) =>
        string.IsNullOrWhiteSpace(GetAcquireHotkey(cfg));

    private bool ShouldForceOpeningAttack() =>
        _firstAttackPendingAfterAcquire
        && !_attackSentForCurrentTarget
        && _attackActionsOnCurrentTarget == 0;

    private bool TryResolveCurrentMapping(string semanticId, out GameHotkeyBinding binding)
    {
        binding = null!;
        return Target is not null
               && _gameHotkeys is not null
               && _gameHotkeys.TryResolve(Target.Hwnd, semanticId, out binding);
    }

    private AttackHotkeyResult TrySendAttackHotkey(
        GlobalConfig cfg,
        bool allowRepeatOnSameTarget = false,
        bool bypassIntervalIfFirstAttack = false,
        bool forceImmediate = false)
    {
        if (Target is null)
            return AttackHotkeyResult.NoTarget;

        var attackHotkey = GetAttackHotkey(cfg);
        if (string.IsNullOrWhiteSpace(attackHotkey))
        {
            InfoThrottled("普攻:未配置", "普攻跳过：未配置普通攻击热键。", "-", 3000);
            return AttackHotkeyResult.MissingHotkey;
        }

        if (!Backend.TryParseHotkey(attackHotkey, out var error))
        {
            Log.Warn(Name, $"攻击热键无效：{attackHotkey} | {error}", Target.Id);
            return AttackHotkeyResult.InvalidHotkey;
        }

        var now = Environment.TickCount64;
        var isFirstAttackOnCurrentTarget = !_attackSentForCurrentTarget && _attackActionsOnCurrentTarget == 0;
        if (!allowRepeatOnSameTarget && !isFirstAttackOnCurrentTarget)
        {
            InfoThrottled("普攻:本目标已发送", "普攻跳过：当前目标已发送过普通攻击，后续仅按技能 CD/间隔循环。", Target.Id, 1500);
            return AttackHotkeyResult.AlreadySentForTarget;
        }

        if (!forceImmediate && (!bypassIntervalIfFirstAttack || !isFirstAttackOnCurrentTarget))
        {
            if (_lastAttackTicks != 0 && now - _lastAttackTicks < Math.Max(0, cfg.AttackIntervalMs))
            {
                var remainingMs = Math.Max(0, cfg.AttackIntervalMs - (now - _lastAttackTicks));
                InfoThrottled("普攻:间隔", $"普攻跳过：距离上次普攻未到间隔，剩余 {remainingMs / 1000.0:0.##} 秒。", Target.Id, 1500);
                return AttackHotkeyResult.WaitingInterval;
            }
        }

        if (!CastHotkeyWithLog(attackHotkey, "普攻"))
            return AttackHotkeyResult.SendFailed;

        _lastAttackTicks = now;
        _attackSentForCurrentTarget = true;
        _firstAttackPendingAfterAcquire = false;
        _pendingAcquireAttackTicks = 0;
        NoteAttackAction();
        Log.Info(Name, $"发送普攻热键：{attackHotkey}", Target.Id);
        return AttackHotkeyResult.Sent;
    }

    private void TrySendEmHeal(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (Target is null || !cfg.EnableEmHeal || string.IsNullOrWhiteSpace(cfg.EmHealHotkey))
            return;

        if (combat.PlayerHpPercent is null || combat.PlayerHpPercent > cfg.EmHealThresholdPct)
            return;

        if (!Backend.TryParseHotkey(cfg.EmHealHotkey, out var error))
        {
            Log.Warn(Name, $"峨眉加血热键无效：{cfg.EmHealHotkey} | {error}", Target.Id);
            return;
        }

        var now = Environment.TickCount64;
        if (_lastEmHealTicks != 0 && now - _lastEmHealTicks < Math.Max(500, cfg.PublicCooldownMs))
            return;

        if (!Backend.TryConsumeGlobalCooldown(Target!, cfg.PublicCooldownMs))
            return;

        if (!CastHotkeyWithLog(cfg.EmHealHotkey, "峨眉加血"))
            return;

        _lastEmHealTicks = now;
        Log.Info(Name, $"峨眉加血触发：HP {combat.PlayerHpPercent}% <= {cfg.EmHealThresholdPct}%，热键 {cfg.EmHealHotkey}。", Target.Id);
    }

    private void TryRunLowPrioritySupportActions(
        GlobalConfig cfg,
        CombatRecognitionResult combat,
        bool allowDuringIdle = false)
    {
        // Combat owns the scan. Recovery and Buff actions are opportunistic only:
        // they may run after the combat loop produced no input, but they must never
        // delay a death switch, target acquisition, or the opening normal attack.
        if (_actionSentThisScan)
            return;

        if (!allowDuringIdle && HasPendingCombatPriority(cfg, combat))
            return;

        Machine.Goto(NormalHangState.CheckHpMpPet);
        TrySendEmHeal(cfg, combat);
        if (!_actionSentThisScan)
            TrySendRecoveryHotkeys(cfg, combat);
        if (!_actionSentThisScan)
            TrySendDueSkill(cfg, combat, SkillCastScope.BuffOnly);
    }

    private bool TryRunEstablishedCombatRecoveryActions(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        // Recovery has an independent scheduling lane, but all game input remains serialized.
        // A death switch, target acquisition and the first normal attack always win. Once the
        // target has received its opening attack, one due recovery action may own this scan;
        // combat resumes on the next scan instead of starving recovery indefinitely.
        if (_actionSentThisScan
            || !_attackSentForCurrentTarget
            || _firstAttackPendingAfterAcquire
            || _pendingAcquireAttackTicks != 0
            || IsDeadOrEmptyTargetHp(combat)
            || _killSwitchPhase == KillSwitchPhase.AwaitingTarget)
        {
            return false;
        }

        Machine.Goto(NormalHangState.CheckHpMpPet);
        TrySendEmHeal(cfg, combat);
        if (!_actionSentThisScan)
            TrySendRecoveryHotkeys(cfg, combat);

        if (!_actionSentThisScan)
            return false;

        TraceDecision(CombatLoopDecision.For(
            CombatLoopDecisionKind.Recovery,
            "交战目标已完成首击，本扫描周期执行到期恢复；下一扫描周期继续战斗。",
            willSkipAttack: true), combat, minIntervalMs: 500);
        return true;
    }

    private bool HasPendingCombatPriority(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (_firstAttackPendingAfterAcquire || _pendingAcquireAttackTicks != 0)
            return true;

        if (IsDeadOrEmptyTargetHp(combat))
            return true;

        if (IsKillStyleMode(cfg.Targeting.SwitchMode)
            && _killSwitchPhase == KillSwitchPhase.AwaitingTarget)
        {
            return true;
        }

        return cfg.Targeting.AutoAcquireWhenNoTarget
               && combat.NearbyMonsterState != VisionTriState.Absent
               && !HasActionableTarget(combat);
    }

    private void TrySendRecoveryHotkeys(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        TryDeployPetIfNeeded(cfg, combat);
        if (_actionSentThisScan)
            return;

        // Pet food has no shared pill cooldown and must not be starved by a failed
        // player medicine attempt. One pulse owns this scan; combat resumes next scan.
        TryRecoverPetWithFood(cfg, combat.PetHpPercent);
        if (_actionSentThisScan)
            return;

        var pillCooldownMs = ResolvePillCooldownMs(cfg);
        var pillUsed = false;

        if (cfg.HealthRecoveryMode == PlayerHealthRecoveryMode.Medicine)
        {
            pillUsed = TrySendThresholdHotkey(
                "药品回血",
                combat.PlayerHpPercent,
                cfg.HpThresholdPct,
                cfg.HealHotkey,
                pillCooldownMs,
                ref _lastPillTicks);
        }
        else
        {
            TrySendPetSkillThreshold(
                "共生回血",
                "health_restore_skill",
                combat.PlayerHpPercent,
                cfg.HpThresholdPct,
                cfg.SymbiosisHealActivationMode,
                cfg.SymbiosisHealHotkey,
                cfg.SymbiosisHealClickX,
                cfg.SymbiosisHealClickBottomOffset,
                Math.Max(30000, cfg.SymbiosisHealCooldownMs),
                ref _lastSymbiosisTicks);
        }

        if (_actionSentThisScan)
            return;

        if (cfg.ManaRecoveryMode == PlayerManaRecoveryMode.Medicine
            && pillUsed
            && combat.PlayerMpPercent is not null
            && combat.PlayerMpPercent <= cfg.MpThresholdPct)
        {
            InfoRecoveryThrottled(
                "药品回蓝:shared-pill-cd",
                $"药品回蓝跳过：人物蓝 {combat.PlayerMpPercent}% <= {cfg.MpThresholdPct}%，但本轮已触发药品回血；回血/回蓝药品共用 {pillCooldownMs / 1000.0:0.#} 秒冷却。",
                Target?.Id ?? "-");
        }
        else if (cfg.ManaRecoveryMode == PlayerManaRecoveryMode.Medicine)
        {
            TrySendThresholdHotkey(
                "药品回蓝",
                combat.PlayerMpPercent,
                cfg.MpThresholdPct,
                cfg.ManaHotkey,
                pillCooldownMs,
                ref _lastPillTicks);
        }
        else
        {
            TrySendPetSkillThreshold(
                "血迹回蓝",
                "mana_restore_skill",
                combat.PlayerMpPercent,
                cfg.MpThresholdPct,
                cfg.BloodManaActivationMode,
                EffectiveBloodManaHotkey(cfg),
                cfg.BloodManaClickX,
                cfg.BloodManaClickBottomOffset,
                Math.Max(30000, cfg.BloodManaCooldownMs),
                ref _lastBloodManaTicks);
        }

        if (_actionSentThisScan)
            return;
    }

    private void TryRecoverPetWithFood(GlobalConfig cfg, int? petHpPercent)
    {
        if (petHpPercent is null)
            return;

        if (!_petFoodRecoveryActive && petHpPercent <= cfg.PetFoodThresholdPct)
        {
            _petFoodRecoveryActive = true;
            Log.Info(
                Name,
                $"宠物恢复开始：宠物血量 {petHpPercent}% <= {cfg.PetFoodThresholdPct}%，进入口粮快速补满状态。",
                Target?.Id ?? "-");
        }

        // The visual percentage may fluctuate by one point around a full bar.
        if (_petFoodRecoveryActive && petHpPercent >= 99)
        {
            _petFoodRecoveryActive = false;
            Log.Info(Name, $"宠物恢复完成：宠物血量 {petHpPercent}%。", Target?.Id ?? "-");
            return;
        }

        if (!_petFoodRecoveryActive)
            return;

        TrySendPetFoodPulse(cfg, petHpPercent.Value);
    }

    private void TrySendPetFoodPulse(GlobalConfig cfg, int petHpPercent)
    {
        if (Target is null)
            return;

        var hotkey = EffectivePetFoodHotkey(cfg);
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            WarnMissingRecoveryHotkey("宠物口粮", petHpPercent, cfg.PetFoodThresholdPct);
            return;
        }

        if (!Backend.TryParseHotkey(hotkey, out var error))
        {
            WarnThrottled("宠物口粮", $"宠物口粮热键无效：{hotkey} | {error}", Target.Id);
            return;
        }

        var now = Environment.TickCount64;
        var repeatIntervalMs = Math.Clamp(cfg.PetFoodCooldownMs, 150, 2000);
        if (_lastPetFoodTicks != 0 && now - _lastPetFoodTicks < repeatIntervalMs)
            return;

        // Pet food is not a character medicine/skill action. It has its own pulse interval
        // and must not be blocked by the character public cooldown while the pet is still low.
        if (!CastHotkeyWithLog(hotkey, "宠物口粮"))
            return;

        _lastPetFoodTicks = now;
        InfoRecoveryThrottled(
            "宠物口粮:补满中",
            $"宠物口粮补满中：当前 {petHpPercent}%，间隔 {repeatIntervalMs}ms。",
            Target.Id,
            1000);
    }

    private void TryDeployPetIfNeeded(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (Target is null)
            return;

        if (combat.PetDeploymentState == PetDeploymentState.Deployed || combat.PetHpPercent is not null)
        {
            _missingPetHpScans = 0;
            return;
        }

        if (combat.PetDeploymentState == PetDeploymentState.Unknown)
        {
            _missingPetHpScans = 0;
            InfoRecoveryThrottled(
                "宠物出战:state-unknown",
                "宠物状态暂时未知，本轮不执行出战动作。",
                Target.Id,
                3000);
            return;
        }

        _missingPetHpScans++;
        if (_missingPetHpScans < 3)
            return;

        var useClick = cfg.PetDeployActivationMode == PetSkillActivationMode.ClientClick;
        var actionText = useClick
            ? $"点击({cfg.PetDeployClickX}, bottom-{cfg.PetDeployClickBottomOffset})"
            : $"热键 {cfg.PetDeployHotkey}";

        if (useClick)
        {
            if (cfg.PetDeployClickX <= 0 || cfg.PetDeployClickBottomOffset <= 0)
            {
                WarnMissingPetDeployAction(_missingPetHpScans);
                return;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cfg.PetDeployHotkey))
            {
                WarnMissingPetDeployAction(_missingPetHpScans);
                return;
            }

            if (!Backend.TryParseHotkey(cfg.PetDeployHotkey, out var error))
            {
                WarnThrottled("宠物出战", $"宠物出战热键无效：{cfg.PetDeployHotkey} | {error}", Target.Id);
                return;
            }
        }

        var now = Environment.TickCount64;
        var cooldownMs = Math.Max(3000, cfg.PetDeployCooldownMs);
        if (_lastPetDeployTicks != 0 && now - _lastPetDeployTicks < cooldownMs)
        {
            InfoRecoveryThrottled(
                "宠物出战:cooldown",
                $"宠物出战跳过：连续 {_missingPetHpScans} 轮未识别到宠物血条，但出战冷却中，剩余 {(cooldownMs - (now - _lastPetDeployTicks)) / 1000.0:0.#} 秒。",
                Target.Id);
            return;
        }

        if (!Backend.TryConsumeGlobalCooldown(Target!, cfg.PublicCooldownMs))
        {
            InfoRecoveryThrottled("宠物出战:public-cd", $"宠物出战跳过：公共 CD 未结束，本轮不执行 {actionText}。", Target.Id);
            return;
        }

        var ok = useClick
            ? ClickPetSkillWithLog("宠物出战", cfg.PetDeployClickX, cfg.PetDeployClickBottomOffset)
            : CastHotkeyWithLog(cfg.PetDeployHotkey, "宠物出战");

        if (!ok)
            return;

        _lastPetDeployTicks = now;
        Log.Info(Name, $"连续 {_missingPetHpScans} 轮未识别到宠物血条，尝试出战当前宠物：{actionText}，冷却 {cooldownMs / 1000.0:0.#} 秒。", Target.Id);
    }

    private static string EffectiveBloodManaHotkey(GlobalConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.BloodManaHotkey) ? cfg.BloodManaHotkey : cfg.PetManaHotkey;

    private static string EffectivePetFoodHotkey(GlobalConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.PetFoodHotkey) ? cfg.PetFoodHotkey : cfg.PetHealHotkey;

    private void TrySendPetSkillThreshold(
        string label,
        string businessClass,
        int? currentPercent,
        int thresholdPercent,
        PetSkillActivationMode activationMode,
        string hotkey,
        int clickX,
        int clickBottomOffset,
        int cooldownMs,
        ref long lastTicks)
    {
        if (Target is null)
            return;

        if (currentPercent is null)
        {
            InfoRecoveryThrottled(
                $"{label}:missing-state",
                $"{label}跳过：未识别到{RecoveryMetricText(label)}，无法判断是否低于 {thresholdPercent}%。",
                Target.Id,
                5000);
            return;
        }

        if (currentPercent > thresholdPercent)
            return;

        var now = Environment.TickCount64;
        cooldownMs = Math.Max(500, cooldownMs);
        if (lastTicks != 0 && now - lastTicks < cooldownMs)
        {
            InfoRecoveryThrottled(
                $"{label}:cooldown",
                $"{label}跳过：{RecoveryMetricText(label)} {currentPercent}% <= {thresholdPercent}%，但独立冷却中，剩余 {(cooldownMs - (now - lastTicks)) / 1000.0:0.#} 秒。",
                Target.Id);
            return;
        }

        if (_perceptionHub is not null
            && _visionActionPlanner is not null
            && _visionActionExecutor is not null)
        {
            var visual = EvaluateVisionPetRestore(
                businessClass,
                currentPercent.Value,
                thresholdPercent,
                _global.Current);
            if (visual.State == PetRestoreVisualState.Blocked)
            {
                InfoRecoveryThrottled(
                    $"{label}:vision-blocked",
                    $"{label}被 BUFF 安全门禁阻止：{visual.Reason}。",
                    Target.Id,
                    2000);
                return;
            }
            if (visual.State == PetRestoreVisualState.Cooling)
            {
                InfoRecoveryThrottled(
                    $"{label}:vision-cooling",
                    $"{label}暂不执行：视觉已确认宠物技能仍在冷却，原因 {visual.Reason}。",
                    Target.Id,
                    2000);
                return;
            }

            if (visual.State == PetRestoreVisualState.Ready)
            {
                var decision = visual.Decision!;
                if (!Backend.TryConsumeGlobalCooldown(Target!, _global.Current.PublicCooldownMs))
                {
                    InfoRecoveryThrottled(
                        $"{label}:public-cd",
                        $"{label}跳过：公共 CD 未结束。",
                        Target.Id);
                    return;
                }

                if (!_visionActionExecutor.TryExecuteClick(Target, decision, _global.Current))
                {
                    WarnThrottled(
                        $"{label}:vision-click",
                        $"{label}视觉插槽点击失败：{decision.Reason}",
                        Target.Id,
                        $"{label}未执行：识别到技能位置，但本次点击未成功。");
                    return;
                }

                _actionSentThisScan = true;
                lastTicks = now;
                Log.Info(
                    Name,
                    $"{label}触发：{currentPercent}% <= {thresholdPercent}%，视觉插槽点击({decision.ClientX},{decision.ClientY})，冷却 {cooldownMs / 1000.0:0.#} 秒。",
                    Target.Id);
                return;
            }

            InfoRecoveryThrottled(
                $"{label}:vision-fallback",
                $"{label}视觉暂不可用，尝试已校准的安全回退；原因 {visual.Reason}。",
                Target.Id,
                2000);
        }

        if (activationMode == PetSkillActivationMode.Hotkey)
        {
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                WarnMissingRecoveryHotkey(label, currentPercent.Value, thresholdPercent);
                return;
            }

            if (!Backend.TryParseHotkey(hotkey, out var error))
            {
                Log.Warn(Name, $"{label}热键无效：{hotkey} | {error}", Target.Id);
                return;
            }
        }

        if (!Backend.TryConsumeGlobalCooldown(Target!, _global.Current.PublicCooldownMs))
        {
            InfoRecoveryThrottled(
                $"{label}:public-cd",
                $"{label}跳过：{RecoveryMetricText(label)} {currentPercent}% <= {thresholdPercent}%，但公共 CD 未结束。",
                Target.Id);
            return;
        }

        var ok = activationMode == PetSkillActivationMode.ClientClick
            ? ClickPetSkillWithLog(label, clickX, clickBottomOffset)
            : CastHotkeyWithLog(hotkey, label);

        if (!ok)
            return;

        lastTicks = now;
        var action = activationMode == PetSkillActivationMode.ClientClick
            ? $"点击({clickX}, bottom-{clickBottomOffset})"
            : $"热键 {hotkey}";
        Log.Info(Name, $"{label}触发：{currentPercent}% <= {thresholdPercent}%，{action}，冷却 {cooldownMs / 1000.0:0.#} 秒。", Target.Id);
    }

    private PetRestoreVisualEvaluation EvaluateVisionPetRestore(
        string businessClass,
        int currentPercent,
        int thresholdPercent,
        GlobalConfig config)
    {
        if (Target is null || _perceptionHub is null || _visionActionPlanner is null)
            return PetRestoreVisualEvaluation.Unavailable("vision planner unavailable");

        var snapshot = _perceptionHub.Read(Target, config);
        if (!snapshot.BuffBar.Available)
            return PetRestoreVisualEvaluation.Blocked("Buff identity is unavailable");

        if (snapshot.BuffBar.Available)
        {
            if (snapshot.BuffBar.PetActiveSkillBlocked
                || snapshot.BuffBar.Fatigue == VisionTriState.Present)
            {
                return PetRestoreVisualEvaluation.Blocked("fatigue Buff is present");
            }
            if (snapshot.BuffBar.Fatigue == VisionTriState.Unknown)
                return PetRestoreVisualEvaluation.Blocked("Buff identity is enabled but fatigue state is unknown");
        }
        var businessSlots = snapshot.SkillBar.PetRestoreSkills
            .Where(item => string.Equals(item.BusinessClass, businessClass, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var matchedSlots = businessSlots.Where(item => item.IsMatched).ToArray();
        var readySlots = matchedSlots
            .Where(item => item.Cooldown == VisionCooldownState.Ready
                           && item.ClickX is not null
                           && item.ClickY is not null)
            .ToArray();

        if (readySlots.Length == 0)
        {
            if (matchedSlots.Any(item => item.Cooldown == VisionCooldownState.Cooling))
                return PetRestoreVisualEvaluation.Cooling("matched skill is cooling");

            return PetRestoreVisualEvaluation.Unavailable(
                businessSlots.Length == 0
                    ? "no matching pet restore slot"
                    : "pet restore identity or click point is unavailable");
        }

        var clickable = readySlots
            .Select(item => new
            {
                business_class = item.BusinessClass,
                identity_state = "matched",
                cd_state = "ready",
                screen_click_point = new[] { item.ClickX!.Value, item.ClickY!.Value },
                variant = item.Variant,
                score = item.Confidence
            })
            .ToArray();
        var ratio = Math.Clamp(currentPercent / 100.0, 0, 1);
        var threshold = Math.Clamp(thresholdPercent / 100.0, 0, 1);
        VisionActionDecision decision;
        if (businessClass == "mana_restore_skill")
        {
            decision = _visionActionPlanner.Plan("player_mana", JsonSerializer.Serialize(new
            {
                player = new { mp_ratio = ratio },
                pet_restore = new { clickable },
                mp_threshold = threshold
            }));
        }
        else
        {
            decision = _visionActionPlanner.Plan("player_health", JsonSerializer.Serialize(new
            {
                player = new { hp_ratio = ratio },
                pet_restore = new { clickable },
                hp_threshold = threshold
            }));
        }

        return decision.Ok && decision.Action == VisionActionKind.Click
            ? PetRestoreVisualEvaluation.Ready(decision)
            : PetRestoreVisualEvaluation.Unavailable(decision.Reason);
    }

    private bool ClickPetSkillWithLog(string action, int x, int bottomOffset)
    {
        if (Target is null)
            return false;

        var resolved = ResolvePetSkillClickPoint(action, x, bottomOffset, Target);
        var y = Target.ClientHeight - Math.Max(0, resolved.BottomOffset);
        if (resolved.X <= 0
            || resolved.BottomOffset <= 0
            || resolved.X >= Target.ClientWidth
            || y < 0
            || y >= Target.ClientHeight)
        {
            WarnThrottled(
                $"{action}:invalid-click",
                $"{action}点击点无效：x={resolved.X}, bottom={resolved.BottomOffset}, client={Target.ClientSizeText}。",
                Target.Id,
                $"{action}未执行：宠物技能位置尚未校准。");
            return false;
        }

        if (!IsPetSkillClickPointInActionBand(resolved.X, y, Target, out var bandReason))
        {
            WarnThrottled(
                $"{action}点击点",
                $"{action}点击点被安全门禁拦截：({resolved.X},{y})，{bandReason}。请先在通用配置里重新校准宠物技能点击点。",
                Target.Id,
                $"{action}未执行：宠物技能位置需要重新校准。");
            return false;
        }

        if (resolved.X != x || resolved.BottomOffset != bottomOffset)
            Log.Trace(Name, $"{action}点击点已按窗口尺寸解析：({x}, bottom-{bottomOffset}) -> ({resolved.X}, bottom-{resolved.BottomOffset})。", Target.Id);

        var ok = Backend.ClickClient(Target, resolved.X, y, _global.Current);
        if (ok)
        {
            _actionSentThisScan = true;
            return true;
        }

        var now = Environment.TickCount64;
        if (_lastInputFailureTicks == 0 || now - _lastInputFailureTicks >= 1500)
        {
            _lastInputFailureTicks = now;
            Log.Log(new LogEntry
            {
                Level = LogLevel.Warn,
                Module = Name,
                Message = $"{action}点击失败：({resolved.X},{y})。",
                Window = Target.Id,
                Audience = LogAudience.Diagnostic
            });
        }

        return false;
    }

    private static (int X, int BottomOffset) ResolvePetSkillClickPoint(string label, int x, int bottomOffset, BoundGameWindow target)
    {
        var legacyBadDefault = (x == 170 || x == 135) && bottomOffset == 90;
        var resolvedX = x;
        var resolvedBottom = bottomOffset;

        if (legacyBadDefault)
        {
            var ratio = x == 135 ? 0.405 : 0.43;
            resolvedX = (int)Math.Round(target.ClientWidth * ratio);
        }

        if (legacyBadDefault)
            resolvedBottom = (int)Math.Round(target.ClientHeight * 0.065);

        return (resolvedX, resolvedBottom);
    }

    private enum PetRestoreVisualState
    {
        Unavailable,
        Blocked,
        Cooling,
        Ready
    }

    private sealed record PetRestoreVisualEvaluation(
        PetRestoreVisualState State,
        VisionActionDecision? Decision,
        string Reason)
    {
        public static PetRestoreVisualEvaluation Unavailable(string reason) =>
            new(PetRestoreVisualState.Unavailable, null, reason);

        public static PetRestoreVisualEvaluation Cooling(string reason) =>
            new(PetRestoreVisualState.Cooling, null, reason);

        public static PetRestoreVisualEvaluation Blocked(string reason) =>
            new(PetRestoreVisualState.Blocked, null, reason);

        public static PetRestoreVisualEvaluation Ready(VisionActionDecision decision) =>
            new(PetRestoreVisualState.Ready, decision, decision.Reason);
    }

    private static bool IsPetSkillClickPointInActionBand(int x, int y, BoundGameWindow target, out string reason)
    {
        if (target.ClientWidth <= 0 || target.ClientHeight <= 0)
        {
            reason = "客户区尺寸无效";
            return false;
        }

        if (x < 0 || x >= target.ClientWidth || y < 0 || y >= target.ClientHeight)
        {
            reason = $"点位超出客户区 {target.ClientSizeText}";
            return false;
        }

        var minActionBandY = (int)Math.Round(target.ClientHeight * 0.55);
        if (y < minActionBandY)
        {
            reason = $"点位不在底部动作区，y={y} < {minActionBandY}";
            return false;
        }

        reason = "";
        return true;
    }

    private bool TrySendThresholdHotkey(
        string label,
        int? currentPercent,
        int thresholdPercent,
        string hotkey,
        int cooldownMs,
        ref long lastTicks)
    {
        if (Target is null)
            return false;

        if (currentPercent is null)
        {
            InfoRecoveryThrottled(
                $"{label}:missing-state",
                $"{label}跳过：未识别到{RecoveryMetricText(label)}，无法判断是否低于 {thresholdPercent}%。",
                Target.Id,
                5000);
            return false;
        }

        var now = Environment.TickCount64;
        var pendingKey = $"threshold:{label}";
        if (_pendingRecoveryVerifications.TryGetValue(pendingKey, out var pending))
        {
            var increased = currentPercent.Value >= pending.BeforePercent + 2 || currentPercent.Value > thresholdPercent;
            if (increased)
            {
                _pendingRecoveryVerifications.Remove(pendingKey);
                _recoveryBackoffUntil.Remove(pendingKey);
                InfoRecoveryThrottled(
                    $"{label}:verified",
                    $"{label}回读成功：{RecoveryMetricText(label)} {pending.BeforePercent}% → {currentPercent}%。",
                    Target.Id,
                    3000);
            }
            else if (now - pending.SentAtTicks < RecoveryVerificationWaitMs)
            {
                return false;
            }
            else
            {
                _pendingRecoveryVerifications.Remove(pendingKey);
                _recoveryBackoffUntil[pendingKey] = now + RecoveryFailureBackoffMs;
                WarnThrottled(
                    pendingKey,
                    $"{label}按键后数值未上升：{RecoveryMetricText(label)}仍为 {currentPercent}%，可能没有药、按键无效或动作失败；暂停该恢复 60 秒，挂机继续。",
                    Target.Id);
                return false;
            }
        }

        if (currentPercent > thresholdPercent)
        {
            _pendingRecoveryVerifications.Remove(pendingKey);
            _recoveryBackoffUntil.Remove(pendingKey);
            return false;
        }

        if (_recoveryBackoffUntil.TryGetValue(pendingKey, out var backoffUntil) && now < backoffUntil)
        {
            InfoRecoveryThrottled(
                $"{label}:failed-backoff",
                $"{label}暂缓：上次触发后数值未上升，剩余 {(backoffUntil - now) / 1000.0:0.#} 秒后再试；挂机继续。",
                Target.Id,
                5000);
            return false;
        }

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            WarnMissingRecoveryHotkey(label, currentPercent.Value, thresholdPercent);
            return false;
        }

        if (!Backend.TryParseHotkey(hotkey, out var error))
        {
            Log.Warn(Name, $"{label}热键无效：{hotkey} | {error}", Target.Id);
            return false;
        }

        var cfg = _global.Current;
        cooldownMs = Math.Max(500, cooldownMs);
        if (lastTicks != 0 && now - lastTicks < cooldownMs)
        {
            InfoRecoveryThrottled(
                $"{label}:cooldown",
                $"{label}跳过：{RecoveryMetricText(label)} {currentPercent}% <= {thresholdPercent}%，但冷却中，剩余 {(cooldownMs - (now - lastTicks)) / 1000.0:0.#} 秒。",
                Target.Id);
            return false;
        }

        if (!Backend.TryConsumeGlobalCooldown(Target!, cfg.PublicCooldownMs))
        {
            InfoRecoveryThrottled(
                $"{label}:public-cd",
                $"{label}跳过：{RecoveryMetricText(label)} {currentPercent}% <= {thresholdPercent}%，但公共 CD 未结束。",
                Target.Id);
            return false;
        }

        if (!CastHotkeyWithLog(hotkey, label))
            return false;

        lastTicks = now;
        _pendingRecoveryVerifications[pendingKey] = new PendingRecoveryVerification(currentPercent.Value, now);
        Log.Info(Name, $"{label}触发：{currentPercent}% <= {thresholdPercent}%，热键 {hotkey}，冷却 {cooldownMs / 1000.0:0.#} 秒。", Target.Id);
        return true;
    }

    private void WarnMissingRecoveryHotkey(string label, int currentPercent, int thresholdPercent)
    {
        WarnThrottled(label, $"{label}触发条件已满足：{currentPercent}% <= {thresholdPercent}%，但未配置可用热键或点击方式。", Target?.Id ?? "-");
    }

    private void WarnMissingPetDeployAction(int missingScans)
    {
        WarnThrottled("宠物出战", $"宠物出战条件已满足：连续 {missingScans} 轮未识别到宠物血条，但未配置宠物出战热键或客户区点击点。", Target?.Id ?? "-");
    }

    private void WarnThrottled(string label, string message, string targetId, string? userSummary = null)
    {
        var now = Environment.TickCount64;
        if (_lastRecoveryWarningTicks.TryGetValue(label, out var last) && now - last < 3000)
            return;

        _lastRecoveryWarningTicks[label] = now;
        if (string.IsNullOrWhiteSpace(userSummary))
        {
            Log.Warn(Name, message, targetId);
            return;
        }

        Log.Log(new LogEntry
        {
            Level = LogLevel.Warn,
            Module = Name,
            Message = message,
            Window = targetId,
            Audience = LogAudience.User,
            UserSummary = userSummary
        });
    }

    private void InfoRecoveryThrottled(string key, string message, string targetId, int minIntervalMs = 3000)
    {
        var now = Environment.TickCount64;
        if (_lastRecoveryInfoTicks.TryGetValue(key, out var last) && now - last < minIntervalMs)
            return;

        _lastRecoveryInfoTicks[key] = now;
        Log.Info(Name, message, targetId);
    }

    private void InfoThrottled(string key, string message, string targetId, int minIntervalMs = 3000)
    {
        var now = Environment.TickCount64;
        if (_lastRecoveryInfoTicks.TryGetValue(key, out var last) && now - last < minIntervalMs)
            return;

        _lastRecoveryInfoTicks[key] = now;
        Log.Info(Name, message, targetId);
    }

    private void UserDiagnosticThrottled(
        string key,
        string userSummary,
        string debugDetails,
        string targetId,
        int minIntervalMs = 2000)
    {
        var throttleKey = $"用户诊断:{key}";
        var now = Environment.TickCount64;
        if (_lastRecoveryInfoTicks.TryGetValue(throttleKey, out var last)
            && now - last < minIntervalMs)
        {
            return;
        }

        _lastRecoveryInfoTicks[throttleKey] = now;
        Log.Log(new LogEntry
        {
            Level = LogLevel.Info,
            Module = Name,
            Message = userSummary,
            Audience = LogAudience.User,
            UserSummary = userSummary,
            DebugDetails = debugDetails,
            Window = targetId,
            FeatureKey = FeatureKeys.HangNormal
        });
    }

    private string BuildCombatDebugDetails(
        GlobalConfig cfg,
        CombatRecognitionResult combat,
        TargetSafetyDecision? targetDecision = null)
    {
        var now = Environment.TickCount64;
        var pendingAttackMs = _pendingAcquireAttackTicks == 0
            ? 0
            : Math.Max(0, _pendingAcquireAttackTicks - now);
        var openingSkillMs = _pendingOpeningSkillDispatch is null
            ? 0
            : Math.Max(0, _pendingOpeningSkillDispatch.DueAtTicks - now);
        return $"mode={cfg.Targeting.SwitchMode}; phase={_killSwitchPhase}; "
               + $"name={combat.TargetNameText}; hasName={combat.HasTargetName}; "
               + $"targetHp={combat.TargetHpPercent}; hasTarget={combat.HasTarget}; "
               + $"player={combat.TargetLooksLikePlayer}; ambiguous={combat.TargetProtectionAmbiguous}; "
               + $"pet={combat.TargetLooksLikePet}; attackable={combat.TargetIsAttackable}; "
               + $"nearby={combat.NearbyMonsterState}; requiresName={RequiresRecognizedTarget(cfg)}; "
               + $"decisionAllowed={targetDecision?.Allowed}; decisionReason={targetDecision?.Reason}; "
               + $"firstAttackPending={_firstAttackPendingAfterAcquire}; "
               + $"attackSent={_attackSentForCurrentTarget}; attackActions={_attackActionsOnCurrentTarget}; "
               + $"pendingAttackMs={pendingAttackMs}; pendingOpeningSkillMs={openingSkillMs}; "
               + $"skillMode={(cfg.SkillCooldownRecognitionEnabled ? "ai-vision" : "custom-timer")}";
    }

    private static string BuildCombatStateSummary(CombatRecognitionResult combat)
    {
        var targetName = Blank(combat.TargetNameText);
        var targetType = combat.TargetLooksLikePlayer
            ? "玩家"
            : combat.TargetProtectionAmbiguous
                ? "疑似玩家"
                : combat.TargetLooksLikePet
                    ? "宠物"
                    : "未分类";
        var attackable = combat.TargetIsAttackable switch
        {
            true => "可攻击",
            false => "不可攻击",
            _ => "待确认"
        };

        return $"目标={targetName}，目标血={PercentText(combat.TargetHpPercent)}，"
               + $"类型={targetType}，攻击判定={attackable}，周边怪={VisionTriStateText(combat.NearbyMonsterState)}";
    }

    private string BuildVisualSkillInventorySummary()
    {
        if (Target is null || _perceptionHub is null)
            return "技能视觉库存不可用";
        if (!_perceptionHub.TryGetLatest(Target.Id, out var snapshot))
            return "尚无技能视觉快照";

        var slots = snapshot.SkillBar.VisualSlots;
        var matched = slots.Count(slot => slot.Identity == VisionSkillIdentityState.Matched);
        var ready = slots.Count(slot => slot.Cooldown == VisionCooldownState.Ready);
        var castable = slots.Count(slot => slot.Castable);
        var policyAllowed = slots.Count(slot =>
            slot.HangUsable
            && string.Equals(slot.ReleaseRule, "allowed_by_policy", StringComparison.OrdinalIgnoreCase));
        return $"技能槽={slots.Count}，身份匹配={matched}，冷却就绪={ready}，"
               + $"策略允许={policyAllowed}，可释放={castable}，画面序号={snapshot.FrameSequence}";
    }

    private string BuildSkillRuntimeDebugDetails()
    {
        if (Target is null || _perceptionHub is null)
            return "perception=unavailable";
        if (!_perceptionHub.TryGetLatest(Target.Id, out var snapshot))
            return "snapshot=unavailable";

        var slots = snapshot.SkillBar.VisualSlots;
        return $"frameSequence={snapshot.FrameSequence}; slots={slots.Count}; "
               + $"matched={slots.Count(slot => slot.Identity == VisionSkillIdentityState.Matched)}; "
               + $"ready={slots.Count(slot => slot.Cooldown == VisionCooldownState.Ready)}; "
               + $"castable={slots.Count(slot => slot.Castable)}; "
               + $"pendingVisual={_pendingVisualSkillVerification?.SkillId}; "
               + $"pendingCustom={_pendingCustomSkillVerification?.SkillId}";
    }

    private string BuildVisualOrCustomSkillSummary(GlobalConfig cfg)
    {
        if (cfg.SkillCooldownRecognitionEnabled)
            return BuildVisualSkillInventorySummary();

        var enabled = GetExecutableSkills();
        var combatSkills = enabled.Count(skill => skill.Kind != SkillKind.Buff);
        var buffs = enabled.Count(skill => skill.Kind == SkillKind.Buff);
        return $"自设技能={combatSkills}，Buff={buffs}，"
               + $"待回读={(_pendingCustomSkillVerification?.SkillName ?? "无")}。";
    }

    private static string BuildCustomSkillWaitSummary(
        SkillCastScope scope,
        int executableCount,
        int scopeEligible,
        int conditionBlocked,
        int intervalBlocked,
        int retryBlocked,
        string nextDueSkill,
        long? nextDueMs)
    {
        if (executableCount == 0)
            return "自设技能未释放：没有启用且热键有效的技能。";
        if (scopeEligible == 0)
            return scope == SkillCastScope.CombatOnly
                ? "自设技能未释放：已启用的技能中没有伤害或 AOE 技能。"
                : "自设 Buff 未释放：已启用的技能中没有增益技能。";

        var nextDueText = nextDueMs.HasValue
            ? $"；下一个预计到期={Blank(nextDueSkill)}，约 {nextDueMs.Value} 毫秒"
            : "";
        return $"自设技能本轮未释放：候选={scopeEligible}，条件未满足={conditionBlocked}，"
               + $"计时未到={intervalBlocked}，失败退避={retryBlocked}{nextDueText}。";
    }

    private static string VisualSkillDispatchResultText(VisualSkillDispatchResult result) => result switch
    {
        VisualSkillDispatchResult.Unavailable => "没有通过身份、冷却或策略门禁的技能",
        VisualSkillDispatchResult.InventoryAvailable => "已识别技能，但当前没有可释放项",
        VisualSkillDispatchResult.Sent => "已发送",
        _ => "未知状态"
    };

    private static string VisionTriStateText(VisionTriState state) => state switch
    {
        VisionTriState.Present => "有",
        VisionTriState.Absent => "无",
        _ => "未知"
    };

    private void TraceDecision(CombatLoopDecision decision, CombatRecognitionResult? combat = null, int minIntervalMs = 1200)
    {
        if (Target is null)
            return;

        var now = Environment.TickCount64;
        if (_lastDecisionKind == decision.Kind
            && string.Equals(_lastDecisionReason, decision.Reason, StringComparison.Ordinal)
            && _lastDecisionLogTicks != 0
            && now - _lastDecisionLogTicks < minIntervalMs)
        {
            return;
        }

        _lastDecisionKind = decision.Kind;
        _lastDecisionReason = decision.Reason;
        _lastDecisionLogTicks = now;

        var actions = new List<string>();
        if (decision.WillAcquire) actions.Add("选怪");
        if (decision.WillAttack) actions.Add("普攻");
        if (decision.WillCastSkill) actions.Add("技能");
        if (decision.WillSkipAttack) actions.Add("不攻击");
        if (decision.IsProtection) actions.Add("保护");
        if (actions.Count == 0) actions.Add("观察");

        var target = combat is null
            ? ""
            : $"，目标={Blank(combat.TargetNameText)}，目标血={PercentText(combat.TargetHpPercent)}{TargetDiagnosticText(combat)}";

        var actionText = string.Join("+", actions);
        Log.Info(Name, $"决策：{DecisionKindText(decision.Kind)}，动作={actionText}，原因={decision.Reason}{target}。", Target.Id);
        RecordCombatTelemetry(
            "decision",
            combat,
            _global.Current,
            decision: decision,
            actions: actionText);
    }

    private static string DecisionKindText(CombatLoopDecisionKind kind) => kind switch
    {
        CombatLoopDecisionKind.AcquireTarget => "寻找目标",
        CombatLoopDecisionKind.PendingFallbackAttack => "选怪后兜底",
        CombatLoopDecisionKind.PureKeyBaseline => "基础按键",
        CombatLoopDecisionKind.ContinueWeakTarget => "弱目标续打",
        CombatLoopDecisionKind.AttackCurrentTarget => "攻击当前目标",
        CombatLoopDecisionKind.SkipProtectedTarget => "保护目标",
        CombatLoopDecisionKind.TargetDeadSwitch => "死亡换怪",
        CombatLoopDecisionKind.TargetLostWait => "目标丢失等待",
        CombatLoopDecisionKind.TargetLostSwitch => "目标丢失换怪",
        CombatLoopDecisionKind.NoDamageDiagnosis => "无伤害诊断",
        CombatLoopDecisionKind.NoActionDiagnosis => "无动作诊断",
        CombatLoopDecisionKind.NoDamageReacquire => "无伤害换怪",
        CombatLoopDecisionKind.GrabMode => "抢怪模式",
        CombatLoopDecisionKind.ReturnPoint => "回点接管",
        CombatLoopDecisionKind.GameBuiltInMonitor => "内置挂机监控",
        CombatLoopDecisionKind.EnvironmentGate => "环境门禁",
        CombatLoopDecisionKind.Recovery => "恢复动作",
        CombatLoopDecisionKind.Stop => "停止",
        _ => "未知"
    };

    private static string RecoveryMetricText(string label) => label switch
    {
        "吃药回血" or "药品回血" or "共生回血" => "人物血量",
        "吃药回蓝" or "药品回蓝" or "血迹回蓝" => "人物蓝量",
        "宠粮回血" or "宠物口粮" => "宠物血量",
        _ => "状态值"
    };


    private static int ResolvePillCooldownMs(GlobalConfig cfg) =>
        Math.Max(10000, Math.Max(cfg.PillHealCooldownMs, cfg.PillManaCooldownMs));

    private bool TrySendDueSkill(GlobalConfig cfg, CombatRecognitionResult combat, SkillCastScope scope)
    {
        if (Target is null)
            return false;

        // A newly acquired target must receive the configured normal attack first.
        // This prevents a cooling/no-mana skill from becoming the only opening action.
        if (scope == SkillCastScope.CombatOnly
            && _firstAttackPendingAfterAcquire
            && !_attackSentForCurrentTarget)
        {
            UserDiagnosticThrottled(
                "技能门禁:等待首攻",
                $"技能暂缓：新目标必须先完成普通攻击；首攻键={Blank(GetAttackHotkey(cfg))}；"
                + $"{BuildCombatStateSummary(combat)}",
                $"scope={scope}; firstAttackPending={_firstAttackPendingAfterAcquire}; "
                + $"attackSent={_attackSentForCurrentTarget}; attackActions={_attackActionsOnCurrentTarget}",
                Target.Id,
                1500);
            return false;
        }

        if (cfg.SkillCooldownRecognitionEnabled)
        {
            // AI mode and custom-timer mode are intentionally exclusive. A failed
            // visual gate stays a no-op instead of falling through to custom skills.
            var visualResult = TrySendReadyVisualSkill(cfg, combat, scope);
            if (visualResult != VisualSkillDispatchResult.Sent)
            {
                UserDiagnosticThrottled(
                    $"技能门禁:AI:{scope}",
                    $"AI技能本轮未释放：{VisualSkillDispatchResultText(visualResult)}；"
                    + $"{BuildVisualSkillInventorySummary()}；{BuildCombatStateSummary(combat)}",
                    $"scope={scope}; result={visualResult}; {BuildSkillRuntimeDebugDetails()}",
                    Target.Id,
                    2500);
            }
            return visualResult == VisualSkillDispatchResult.Sent;
        }

        var pendingCustomSkill = _pendingCustomSkillVerification;
        if (EvaluatePendingCustomSkill(cfg))
        {
            if (pendingCustomSkill is not null)
            {
                var elapsedMs = Math.Max(0, Environment.TickCount64 - pendingCustomSkill.SentAtTicks);
                UserDiagnosticThrottled(
                    $"技能门禁:等待回读:{pendingCustomSkill.SkillId}",
                    $"自设技能等待结果回读：{pendingCustomSkill.SkillName}，已等待 {elapsedMs} 毫秒；"
                    + "回读结束后才会调度下一个技能。",
                    $"skillId={pendingCustomSkill.SkillId}; elapsedMs={elapsedMs}; "
                    + $"sourceFrame={pendingCustomSkill.FrameSequence}; "
                    + $"feedbackRequestFrame={pendingCustomSkill.FeedbackRequestFrameSequence}",
                    Target.Id,
                    1500);
            }
            return false;
        }

        var now = Environment.TickCount64;
        var executableSkills = GetExecutableSkills();
        var scopeEligible = 0;
        var conditionBlocked = 0;
        var intervalBlocked = 0;
        var retryBlocked = 0;
        long? nextDueMs = null;
        string nextDueSkill = "";
        foreach (var skill in executableSkills)
        {
            if (scope == SkillCastScope.BuffOnly && skill.Kind != SkillKind.Buff)
                continue;
            if (scope == SkillCastScope.CombatOnly && skill.Kind == SkillKind.Buff)
                continue;

            scopeEligible++;
            if (skill.Kind != SkillKind.Buff && !IsConditionExecutable(skill.Condition, cfg, combat))
            {
                conditionBlocked++;
                continue;
            }

            var intervalMs = Math.Max(0, skill.IntervalMs);
            if (_lastSkillTicks.TryGetValue(skill.Id, out var last) && now - last < intervalMs)
            {
                intervalBlocked++;
                var remainingMs = Math.Max(0, intervalMs - (now - last));
                if (!nextDueMs.HasValue || remainingMs < nextDueMs.Value)
                {
                    nextDueMs = remainingMs;
                    nextDueSkill = skill.Name;
                }
                continue;
            }
            if (_customSkillRetryUntil.TryGetValue(skill.Id, out var retryAt) && now < retryAt)
            {
                retryBlocked++;
                var remainingMs = Math.Max(0, retryAt - now);
                if (!nextDueMs.HasValue || remainingMs < nextDueMs.Value)
                {
                    nextDueMs = remainingMs;
                    nextDueSkill = skill.Name;
                }
                continue;
            }

            if (!Backend.TryConsumeGlobalCooldown(Target!, cfg.PublicCooldownMs))
            {
                InfoThrottled(
                    $"技能:公共CD:{skill.Id}",
                    $"技能跳过：{skill.Name} [{skill.Hotkey}] 已到释放条件，但公共 CD 未结束。",
                    Target.Id,
                    1500);
                return false;
            }

            RuntimePerceptionSnapshot? beforeDispatch = null;
            if (_perceptionHub is not null)
                _perceptionHub.TryGetLatest(Target.Id, out beforeDispatch!);
            if (!CastHotkeyWithLog(skill.Hotkey, skill.Kind == SkillKind.Buff ? "Buff" : "技能"))
                return false;

            // Message delivery is not the same as an accepted in-game cast. Keep a
            // short verification window so cooldown/resource/busy feedback can make
            // the skill retry quickly instead of incorrectly consuming its full CD.
            if (beforeDispatch is { FrameSequence: > 0 })
            {
                _pendingCustomSkillVerification = new PendingCustomSkillVerification(
                    skill.Id,
                    skill.Name,
                    skill.IntervalMs,
                    beforeDispatch.FrameSequence,
                    beforeDispatch.SelfBars.PlayerMpPercent ?? combat.PlayerMpPercent,
                    Environment.TickCount64,
                    0);
            }
            else
            {
                _lastSkillTicks[skill.Id] = Environment.TickCount64;
            }
            if (skill.Kind != SkillKind.Buff)
                NoteAttackAction();
            var label = skill.Kind == SkillKind.Buff ? "释放Buff" : "释放技能";
            Log.Info(
                Name,
                $"{label}：{skill.Name} [{skill.Hotkey}]，类型 {SkillKindText(skill.Kind)}，CD {skill.IntervalMs / 1000.0:0.###} 秒。",
                Target.Id);
            return true;
        }

        UserDiagnosticThrottled(
            $"技能门禁:自设:{scope}",
            BuildCustomSkillWaitSummary(
                scope,
                executableSkills.Count,
                scopeEligible,
                conditionBlocked,
                intervalBlocked,
                retryBlocked,
                nextDueSkill,
                nextDueMs),
            $"scope={scope}; executable={executableSkills.Count}; eligible={scopeEligible}; "
            + $"conditionBlocked={conditionBlocked}; intervalBlocked={intervalBlocked}; "
            + $"retryBlocked={retryBlocked}; nextDueSkill={nextDueSkill}; nextDueMs={nextDueMs}",
            Target.Id,
            2500);
        return false;
    }

    private bool EvaluatePendingCustomSkill(GlobalConfig cfg)
    {
        var pending = _pendingCustomSkillVerification;
        if (pending is null || Target is null)
            return false;

        if (_perceptionHub is null)
        {
            CommitCustomSkillCooldown(pending, "无视觉回读服务，沿用定时模式");
            return true;
        }

        var now = Environment.TickCount64;
        var elapsed = now - pending.SentAtTicks;
        var snapshot = _perceptionHub.Read(Target, cfg);
        if (snapshot.FrameSequence > pending.FrameSequence)
        {
            var requestFeedback = pending.FeedbackRequestFrameSequence <= 0;
            snapshot = _perceptionHub.Read(
                Target,
                cfg,
                requestFeedback
                    ? PerceptionReadOptions.SystemFeedbackTextAsync
                    : PerceptionReadOptions.SystemFeedbackTextAsyncPoll);
            if (requestFeedback && _pendingCustomSkillVerification is not null)
            {
                _pendingCustomSkillVerification = pending = pending with
                {
                    FeedbackRequestFrameSequence = snapshot.SystemFeedback.RequestedFrameSequence > 0
                        ? snapshot.SystemFeedback.RequestedFrameSequence
                        : snapshot.FrameSequence
                };
            }
        }

        var failure = snapshot.SystemFeedback.Observations
            .Where(item => item.FrameSequence > pending.FrameSequence)
            .Where(item => item.Semantic is SystemFeedbackSemanticKind.MaterialBusy
                or SystemFeedbackSemanticKind.Cooldown
                or SystemFeedbackSemanticKind.InsufficientResource
                or SystemFeedbackSemanticKind.InvalidAction)
            .OrderByDescending(item => item.FrameSequence)
            .Select(item => item.Semantic)
            .FirstOrDefault();
        if (failure != SystemFeedbackSemanticKind.Unknown)
        {
            _pendingCustomSkillVerification = null;
            var retryMs = CustomSkillRetryBackoffMs(failure);
            _customSkillRetryUntil[pending.SkillId] = now + retryMs;
            Log.Warn(
                Name,
                $"自设技能未被游戏接受：{pending.SkillName}，反馈={failure}；{retryMs}ms 后重试，不计入完整技能 CD。",
                Target.Id);
            return true;
        }

        var mpChanged = pending.PlayerMpPercent.HasValue
                        && snapshot.SelfBars.PlayerMpPercent.HasValue
                        && pending.PlayerMpPercent.Value != snapshot.SelfBars.PlayerMpPercent.Value;
        if (mpChanged)
        {
            CommitCustomSkillCooldown(pending, "人物蓝量变化");
            return true;
        }

        var asyncFeedbackCoversAction = snapshot.SystemFeedback.AsyncRequestInFlight
                                        && snapshot.SystemFeedback.RequestedFrameSequence > pending.FrameSequence;
        if (elapsed < CustomSkillNoFailureCommitMs
            || (asyncFeedbackCoversAction && elapsed < CustomSkillAsyncFeedbackMaxWaitMs))
        {
            return true;
        }

        CommitCustomSkillCooldown(pending, "未发现失败反馈");
        return true;
    }

    private void CommitCustomSkillCooldown(PendingCustomSkillVerification pending, string evidence)
    {
        _pendingCustomSkillVerification = null;
        _customSkillRetryUntil.Remove(pending.SkillId);
        _lastSkillTicks[pending.SkillId] = pending.SentAtTicks;
        if (Target is not null)
        {
            Log.Trace(
                Name,
                $"自设技能释放确认：{pending.SkillName}，证据={evidence}，按配置 CD {pending.IntervalMs / 1000.0:0.###} 秒计时。",
                Target.Id);
        }
    }

    private static int CustomSkillRetryBackoffMs(SystemFeedbackSemanticKind feedback) => feedback switch
    {
        SystemFeedbackSemanticKind.MaterialBusy => 350,
        SystemFeedbackSemanticKind.Cooldown => 1000,
        SystemFeedbackSemanticKind.InsufficientResource => 1500,
        _ => 750
    };

    private bool TrySendDueSkillAfterAttack(
        GlobalConfig cfg,
        CombatRecognitionResult combat,
        AttackHotkeyResult attackResult)
    {
        if (attackResult != AttackHotkeyResult.Sent)
            return TrySendDueSkill(cfg, combat, SkillCastScope.CombatOnly);

        // The game rejects a same-tick attack + skill burst, but deferring to the next
        // complete vision pass creates a multi-second idle gap. Queue one lightweight
        // dispatch after the configured public CD instead. Target transitions clear it.
        _pendingOpeningSkillDispatch = new PendingOpeningSkillDispatch(
            combat,
            Environment.TickCount64 + ResolveOpeningSkillDelayMs(cfg));
        return false;
    }

    private async Task<bool> TryDispatchPendingOpeningSkillAsync(GlobalConfig cfg, CancellationToken token)
    {
        var pending = _pendingOpeningSkillDispatch;
        if (pending is null || Target is null)
            return false;

        if (!_attackSentForCurrentTarget
            || _killSwitchPhase == KillSwitchPhase.AwaitingTarget
            || IsDeadOrEmptyTargetHp(pending.Combat))
        {
            UserDiagnosticThrottled(
                "技能调度:首攻后任务取消",
                $"首攻后的技能调度已取消：首攻完成={_attackSentForCurrentTarget}，"
                + $"战斗阶段={_killSwitchPhase}，目标血={PercentText(pending.Combat.TargetHpPercent)}。",
                $"attackSent={_attackSentForCurrentTarget}; phase={_killSwitchPhase}; "
                + $"targetHp={pending.Combat.TargetHpPercent}",
                Target.Id,
                1500);
            _pendingOpeningSkillDispatch = null;
            return false;
        }

        var remainingMs = pending.DueAtTicks - Environment.TickCount64;
        if (remainingMs > 0)
            await Task.Delay((int)Math.Min(remainingMs, 500), token).ConfigureAwait(false);

        if (!ReferenceEquals(_pendingOpeningSkillDispatch, pending)
            || Target is null
            || !_attackSentForCurrentTarget
            || _killSwitchPhase == KillSwitchPhase.AwaitingTarget)
        {
            return false;
        }

        _pendingOpeningSkillDispatch = null;
        _actionSentThisScan = false;
        var sent = TrySendDueSkill(cfg, pending.Combat, SkillCastScope.CombatOnly);
        if (sent)
        {
            Log.Trace(
                Name,
                $"首攻后技能已按公共 CD {ResolveOpeningSkillDelayMs(cfg)}ms 轻量调度，不等待下一次完整视觉扫描。",
                Target.Id);
        }
        else
        {
            UserDiagnosticThrottled(
                "技能调度:首攻后无技能",
                $"首攻已完成，但本轮没有技能发出；技能模式="
                + $"{(cfg.SkillCooldownRecognitionEnabled ? "AI自动判定" : "自设按键")}；"
                + $"{BuildVisualOrCustomSkillSummary(cfg)}",
                BuildSkillRuntimeDebugDetails(),
                Target.Id,
                2000);
        }

        return sent;
    }

    private static int ResolveOpeningSkillDelayMs(GlobalConfig cfg) =>
        Math.Clamp(cfg.PublicCooldownMs, 200, 500);

    private VisualSkillDispatchResult TrySendReadyVisualSkill(
        GlobalConfig cfg,
        CombatRecognitionResult combat,
        SkillCastScope scope)
    {
        if (Target is null || _perceptionHub is null || !cfg.SkillCooldownRecognitionEnabled)
            return VisualSkillDispatchResult.Unavailable;

        var snapshot = _perceptionHub.Read(Target, cfg);
        if (_pendingVisualSkillVerification is { } verification
            && snapshot.FrameSequence > verification.FrameSequence)
        {
            var requestFeedback = verification.FeedbackRequestFrameSequence <= 0;
            snapshot = _perceptionHub.Read(
                Target,
                cfg,
                requestFeedback
                    ? PerceptionReadOptions.SystemFeedbackTextAsync
                    : PerceptionReadOptions.SystemFeedbackTextAsyncPoll);
            if (requestFeedback && _pendingVisualSkillVerification is not null)
            {
                _pendingVisualSkillVerification = verification with
                {
                    FeedbackRequestFrameSequence = snapshot.SystemFeedback.RequestedFrameSequence > 0
                        ? snapshot.SystemFeedback.RequestedFrameSequence
                        : snapshot.FrameSequence
                };
            }
        }
        var pending = EvaluatePendingVisualSkill(snapshot);
        if (pending == PendingVisualSkillResult.WaitingOrResolved)
            return VisualSkillDispatchResult.InventoryAvailable;
        var observedSlots = snapshot.SkillBar.VisualSlots;
        var visualInventory = observedSlots
            .Where(slot => slot.FrameSequence > 0 && slot.FrameSequence == snapshot.FrameSequence)
            .Where(slot => slot.Identity == VisionSkillIdentityState.Matched)
            .Where(slot => string.Equals(slot.IdentitySource, "icons_ingame", StringComparison.OrdinalIgnoreCase))
            .Where(slot => slot.HangUsable)
            .Where(slot => string.Equals(slot.ReleaseRule, "allowed_by_policy", StringComparison.OrdinalIgnoreCase))
            .Where(IsExecutableVisualSlot)
            .ToArray();
        if (visualInventory.Length == 0)
        {
            var matched = observedSlots.Count(slot => slot.Identity == VisionSkillIdentityState.Matched);
            var ready = observedSlots.Count(slot => slot.Cooldown == VisionCooldownState.Ready);
            var castable = observedSlots.Count(slot => slot.Castable);
            InfoThrottled(
                "视觉技能:无可执行库存",
                $"视觉技能未释放：槽位 {observedSlots.Count}，身份匹配 {matched}，CD 就绪 {ready}，可执行 {castable}；需要同帧游戏内模板、挂机策略和合法键位/点击点同时通过。",
                Target.Id,
                5000);
            return VisualSkillDispatchResult.Unavailable;
        }

        // 视觉库存只负责回答“是什么技能、CD 是否就绪”。实际键位来自
        // 当前角色改键或用户技能预设，稳定观察两帧后再补齐 SkillId 策略。
        SynchronizeVisualSkillPolicies(cfg, visualInventory);

        var policies = cfg.AiSkills
            .Where(skill => skill.Enabled)
            .Where(skill => !string.IsNullOrWhiteSpace(skill.ExpectedVisualSkillId))
            .Where(skill => scope == SkillCastScope.BuffOnly
                ? skill.Kind == SkillKind.Buff
                : skill.Kind != SkillKind.Buff)
            .Where(skill => skill.Kind == SkillKind.Buff || IsConditionExecutable(skill.Condition, cfg, combat))
            .ToArray();
        if (policies.Length == 0)
        {
            InfoThrottled(
                "视觉技能:无角色策略",
                $"视觉技能未释放：已发现 {visualInventory.Length} 个可信技能，但当前角色档案没有对应 SkillId 策略。",
                Target.Id,
                5000);
            return VisualSkillDispatchResult.InventoryAvailable;
        }

        var candidates = visualInventory
            .SelectMany(slot => policies
                .Where(policy => string.Equals(
                    policy.ExpectedVisualSkillId.Trim(),
                    slot.SkillId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .Select(policy => (
                    Slot: slot,
                    Policy: policy,
                    Hotkey: ResolveVisualKeyboardHotkey(slot, policy))))
            .OrderBy(candidate => candidate.Policy.Priority)
            .ThenByDescending(candidate => candidate.Slot.MatchScore)
            .ThenBy(candidate => candidate.Slot.Row)
            .ThenBy(candidate => candidate.Slot.Column)
            .ToArray();

        if (candidates.Length == 0)
        {
            var expectedIds = string.Join(", ", policies
                .Select(policy => policy.ExpectedVisualSkillId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            InfoThrottled(
                "视觉技能:策略未命中槽位",
                $"视觉技能未释放：角色策略 [{expectedIds}] 未命中当前可信技能，可能是角色配置串档或模板未覆盖。",
                Target.Id,
                5000);
            return VisualSkillDispatchResult.InventoryAvailable;
        }

        var now = Environment.TickCount64;
        foreach (var candidate in candidates)
        {
            var slot = candidate.Slot;
            var policy = candidate.Policy;
            var effectiveHotkey = candidate.Hotkey;
            if (!slot.Castable || slot.Cooldown != VisionCooldownState.Ready)
                continue;
            if (string.Equals(slot.ActivationKind, "keyboard", StringComparison.OrdinalIgnoreCase)
                && !Backend.TryParseHotkey(effectiveHotkey, out var error))
            {
                WarnThrottled(
                    $"视觉技能热键:{slot.SlotId}",
                    $"视觉技能热键无效：{slot.SkillName} [{effectiveHotkey}] | {error}",
                    Target.Id);
                continue;
            }

            // Debounce follows stable SkillId so moving or duplicating a skill does
            // not create independent timers for each slot.
            var dispatchId = $"vision:{slot.SkillId}";
            var debounceMs = Math.Max(200, cfg.SkillCooldownReadyDebounceMs);
            if (_visualSkillBackoffUntil.TryGetValue(slot.SkillId, out var backoffUntil)
                && now < backoffUntil)
                continue;
            if (_lastSkillTicks.TryGetValue(dispatchId, out var last) && now - last < debounceMs)
                continue;
            if (!Backend.TryConsumeGlobalCooldown(Target!, cfg.PublicCooldownMs))
            {
                InfoThrottled(
                    "视觉技能:公共CD",
                    $"视觉技能等待公共 CD：{slot.SkillName} [{VisualSlotActionText(slot, effectiveHotkey)}] 已就绪。",
                    Target.Id,
                    1500);
                return VisualSkillDispatchResult.InventoryAvailable;
            }
            if (!DispatchVisualSkill(slot, effectiveHotkey, cfg))
                return VisualSkillDispatchResult.InventoryAvailable;

            _lastSkillTicks[dispatchId] = now;
            _pendingVisualSkillVerification = new PendingVisualSkillVerification(
                slot.SkillId,
                slot.SkillName,
                snapshot.FrameSequence,
                snapshot.SelfBars.PlayerMpPercent,
                now,
                0);
            NoteAttackAction();
            Log.Info(
                Name,
                $"释放视觉技能：{slot.SkillName} [{VisualSlotActionText(slot, effectiveHotkey)}]，策略 {policy.Name}，表面 {slot.Surface}，槽位 {slot.SlotId}，匹配 {slot.MatchScore:0.000}。",
                Target.Id);
            return VisualSkillDispatchResult.Sent;
        }

        var blockedSummary = string.Join("；", candidates
            .Take(4)
            .Select(candidate =>
                $"{candidate.Slot.SkillName}[{VisualSlotActionText(candidate.Slot, candidate.Hotkey)}]={candidate.Slot.Cooldown}/{(candidate.Slot.Castable ? "可执行" : "受阻")}"));
        InfoThrottled(
            "视觉技能:尚未就绪",
            $"视觉技能未释放：{blockedSummary}。",
            Target.Id,
            3000);

        return VisualSkillDispatchResult.InventoryAvailable;
    }

    private PendingVisualSkillResult EvaluatePendingVisualSkill(RuntimePerceptionSnapshot snapshot)
    {
        var pending = _pendingVisualSkillVerification;
        if (pending is null || Target is null)
            return PendingVisualSkillResult.None;

        var now = Environment.TickCount64;
        var elapsed = now - pending.SentAtTicks;
        if (snapshot.FrameSequence <= pending.FrameSequence)
            return elapsed < VisualSkillFastEvidenceWaitMs
                ? PendingVisualSkillResult.WaitingOrResolved
                : ResolveVisualSkillWithoutEvidence(pending, now);

        var sameSkill = snapshot.SkillBar.VisualSlots
            .Where(slot => slot.FrameSequence == snapshot.FrameSequence)
            .Where(slot => string.Equals(slot.SkillId, pending.SkillId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(slot => slot.MatchScore)
            .FirstOrDefault();
        var mpChanged = pending.PlayerMpPercent.HasValue
                        && snapshot.SelfBars.PlayerMpPercent.HasValue
                        && pending.PlayerMpPercent.Value != snapshot.SelfBars.PlayerMpPercent.Value;
        if (sameSkill?.Cooldown == VisionCooldownState.Cooling || mpChanged)
        {
            _pendingVisualSkillVerification = null;
            Log.Info(
                Name,
                $"视觉技能回读成功：{pending.SkillName}，证据={(sameSkill?.Cooldown == VisionCooldownState.Cooling ? "Ready→Cooling" : "人物蓝量变化")}，帧 {pending.FrameSequence}→{snapshot.FrameSequence}。",
                Target.Id);
            return PendingVisualSkillResult.WaitingOrResolved;
        }

        var feedback = snapshot.SystemFeedback.Observations
            .Where(item => item.FrameSequence > pending.FrameSequence)
            .Where(item => item.Semantic is SystemFeedbackSemanticKind.MaterialBusy
                or SystemFeedbackSemanticKind.Cooldown
                or SystemFeedbackSemanticKind.InsufficientResource
                or SystemFeedbackSemanticKind.InvalidAction)
            .OrderByDescending(item => item.FrameSequence)
            .Select(item => item.Semantic)
            .FirstOrDefault();
        if (feedback != SystemFeedbackSemanticKind.Unknown)
        {
            _pendingVisualSkillVerification = null;
            _visualSkillBackoffUntil[pending.SkillId] = now + 2000;
            Log.Warn(Name, $"视觉技能回读失败：{pending.SkillName}，系统反馈={feedback}；2 秒内不重复发送。", Target.Id);
            return PendingVisualSkillResult.WaitingOrResolved;
        }

        var asyncFeedbackCoversAction = snapshot.SystemFeedback.AsyncRequestInFlight
                                        && snapshot.SystemFeedback.RequestedFrameSequence > pending.FrameSequence;
        if (elapsed < VisualSkillFastEvidenceWaitMs
            || (asyncFeedbackCoversAction && elapsed < VisualSkillAsyncFeedbackMaxWaitMs))
            return PendingVisualSkillResult.WaitingOrResolved;
        return ResolveVisualSkillWithoutEvidence(pending, now);
    }

    private PendingVisualSkillResult ResolveVisualSkillWithoutEvidence(
        PendingVisualSkillVerification pending,
        long now)
    {
        _pendingVisualSkillVerification = null;
        _visualSkillBackoffUntil[pending.SkillId] = now + 1500;
        if (Target is not null)
        {
            Log.Warn(
                Name,
                $"视觉技能未获得释放后证据：{pending.SkillName}；未观察到 Cooling、资源变化或系统反馈，1.5 秒内不重发。",
                Target.Id);
        }
        return PendingVisualSkillResult.WaitingOrResolved;
    }

    private static bool IsExecutableVisualSlot(VisionSkillSlotPerception slot)
    {
        if (string.Equals(slot.Surface, "bottom_skill_bar", StringComparison.OrdinalIgnoreCase)
            && string.Equals(slot.ActivationKind, "keyboard", StringComparison.OrdinalIgnoreCase))
        {
            // 模型输出的 Key 只是槽位提示，玩家可能已经改键；这里只校验槽位几何。
            return slot.Column >= 0;
        }

        return string.Equals(slot.Surface, "right_skill_bar", StringComparison.OrdinalIgnoreCase)
               && string.Equals(slot.ActivationKind, "mouse", StringComparison.OrdinalIgnoreCase)
               && slot.ClickX.HasValue
               && slot.ClickY.HasValue;
    }

    private bool DispatchVisualSkill(
        VisionSkillSlotPerception slot,
        string effectiveHotkey,
        GlobalConfig cfg)
    {
        if (Target is null)
            return false;
        if (string.Equals(slot.ActivationKind, "keyboard", StringComparison.OrdinalIgnoreCase))
            return CastHotkeyWithLog(effectiveHotkey, "视觉技能");
        if (!string.Equals(slot.ActivationKind, "mouse", StringComparison.OrdinalIgnoreCase)
            || !slot.ClickX.HasValue
            || !slot.ClickY.HasValue
            || slot.ClickX.Value < 0
            || slot.ClickY.Value < 0
            || slot.ClickX.Value >= Target.ClientWidth
            || slot.ClickY.Value >= Target.ClientHeight)
        {
            WarnThrottled(
                $"视觉技能点击点:{slot.SlotId}",
                $"视觉技能点击点无效：{slot.SkillName} {slot.ClickX},{slot.ClickY}。",
                Target.Id);
            return false;
        }

        return Backend.ClickClient(Target, slot.ClickX.Value, slot.ClickY.Value, cfg);
    }

    private static string VisualSlotActionText(
        VisionSkillSlotPerception slot,
        string effectiveHotkey) =>
        string.Equals(slot.ActivationKind, "mouse", StringComparison.OrdinalIgnoreCase)
            ? $"{slot.ClickX},{slot.ClickY}"
            : effectiveHotkey;

    private string ResolveVisualKeyboardHotkey(
        VisionSkillSlotPerception slot,
        SkillPreset? policy)
    {
        if (!string.Equals(slot.ActivationKind, "keyboard", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        // 底栏列与游戏 action1..action15 对齐。角色配置 key1 是实际当前键位，
        // 比模型 worker 中的默认槽位标签更可信。
        if (slot.Column >= 0
            && TryResolveCurrentMapping(GameInputSemanticIds.SkillSlot(slot.Column + 1), out var binding))
        {
            return binding.Hotkey;
        }

        return policy?.Hotkey?.Trim() ?? string.Empty;
    }

    private void SynchronizeVisualSkillPolicies(
        GlobalConfig cfg,
        IReadOnlyCollection<VisionSkillSlotPerception> visualInventory)
    {
        cfg.AiSkills ??= new List<SkillPreset>();
        var trusted = visualInventory
            .Where(slot => slot.Identity == VisionSkillIdentityState.Matched)
            .Where(slot => string.Equals(slot.IdentitySource, "icons_ingame", StringComparison.OrdinalIgnoreCase))
            .Where(slot => slot.HangUsable)
            .Where(slot => string.Equals(slot.ReleaseRule, "allowed_by_policy", StringComparison.OrdinalIgnoreCase))
            .Where(slot => !string.IsNullOrWhiteSpace(slot.SkillId))
            .GroupBy(slot => slot.SkillId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(slot => slot.MatchScore)
                .ThenBy(slot => slot.Row)
                .ThenBy(slot => slot.Column)
                .First())
            .ToArray();

        var visibleIds = trusted
            .Select(slot => slot.SkillId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in _visualSkillPolicyObservations.Keys.Where(key => !visibleIds.Contains(key)).ToArray())
            _visualSkillPolicyObservations.Remove(stale);

        var changed = false;
        foreach (var slot in trusted)
        {
            var skillId = slot.SkillId.Trim();
            var observations = _visualSkillPolicyObservations.TryGetValue(skillId, out var count)
                ? count + 1
                : 1;
            _visualSkillPolicyObservations[skillId] = Math.Min(observations, 3);
            if (observations < 2
                || cfg.AiSkills.Any(skill => string.Equals(
                    skill.ExpectedVisualSkillId?.Trim(),
                    skillId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var resolvedHotkey = ResolveVisualKeyboardHotkey(slot, null);
            var policy = cfg.AiSkills.FirstOrDefault(skill =>
                string.IsNullOrWhiteSpace(skill.ExpectedVisualSkillId)
                && !string.IsNullOrWhiteSpace(resolvedHotkey)
                && string.Equals(skill.Hotkey.Trim(), resolvedHotkey, StringComparison.OrdinalIgnoreCase));
            if (policy is null)
            {
                if (string.Equals(slot.ActivationKind, "keyboard", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(resolvedHotkey))
                {
                    InfoThrottled(
                        $"视觉技能:等待真实键位:{skillId}",
                        $"已识别技能 {slot.SkillName} [{skillId}]，但当前角色没有可验证的改键映射；暂不自动编入。",
                        Target?.Id ?? "-",
                        5000);
                    continue;
                }

                policy = new SkillPreset
                {
                    Hotkey = resolvedHotkey,
                    Enabled = true,
                    Priority = cfg.AiSkills.Count == 0 ? 1 : cfg.AiSkills.Max(skill => skill.Priority) + 1,
                    IntervalMs = Math.Max(200, cfg.SkillCooldownReadyDebounceMs),
                    Condition = SkillCondition.TargetExists,
                    Cooldown = new SkillCooldownSlotConfig { Enabled = true }
                };
                cfg.AiSkills.Add(policy);
            }

            policy.ExpectedVisualSkillId = skillId;
            policy.Name = string.IsNullOrWhiteSpace(slot.SkillName) ? skillId : slot.SkillName.Trim();
            policy.Kind = VisualPolicyKind(slot.PolicyClass);
            policy.Condition = policy.Kind == SkillKind.Buff ? SkillCondition.Always : SkillCondition.TargetExists;
            policy.Cooldown ??= new SkillCooldownSlotConfig();
            policy.Cooldown.Enabled = true;
            changed = true;
            Log.Info(
                Name,
                $"已导入视觉技能：{policy.Name} [{skillId}]，当前动作 {VisualSlotActionText(slot, ResolveVisualKeyboardHotkey(slot, policy))}，匹配 {slot.MatchScore:0.000}。",
                Target?.Id ?? "-");
        }

        if (changed)
            _global.NotifyChanged();
    }

    private static SkillKind VisualPolicyKind(string? policyClass)
    {
        var normalized = policyClass?.Trim() ?? string.Empty;
        if (normalized.Contains("buff", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("增益", StringComparison.OrdinalIgnoreCase))
        {
            return SkillKind.Buff;
        }

        if (normalized.Contains("aoe", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("范围", StringComparison.OrdinalIgnoreCase))
        {
            return SkillKind.Aoe;
        }

        return SkillKind.Damage;
    }

    private enum VisualSkillDispatchResult
    {
        Unavailable,
        InventoryAvailable,
        Sent
    }

    private enum PendingVisualSkillResult
    {
        None,
        WaitingOrResolved
    }

    private sealed record PendingVisualSkillVerification(
        string SkillId,
        string SkillName,
        long FrameSequence,
        int? PlayerMpPercent,
        long SentAtTicks,
        long FeedbackRequestFrameSequence);

    private sealed record PendingCustomSkillVerification(
        string SkillId,
        string SkillName,
        int IntervalMs,
        long FrameSequence,
        int? PlayerMpPercent,
        long SentAtTicks,
        long FeedbackRequestFrameSequence);

    private sealed record PendingOpeningSkillDispatch(
        CombatRecognitionResult Combat,
        long DueAtTicks);

    private SkillCooldownInspection InspectSkillCooldown(GlobalConfig cfg, SkillPreset skill)
    {
        if (Target is null || _skillCooldown is null)
            return new SkillCooldownInspection(SkillCooldownReadiness.Disabled, "service-disabled", 0, 0, 0, 0, 0, 0, 0);

        return _skillCooldown.Inspect(Target, skill, cfg);
    }

    private List<SkillPreset> GetExecutableSkills()
    {
        return _global.Current.Skills
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Hotkey))
            .Where(s =>
            {
                var ok = Backend.TryParseHotkey(s.Hotkey, out var error);
                if (!ok)
                    Log.Warn(Name, $"技能热键无效：{s.Name} [{s.Hotkey}] | {error}", Target?.Id ?? "-");
                return ok;
            })
            // Priority is the persisted drag order. LINQ's stable ordering keeps
            // the profile list order as the deterministic fallback for old files.
            .OrderBy(s => s.Priority)
            .ToList();
    }

    private static bool IsConditionExecutable(SkillCondition condition, GlobalConfig cfg, CombatRecognitionResult combat)
    {
        return condition switch
        {
            SkillCondition.Always => true,
            SkillCondition.TargetExists => combat.HasTarget,
            SkillCondition.EmHeal => false,
            SkillCondition.HpLow => combat.PlayerHpPercent is not null && combat.PlayerHpPercent <= cfg.HpThresholdPct,
            SkillCondition.MpLow => combat.PlayerMpPercent is not null && combat.PlayerMpPercent <= cfg.MpThresholdPct,
            _ => false
        };
    }

    private static string SkillKindText(SkillKind kind) => kind switch
    {
        SkillKind.Damage => "伤害",
        SkillKind.Aoe => "AOE",
        SkillKind.Buff => "增益Buff",
        _ => kind.ToString()
    };

    private void WriteUnsupportedConditionWarningIfNeeded(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (_conditionWarningWritten)
            return;

        var needsStatusNumber =
            cfg.EnableEmHeal
            || cfg.Skills.Any(s => s.Enabled && s.Condition is SkillCondition.HpLow or SkillCondition.MpLow);

        if (!needsStatusNumber)
            return;

        if (combat.PlayerHpPercent is not null || combat.PlayerMpPercent is not null)
            return;

        _conditionWarningWritten = true;
        Log.Warn(Name, "未识别到血蓝数值，暂不触发。", Target?.Id ?? "-");
    }

    private Task DelayForNextScan(GlobalConfig cfg, CancellationToken token)
    {
        var maximumDelayMs = _killSwitchPhase switch
        {
            KillSwitchPhase.AwaitingTarget => 20,
            KillSwitchPhase.Fighting => 100,
            _ => 250
        };
        var minimumDelayMs = _killSwitchPhase == KillSwitchPhase.AwaitingTarget ? 20 : 50;
        return Task.Delay(Math.Max(minimumDelayMs, Math.Min(maximumDelayMs, cfg.Targeting.ScanIntervalMs)), token);
    }

    private void LogCombatSnapshotIfDue(CombatRecognitionResult combat, string reason)
    {
        if (Target is null)
            return;

        var now = Environment.TickCount64;
        if (_lastCombatSnapshotTicks != 0 && now - _lastCombatSnapshotTicks < 3000)
            return;

        _lastCombatSnapshotTicks = now;
        var cfg = _global.Current;
        var targetDecision = EvaluateTarget(cfg, combat);
        var userSummary =
            $"战斗快照：{reason}；{BuildCombatStateSummary(combat)}；"
            + $"人物血={PercentText(combat.PlayerHpPercent)}，人物蓝={PercentText(combat.PlayerMpPercent)}，"
            + $"宠物血={PercentText(combat.PetHpPercent)}；阶段={_killSwitchPhase}，"
            + $"首攻={(_attackSentForCurrentTarget ? "已发送" : _firstAttackPendingAfterAcquire ? "等待中" : "未发送")}，"
            + $"策略={targetDecision.Reason}。";
        Log.Log(new LogEntry
        {
            Level = LogLevel.Info,
            Module = Name,
            Message = userSummary,
            Audience = LogAudience.User,
            UserSummary = userSummary,
            DebugDetails = BuildCombatDebugDetails(cfg, combat, targetDecision),
            Window = Target.Id,
            FeatureKey = FeatureKeys.HangNormal
        });
    }

    private void LogRecognizedTargetName(GlobalConfig cfg, CombatRecognitionResult combat)
    {
        if (Target is null || !cfg.Targeting.L2TargetNameEnabled)
            return;

        var name = combat.TargetNameText?.Trim() ?? "";
        if (name.Length == 0)
        {
            if (!combat.HasTarget || combat.TargetHpPercent is 0)
                _lastUserVisibleTargetName = "";
            return;
        }

        if (string.Equals(name, _lastUserVisibleTargetName, StringComparison.Ordinal))
            return;

        _lastUserVisibleTargetName = name;
        Log.Log(new LogEntry
        {
            Level = LogLevel.Info,
            Module = Name,
            Message = $"L2 target name recognized: {name}",
            Window = Target.Id,
            Audience = LogAudience.User,
            UserSummary = $"识别到目标：{name}。"
        });
    }

    private static string TargetDiagnosticText(CombatRecognitionResult combat)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(combat.TargetSampleKind))
        {
            var label = string.IsNullOrWhiteSpace(combat.TargetSampleLabel)
                ? combat.TargetSampleKind
                : combat.TargetSampleLabel;
            parts.Add($"样本={combat.TargetSampleKind}/{label}/{combat.TargetSampleScore:0.000}");
        }

        if (combat.TargetLooksLikePlayer)
            parts.Add("明确玩家");
        else if (combat.TargetProtectionAmbiguous)
            parts.Add("低置信玩家样本");
        if (combat.TargetLooksLikePet)
            parts.Add("宠物目标");
        if (combat.TargetIsAttackable is not null)
            parts.Add($"可攻击={combat.TargetIsAttackable}");
        var structure = TargetStructureText(combat);
        if (!string.IsNullOrWhiteSpace(structure))
            parts.Add($"结构={structure}");

        return parts.Count == 0 ? "" : "，" + string.Join("，", parts);
    }

    private static string TargetStructureText(CombatRecognitionResult combat)
    {
        var parts = new List<string>();
        if (combat.TargetHasSchoolMarker)
            parts.Add("门派");
        if (combat.TargetHasPlayerPortrait)
            parts.Add("头像");
        if (combat.TargetHasPlayerStatusBars)
            parts.Add("三条");
        return string.Join("/", parts);
    }

    private bool CastHotkeyWithLog(string hotkey, string action)
    {
        if (Target is null)
            return false;

        var ok = Backend.CastHotkey(Target, hotkey, _global.Current);
        if (ok)
        {
            _actionSentThisScan = true;
            return true;
        }

        var now = Environment.TickCount64;
        if (_lastInputFailureTicks == 0 || now - _lastInputFailureTicks >= 1500)
        {
            _lastInputFailureTicks = now;
            Log.Log(new LogEntry
            {
                Level = LogLevel.Warn,
                Module = Name,
                Message = $"{action}热键发送失败：{hotkey}。",
                Window = Target.Id,
                Audience = LogAudience.User,
                UserSummary = $"输入失败：{action}键 {hotkey} 未被游戏窗口接受，下一轮会继续尝试。",
                DebugDetails = $"action={action}; hotkey={hotkey}; inputMode={_global.Current.InputDispatchMode}",
                FeatureKey = FeatureKeys.HangNormal
            });
        }

        return false;
    }

    private static string PercentText(int? value) => value is null ? "-" : $"{value}%";

    private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private void TryTriggerGameBuiltInHangSwitch(GlobalConfig cfg)
    {
        if (Target is null || cfg.GameBuiltInHangSwitchMode == GameBuiltInHangSwitchMode.Disabled)
            return;

        if (_gameBuiltInHangSwitch is null)
        {
            Log.Warn(Name, "内置挂机开关服务未接入，本次只进入监控模式。", Target.Id);
            return;
        }

        var result = _gameBuiltInHangSwitch.TryTrigger(Target, cfg, execute: true);
        if (result.Executed)
        {
            Log.Info(Name, result.Message, Target.Id);
            return;
        }

        var message = result.IsValid
            ? result.Message
            : $"内置挂机开关配置无效：{result.Message}";
        Log.Warn(Name, message, Target.Id);
    }

    private static string TargetSwitchModeText(TargetSwitchMode mode) => mode switch
    {
        TargetSwitchMode.Grab => "抢怪",
        TargetSwitchMode.KillThenSwitch => "打死换怪",
        TargetSwitchMode.BossWhitelist => "BOSS白名单",
        TargetSwitchMode.Dungeon => "副本",
        _ => mode.ToString()
    };

    private static bool IsKillStyleMode(TargetSwitchMode mode) =>
        mode is TargetSwitchMode.KillThenSwitch or TargetSwitchMode.BossWhitelist or TargetSwitchMode.Dungeon;

    private void RefreshAutoBackendIfDue(GlobalConfig cfg)
    {
        if (Target is null || cfg.CombatBackendPreference != CombatBackendPreference.Auto)
            return;

        var now = Environment.TickCount64;
        var intervalMs = ResolveBackendRecheckMs(cfg);
        if (_lastBackendRecheckTicks != 0 && now - _lastBackendRecheckTicks < intervalMs)
            return;

        _lastBackendRecheckTicks = now;
        var previous = _backend;
        var next = _backendSelector.Resolve(Target, cfg, out var status);
        if (!status.IsAvailable)
        {
            Log.Warn(Name, $"自动判断战斗方式暂不可用，保持当前方式：{status.Message}", Target.Id);
            return;
        }

        if (previous?.Kind == next.Kind)
        {
            _backend = next;
            return;
        }

        _backend = next;
        ResetTargetAttackProgress();
        _pendingAcquireAttackTicks = 0;
        _lastAcquireTicks = 0;
        _lastAttackTicks = 0;

        if (next.Kind == CombatBackendKind.InternalPlugin)
        {
            Log.Info(Name, "自动判断切换为内置挂机监控：本程序暂停选怪/普攻/技能，只保留回点、死亡和卡点监控。", Target.Id);
            TryTriggerGameBuiltInHangSwitch(cfg);
            return;
        }

        Log.Info(Name, "自动判断切换为外部按键打怪：恢复选怪、普攻和技能循环。", Target.Id);
    }

    private static int ResolveBackendRecheckMs(GlobalConfig cfg) =>
        Math.Clamp(Math.Max(cfg.Targeting.ScanIntervalMs * 4, 3000), 3000, 15000);

    private static bool IsGameBuiltInMonitorMode(GlobalConfig cfg, ICombatBackend? backend = null) =>
        cfg.CombatBackendPreference == CombatBackendPreference.InternalPlugin
        || backend?.Kind == CombatBackendKind.InternalPlugin;

    private enum SkillCastScope
    {
        BuffOnly,
        CombatOnly
    }

}
