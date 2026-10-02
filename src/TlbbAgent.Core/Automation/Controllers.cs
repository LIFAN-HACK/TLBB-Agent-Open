using TlbbAgent.Contracts;

namespace TlbbAgent.Core.Automation;

public enum NormalHangState
{
    Idle, BindWindow, CheckRoleStatus, SelectTarget, ValidateTarget,
    AttackLoop, CheckHpMpPet, DiagnoseNoDamage, LootOrNextTarget, ReturnPointIfNeeded, ErrorRecovery, Stop
}

public enum HorseBanditState
{
    Idle, BindWindow, CheckClientReady, CheckTeamState, FollowLeader,
    SearchHorseBandit, SelectTarget, AttackTarget, WaitBattle,
    DetectKillOrLostTarget, Recover, ReturnToFollow, Stop
}

public enum ActivityState
{
    Idle, WaitActivityWindow, PrepareTeam, NavigateOrFollow, DetectNpcOrEntry,
    EnterBattleOrDungeon, ExecuteFight, ConfirmCompletion, Cleanup, NextRunOrStop
}

public enum NpcGrabState { Idle, Scan, SelectTarget, Action, Retry, Stop }

public enum AutoRebelState
{
    Idle,
    ValidateConfig,
    RecognizePosition,
    NavigateRoute,
    DetectTarget,
    EngageTarget,
    WaitBattle,
    ConfirmCompletion,
    Recover,
    Stop
}

public enum AutoMonkState
{
    Idle,
    ValidateConfig,
    RecognizePosition,
    NavigateRoute,
    DetectTarget,
    EngageTarget,
    WaitBattle,
    DetectActivityEnd,
    Recover,
    Stop
}

public enum DungeonState
{
    Idle,
    ValidateConfig,
    PrepareTeam,
    ExecuteScriptStep,
    NavigateStage,
    EnterBattleOrDungeon,
    ExecuteFight,
    ConfirmCompletion,
    Cleanup,
    ChainNextTask,
    Recover,
    Stop
}

public enum ReturnPointState
{
    Idle,
    ValidateConfig,
    WaitForReturnCondition,
    ReturnToPoint,
    EarthSpiritReturn,
    DeathReturn,
    StuckRecovery,
    ErrorRecovery,
    Stop
}

public enum LifeCollectState
{
    Idle,
    ValidateConfig,
    RecognizePosition,
    NavigatePoint,
    Collect,
    NextPoint,
    BagFull,
    Recover,
    Stop
}

public enum TradeState
{
    Idle,
    ValidateConfig,
    RecognizePosition,
    NavigatePoint,
    ExecuteTradeAction,
    WaitAfterAction,
    NextPoint,
    Recover,
    Stop
}

public enum MaterialCraftState
{
    Idle,
    ValidateConfig,
    EnsurePanel,
    EntryUnavailable,
    ExecuteStep,
    ObserveFeedback,
    BusyWait,
    WaitAfterStep,
    CleanupBag,
    NextStep,
    NextBatch,
    Recover,
    Stop
}
