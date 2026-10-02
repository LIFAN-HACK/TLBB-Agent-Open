using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TlbbAgent.Contracts;
using TlbbAgent.Core;
using TlbbAgent.Core.Automation;
using TlbbAgent.Core.Perception;

namespace TlbbAgent.CombatHarness;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var scenarios = new[]
        {
            Scenario(
                "weak-target-attacks",
                new[]
                {
                    NoTarget(),
                    WeakTarget(100),
                    WeakTarget(99),
                    WeakTarget(97),
                },
                backend => backend.Hotkeys.Contains("Q") && backend.Hotkeys.Count(h => h == "Tab") <= 2,
                "弱血条目标必须进入输出，不能只选怪"),

            Scenario(
                "weak-target-path-casts-due-skill",
                Enumerable.Repeat(WeakTarget(100), 40).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Contains("W")
                          && result.Telemetry.Any(e => e.Event == "action"
                                                       && e.SkillSent),
                "弱血条输出路径不能只普攻，已到期伤害技能也要接上",
                timeoutMs: 1200,
                configure: cfg =>
                {
                    cfg.SkillCooldownRecognitionEnabled = false;
                    cfg.Skills.Add(new SkillPreset
                    {
                        Name = "FallbackDamage",
                        Hotkey = "W",
                        IntervalMs = 100,
                        Enabled = true,
                        Kind = SkillKind.Damage,
                        Condition = SkillCondition.Always
                    });
                }),

            Scenario(
                "custom-skills-follow-drag-priority-and-independent-cd",
                Enumerable.Repeat(WeakTarget(100), 80).ToArray(),
                result =>
                {
                    var skillEvents = result.Backend.TimedHotkeys
                        .Where(item => item.Hotkey is "E" or "W" or "R")
                        .ToArray();
                    var firstCycle = skillEvents.Take(3).Select(item => item.Hotkey).ToArray();
                    var prioritySkill = skillEvents.Where(item => item.Hotkey == "E").ToArray();
                    return firstCycle.SequenceEqual(new[] { "E", "W", "R" })
                           && prioritySkill.Length >= 2
                           && prioritySkill[1].Ticks - prioritySkill[0].Ticks >= 300;
                },
                "自设列表按拖拽优先级释放；高优先级技能冷却中要跳过，其他到期技能继续；各技能按独立 CD 再次释放",
                timeoutMs: 1800,
                configure: cfg =>
                {
                    cfg.SkillCooldownRecognitionEnabled = false;
                    cfg.AttackIntervalMs = 5000;
                    cfg.Targeting.ScanIntervalMs = 50;
                    cfg.Skills.Add(new SkillPreset
                    {
                        Name = "第二顺位",
                        Hotkey = "W",
                        IntervalMs = 1000,
                        Priority = 2,
                        Enabled = true,
                        Kind = SkillKind.Damage,
                        Condition = SkillCondition.Always
                    });
                    cfg.Skills.Add(new SkillPreset
                    {
                        Name = "第三顺位",
                        Hotkey = "R",
                        IntervalMs = 1000,
                        Priority = 3,
                        Enabled = true,
                        Kind = SkillKind.Damage,
                        Condition = SkillCondition.Always
                    });
                    cfg.Skills.Add(new SkillPreset
                    {
                        Name = "第一顺位",
                        Hotkey = "E",
                        IntervalMs = 300,
                        Priority = 1,
                        Enabled = true,
                        Kind = SkillKind.Damage,
                        Condition = SkillCondition.Always
                    });
                }),

            Scenario(
                "opening-skill-does-not-wait-for-slow-perception",
                Enumerable.Repeat(MonsterTarget("slow-vision-target", 100), 8).ToArray(),
                result =>
                {
                    var actions = result.Backend.TimedHotkeys
                        .Where(item => item.Hotkey is "Q" or "W")
                        .ToArray();
                    if (actions.Length < 2 || actions[0].Hotkey != "Q" || actions[1].Hotkey != "W")
                        return false;

                    return actions[1].Ticks - actions[0].Ticks is >= 200 and <= 700;
                },
                "完整视觉读取很慢时，首攻后技能仍应只等待公共 CD，不得等待下一次完整识别",
                timeoutMs: 2800,
                configure: cfg =>
                {
                    cfg.SkillCooldownRecognitionEnabled = false;
                    cfg.AttackIntervalMs = 5000;
                    cfg.PublicCooldownMs = 300;
                    cfg.Skills.Add(new SkillPreset
                    {
                        Name = "首攻后技能",
                        Hotkey = "W",
                        IntervalMs = 10000,
                        Priority = 1,
                        Enabled = true,
                        Kind = SkillKind.Damage,
                        Condition = SkillCondition.Always
                    });
                },
                configureBackend: backend => backend.ReadDelayMs = 1000),

            Scenario(
                "missing-ocr-fallback-acquire-then-attack",
                Enumerable.Repeat(NoTarget(), 40).ToArray(),
                backend => backend.Hotkeys.Contains("Tab") && backend.Hotkeys.Contains("Q"),
                "OCR 完全未确认目标时，普通打怪仍要执行基础选怪-普攻链路",
                timeoutMs: 1200),

            Scenario(
                "missing-ocr-still-runs-always-skills",
                Enumerable.Repeat(NoTarget(), 40).ToArray(),
                backend => backend.Hotkeys.Contains("Tab") && backend.Hotkeys.Contains("Q") && backend.Hotkeys.Contains("W"),
                "OCR 完全未确认目标时，始终类技能仍按基础按键循环释放",
                timeoutMs: 1200,
                configure: cfg =>
                {
                    cfg.SkillCooldownRecognitionEnabled = false;
                    cfg.Skills.Add(new SkillPreset
                    {
                        Name = "HarnessAlwaysDamage",
                        Hotkey = "W",
                        IntervalMs = 100,
                        Enabled = true,
                        Kind = SkillKind.Damage,
                        Condition = SkillCondition.Always
                    });
                }),

            Scenario(
                "confirmed-no-nearby-monsters-suppresses-blind-input",
                Enumerable.Repeat(NoTarget() with { NearbyMonsterState = VisionTriState.Absent }, 40).ToArray(),
                result => result.Backend.Hotkeys.Count == 0
                          && result.Logs.Any(entry => entry.Message.Contains("已暂停选怪轮询", StringComparison.Ordinal)),
                "小地图时序确认无怪时不得退化为盲选怪/普攻",
                timeoutMs: 1200),

            Scenario(
                "dead-target-residual-name-no-repeat-acquire-when-minimap-empty",
                new[]
                {
                    MonsterTarget("石老虎", 100),
                    MonsterTarget("石老虎", 0),
                }.Concat(Enumerable.Repeat(
                    MonsterTarget("石老虎", 0) with { NearbyMonsterState = VisionTriState.Absent },
                    40)).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Count(hotkey => hotkey == "Tab") == 1
                          && result.Logs.Any(entry => entry.Message.Contains("已暂停选怪轮询", StringComparison.Ordinal)),
                "空血死亡后只允许立即换怪一次；周围无怪时残留姓名框不得触发持续选怪",
                timeoutMs: 1800),

            Scenario(
                "attackable-structure-target-is-strong-signal",
                new[]
                {
                    StructuralAttackableTarget(100),
                    StructuralAttackableTarget(98),
                },
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Telemetry.Any(e => e.Event == "decision"),
                "显式可攻击信号应输出攻击"),

            Scenario(
                "specific-target-does-not-blind-attack",
                Enumerable.Repeat(NoTarget(), 40).ToArray(),
                backend => backend.Hotkeys.Contains("Tab") && !backend.Hotkeys.Contains("Q"),
                "指定目标策略需要强识别，不能在 OCR 未确认时盲打",
                timeoutMs: 1200,
                configure: cfg =>
                {
                    cfg.Targeting.Filter = TargetFilter.SpecificName;
                    cfg.Targeting.SpecificName = "江湖宵小";
                }),

            Scenario(
                "public-cooldown-does-not-block-attack",
                new[]
                {
                    WeakTarget(100),
                    WeakTarget(100),
                },
                backend => backend.Hotkeys.Contains("Q"),
                "普攻不能被技能/药品公共 CD 饿死",
                consumeGlobalCooldown: false),

            Scenario(
                "failed-recovery-never-blocks-death-switch-and-next-opening-attack",
                Enumerable.Repeat(MonsterTarget("training target", 100) with
                    {
                        PlayerHpText = "60",
                        PlayerHpPercent = 60,
                        PlayerMpText = "100",
                        PlayerMpPercent = 100
                    }, 8)
                    .Concat(new[]
                    {
                        DeadTarget() with
                        {
                            PlayerHpText = "60",
                            PlayerHpPercent = 60,
                            PlayerMpText = "100",
                            PlayerMpPercent = 100
                        },
                        DeadTarget() with
                        {
                            PlayerHpText = "60",
                            PlayerHpPercent = 60,
                            PlayerMpText = "100",
                            PlayerMpPercent = 100
                        }
                    })
                    .Concat(Enumerable.Repeat(MonsterTarget("next target", 100) with
                    {
                        PlayerHpText = "60",
                        PlayerHpPercent = 60,
                        PlayerMpText = "100",
                        PlayerMpPercent = 100
                    }, 16))
                    .ToArray(),
                result =>
                {
                    var hotkeys = result.Backend.Hotkeys.ToList();
                    var recovery = hotkeys.IndexOf("F8");
                    var switched = hotkeys.IndexOf("Tab");
                    var nextAttack = switched < 0
                        ? -1
                        : hotkeys.FindIndex(switched + 1, hotkey => hotkey == "Q");
                    return recovery >= 0 && switched > recovery && nextAttack > switched;
                },
                "恢复无效或没有药时，死亡切怪和新目标首击仍必须优先执行",
                timeoutMs: 2500,
                configure: cfg =>
                {
                    cfg.HealthRecoveryMode = PlayerHealthRecoveryMode.Medicine;
                    cfg.ManaRecoveryMode = PlayerManaRecoveryMode.Medicine;
                    cfg.HealHotkey = "F8";
                    cfg.ManaHotkey = "";
                    cfg.HpThresholdPct = 70;
                    cfg.AttackIntervalMs = 5000;
                    cfg.Targeting.AcquireIntervalMs = 100;
                    cfg.Targeting.ScanIntervalMs = 50;
                }),

            Scenario(
                "continuous-combat-runs-player-and-pet-recovery-after-opening-attack",
                Enumerable.Repeat(MonsterTargetWithPetHp("training target", 100, 45) with
                {
                    PlayerHpText = "60",
                    PlayerHpPercent = 60,
                    PlayerMpText = "100",
                    PlayerMpPercent = 100
                }, 40).ToArray(),
                result =>
                {
                    var hotkeys = result.Backend.Hotkeys.ToList();
                    var openingAttack = hotkeys.IndexOf("Q");
                    var playerRecovery = hotkeys.IndexOf("F8");
                    var petRecovery = hotkeys.IndexOf("F10");
                    return openingAttack >= 0
                           && petRecovery > openingAttack
                           && playerRecovery > petRecovery;
                },
                "持续交战时先完成新目标首击，随后人物恢复与宠物恢复均获得独立调度时间片",
                timeoutMs: 2200,
                configure: cfg =>
                {
                    cfg.HealthRecoveryMode = PlayerHealthRecoveryMode.Medicine;
                    cfg.ManaRecoveryMode = PlayerManaRecoveryMode.Medicine;
                    cfg.HealHotkey = "F8";
                    cfg.ManaHotkey = "";
                    cfg.HpThresholdPct = 70;
                    cfg.PetFoodHotkey = "F10";
                    cfg.PetFoodThresholdPct = 70;
                    cfg.PetFoodCooldownMs = 150;
                    cfg.AttackIntervalMs = 5000;
                    cfg.Targeting.ScanIntervalMs = 50;
                }),

            Scenario(
                "failed-attack-send-does-not-arm-attack-interval",
                Enumerable.Repeat(WeakTarget(100), 60).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Logs.Any(e => e.Message.Contains("普攻热键发送失败", StringComparison.Ordinal)),
                "第一次普攻输入失败不能进入已攻击/间隔状态，下一轮必须继续尝试普攻",
                timeoutMs: 1500,
                configure: cfg => cfg.AttackIntervalMs = 5000,
                configureBackend: backend => backend.FailNextHotkey("Q")),

            Scenario(
                "repeated-planned-attack-without-action-enters-diagnostics",
                Enumerable.Repeat(WeakTarget(100), 80).ToArray(),
                result => !result.Backend.Hotkeys.Contains("Q")
                          && result.Logs.Any(e => e.Message.Contains("普攻热键发送失败", StringComparison.Ordinal))
                          && (result.States.Contains(nameof(NormalHangState.DiagnoseNoDamage))
                              || result.Logs.Any(e => e.Message.Contains("没有成功", StringComparison.Ordinal)
                                                      && e.Message.Contains("普攻", StringComparison.Ordinal))),
                "连续计划攻击但普攻/技能都未成功时必须进入无动作诊断",
                timeoutMs: 3000,
                configureBackend: backend => backend.FailNextHotkey("Q", 200)),

            Scenario(
                "normal-wait-after-first-attack-does-not-enter-no-action-diagnostics",
                Enumerable.Repeat(MonsterTarget("石老虎", 100), 80).ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "Q") >= 1
                          && !result.States.Contains(nameof(NormalHangState.DiagnoseNoDamage))
                          && !result.Logs.Any(e => e.Message.Contains("计划攻击但没有成功动作", StringComparison.Ordinal)),
                "普通打怪首发普攻后等待技能/目标变化是正常等待，不能误报无动作诊断",
                timeoutMs: 1200,
                configure: cfg => cfg.AttackIntervalMs = 5000),

            Scenario(
                "player-target-protected",
                new[]
                {
                    PlayerTarget(100),
                    PlayerTarget(100),
                },
                backend => !backend.Hotkeys.Contains("Q") && backend.Hotkeys.Contains("Tab"),
                "玩家目标必须跳过攻击并重新选怪"),

            Scenario(
                "ambiguous-player-sample-is-protected",
                new[]
                {
                    AmbiguousTarget(100),
                    AmbiguousTarget(99),
                },
                backend => !backend.Hotkeys.Contains("Q") && backend.Hotkeys.Contains("Tab"),
                "疑似玩家目标默认保护，不能为了基础打怪兜底而攻击"),

            Scenario(
                "structural-player-like-target-is-protected",
                new[]
                {
                    StructuralAmbiguousTarget(100),
                    StructuralAmbiguousTarget(98),
                },
                backend => !backend.Hotkeys.Contains("Q") && backend.Hotkeys.Contains("Tab"),
                "只有结构像玩家、没有非玩家样本确认时必须默认保护，避免误伤玩家"),

            Scenario(
                "decision-log-explains-weak-target-attack",
                new[]
                {
                    WeakTarget(100),
                    WeakTarget(98),
                },
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Logs.Any(e => e.Message.Contains("决策：", StringComparison.Ordinal)),
                "无怪名弱血条必须输出可读决策日志并出手"),

            Scenario(
                "telemetry-records-decision-and-action",
                new[]
                {
                    WeakTarget(100),
                    WeakTarget(98),
                },
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Telemetry.Any(e => e.Event == "decision")
                          && result.Telemetry.Any(e => e.Event == "action" && e.AttackResult == "已发送"),
                "打怪循环必须留下结构化决策与动作遥测"),

            Scenario(
                "decision-log-explains-player-protection",
                new[]
                {
                    PlayerTarget(100),
                    PlayerTarget(100),
                },
                result => !result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Contains("Tab")
                          && result.Logs.Any(e => e.Message.Contains("决策：保护目标", StringComparison.Ordinal)),
                "玩家保护必须输出可读决策日志"),

            Scenario(
                "dead-target-reacquires",
                new[]
                {
                    WeakTarget(25),
                    DeadTarget(),
                    NoTarget(),
                },
                backend => backend.Hotkeys.Contains("Q") && backend.Hotkeys.Contains("Tab"),
                "目标血量归零后必须换怪"),

            Scenario(
                "dead-frame-never-attacks-stale-corpse",
                new[]
                {
                    WeakTarget(25),
                    DeadTarget(),
                    DeadTarget(),
                    DeadTarget(),
                },
                result =>
                {
                    var hotkeys = result.Backend.Hotkeys.ToList();
                    var firstSwitch = hotkeys.IndexOf("Tab");
                    return firstSwitch >= 0
                           && !hotkeys.Skip(firstSwitch + 1).Contains("Q");
                },
                "空血帧必须立即切怪，但在新目标正血帧到来前不得攻击旧尸体",
                timeoutMs: 1200,
                configure: cfg =>
                {
                    cfg.Targeting.AcquireIntervalMs = 100;
                    cfg.Targeting.ScanIntervalMs = 50;
                }),

            Scenario(
                "weak-dead-target-uses-dead-switch-branch",
                new[]
                {
                    MonsterTarget("石老虎", 25),
                    DeadTarget(),
                    NoTarget(),
                },
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Contains("Tab")
                          && result.Logs.Any(e => e.Message.Contains("决策：死亡换怪", StringComparison.Ordinal)
                                                  || e.Message.Contains("目标血量为 0，进入换怪", StringComparison.Ordinal)),
                "只有弱血条的目标死亡时也必须进入打死换怪分支，不能被识别不足分支吞掉"),

            Scenario(
                "new-target-first-attack-bypasses-attack-interval",
                new[]
                {
                    WeakTarget(25),
                    DeadTarget(),
                    DeadTarget(),
                    WeakTarget(100),
                    WeakTarget(99),
                },
                result =>
                {
                    var timed = result.Backend.TimedHotkeys.ToList();
                    var switchIndex = timed.FindIndex(item => item.Hotkey == "Tab");
                    var nextAttackIndex = switchIndex < 0
                        ? -1
                        : timed.FindIndex(switchIndex + 1, item => item.Hotkey == "Q");
                    return result.Backend.Hotkeys.Count(h => h == "Q") >= 2
                           && switchIndex >= 0
                           && nextAttackIndex > switchIndex
                           && timed[nextAttackIndex].Ticks - timed[switchIndex].Ticks <= 100;
                },
                "打死换怪后的新目标第一发普攻不能被旧间隔卡住，选怪到普攻必须不超过 100ms",
                timeoutMs: 2000,
                configure: cfg =>
                {
                    cfg.AttackIntervalMs = 5000;
                    cfg.Targeting.TargetLostDelayMs = 100;
                    cfg.Targeting.AcquireIntervalMs = 100;
                    cfg.Targeting.ScanIntervalMs = 50;
                }),

            Scenario(
                "new-target-normal-attack-precedes-skill",
                new[]
                {
                    WeakTarget(25),
                    DeadTarget(),
                    DeadTarget(),
                    WeakTarget(100),
                    WeakTarget(99),
                },
                result =>
                {
                    var hotkeys = result.Backend.Hotkeys.ToList();
                    var qIndexes = hotkeys
                        .Select((key, index) => new { key, index })
                        .Where(item => item.key == "Q")
                        .Select(item => item.index)
                        .ToArray();
                    if (qIndexes.Length < 2)
                        return false;

                    var secondAttack = qIndexes[1];
                    var precedingSwitch = hotkeys.Take(secondAttack).ToList().LastIndexOf("Tab");
                    var nextSkill = hotkeys.FindIndex(precedingSwitch + 1, key => key == "W");
                    return precedingSwitch >= 0 && (nextSkill < 0 || secondAttack < nextSkill);
                },
                "切到新目标后必须先发送普通攻击，再允许伤害技能",
                timeoutMs: 2200,
                configure: cfg =>
                {
                    cfg.AttackIntervalMs = 5000;
                    cfg.Targeting.AcquireIntervalMs = 100;
                    cfg.Targeting.ScanIntervalMs = 50;
                    cfg.Skills.Add(new SkillPreset
                    {
                        Name = "HarnessDamage",
                        Hotkey = "W",
                        IntervalMs = 100,
                        Enabled = true,
                        Kind = SkillKind.Damage,
                        Condition = SkillCondition.TargetExists
                    });
                }),

            Scenario(
                "awaiting-target-does-not-thrash-before-visual-confirmation",
                new[]
                {
                    WeakTarget(25),
                    DeadTarget(),
                }
                .Concat(Enumerable.Repeat(NoTarget(), 10))
                .Concat(new[] { WeakTarget(100), WeakTarget(99) })
                .ToArray(),
                result =>
                {
                    var hotkeys = result.Backend.Hotkeys.ToList();
                    var firstSwitch = hotkeys.IndexOf("Tab");
                    var secondAttack = hotkeys
                        .Select((key, index) => new { key, index })
                        .Where(item => item.key == "Q")
                        .Skip(1)
                        .Select(item => item.index)
                        .FirstOrDefault(-1);
                    return firstSwitch >= 0
                           && secondAttack > firstSwitch
                           && hotkeys.Skip(firstSwitch).Take(secondAttack - firstSwitch).Count(key => key == "Tab") == 1;
                },
                "死亡后只选怪一次并等待视觉确认，500ms 推理延迟期间不得连续切走新目标",
                timeoutMs: 2200,
                configure: cfg =>
                {
                    cfg.AttackIntervalMs = 5000;
                    cfg.Targeting.AcquireIntervalMs = 100;
                    cfg.Targeting.ScanIntervalMs = 50;
                }),

            Scenario(
                "specific-name-filter-keeps-fast-death-lifecycle",
                new[]
                {
                    MonsterTarget("石老虎", 25),
                    DeadTarget(),
                    DeadTarget(),
                    MonsterTarget("石老虎", 100),
                    MonsterTarget("石老虎", 99),
                },
                result => result.Backend.Hotkeys.Contains("Tab")
                          && result.Backend.Hotkeys.Count(h => h == "Q") >= 2,
                "启用目标名称过滤后仍必须走空血秒切链路，不能退回等待框体消失的旧循环",
                timeoutMs: 1800,
                configure: cfg =>
                {
                    cfg.Targeting.Filter = TargetFilter.SpecificName;
                    cfg.Targeting.SpecificName = "石老虎";
                    cfg.AttackIntervalMs = 5000;
                    cfg.Targeting.AcquireIntervalMs = 100;
                    cfg.Targeting.ScanIntervalMs = 50;
                }),

            Scenario(
                "no-damage-target-keeps-attacking-within-timeout",
                Enumerable.Repeat(MonsterTarget("石老虎", 100), 80).ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "Q") >= 1
                          && result.Backend.Hotkeys.Count(h => h == "Tab") <= 2
                          && !result.Telemetry.Any(e => e.Event == "decision" && e.Decision == "无伤害换怪"),
                "强识别目标血量短时不掉时保留目标持续输出，不能马上切怪",
                timeoutMs: 1200,
                configure: cfg => cfg.AttackIntervalMs = 5000),

            Scenario(
                "kill-mode-stalled-target-reacquires",
                Enumerable.Repeat(MonsterTarget("障碍物后的怪物", 100), 180).ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "Q") >= 3
                          && result.Backend.Hotkeys.Contains("Tab"),
                "打死换怪模式连续攻击但超过阈值不掉血时必须脱困换目标",
                timeoutMs: 3800,
                configure: cfg =>
                {
                    cfg.Targeting.NoDamageReacquireMs = 3000;
                    cfg.Targeting.ScanIntervalMs = 50;
                    cfg.Targeting.AcquireIntervalMs = 100;
                    cfg.AttackIntervalMs = 100;
                }),

            Scenario(
                "kill-mode-locks-target-until-dead",
                Enumerable.Repeat(WeakTarget(100), 120).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Count(h => h == "Tab") <= 3
                          && (result.Logs.Any(e => e.Message.Contains("锁定", StringComparison.Ordinal))
                              || result.Telemetry.Any(e => e.Event == "decision" && e.Decision == "弱目标续打")),
                "打死换怪：选怪后应锁定输出，不能每轮都 Ctrl+Tab 切目标",
                timeoutMs: 3500,
                configure: cfg =>
                {
                    cfg.Targeting.AcquireIntervalMs = 200;
                    cfg.AttackIntervalMs = 200;
                }),

            Scenario(
                "predictive-death-switches-before-zero",
                new[]
                {
                    MonsterTarget("石老虎", 100),
                    MonsterTarget("石老虎", 72),
                    MonsterTarget("石老虎", 44),
                    MonsterTarget("石老虎", 16),
                    MonsterTarget("石老虎", 8),
                }.Concat(Enumerable.Repeat(MonsterTarget("石老虎", 8), 40)).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Contains("Tab")
                          && result.Logs.Any(e => e.Message.Contains("预测换怪", StringComparison.Ordinal))
                          && result.Telemetry.Any(e => e.Decision == "死亡换怪"),
                "预测换怪：不等 0% 空血，最近掉血足够击杀时应在最后一轮输出后提前切目标",
                timeoutMs: 2500,
                configure: cfg =>
                {
                    cfg.Targeting.DeathSwitchStrategy = DeathSwitchStrategy.Predictive;
                    cfg.AttackIntervalMs = 100;
                    cfg.Targeting.ScanIntervalMs = 80;
                    cfg.Targeting.AcquireIntervalMs = 100;
                }),

            Scenario(
                "low-positive-hp-remains-locked",
                Enumerable.Repeat(MonsterTarget("厚血首领", 4), 60).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && !result.Backend.Hotkeys.Contains("Tab")
                          && !result.Telemetry.Any(e => e.Decision == "死亡换怪"),
                "目标仍有 1–5% 正血时必须继续输出，不能把厚血怪残血误判为死亡",
                timeoutMs: 1200,
                configure: cfg =>
                {
                    cfg.Targeting.DeathSwitchStrategy = DeathSwitchStrategy.Standard;
                    cfg.AttackIntervalMs = 100;
                }),

            Scenario(
                "predictive-mode-does-not-pre-switch-boss",
                new[]
                {
                    BossTarget("世界首领", 100),
                    BossTarget("世界首领", 70),
                    BossTarget("世界首领", 40),
                    BossTarget("世界首领", 12),
                    BossTarget("世界首领", 4),
                }.Concat(Enumerable.Repeat(BossTarget("世界首领", 4), 40)).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && !result.Backend.Hotkeys.Contains("Tab")
                          && !result.Logs.Any(e => e.Message.Contains("预测换怪", StringComparison.Ordinal)),
                "模型已识别为 BOSS/精英时禁用预测提前切换，必须等确认空血",
                timeoutMs: 1800,
                configure: cfg =>
                {
                    cfg.Targeting.DeathSwitchStrategy = DeathSwitchStrategy.Predictive;
                    cfg.AttackIntervalMs = 100;
                }),

            Scenario(
                "blacklist-target-switches-without-attack",
                Enumerable.Repeat(MonsterTarget("狼族刺棘使", 100), 30).ToArray(),
                result => result.Backend.Hotkeys.Contains("Tab")
                          && !result.Backend.Hotkeys.Contains("Q")
                          && result.Logs.Any(e => e.Message.Contains("命中黑名单", StringComparison.Ordinal)),
                "L2 名称命中黑名单时必须直接换目标，不能发送普攻",
                timeoutMs: 1000,
                configure: cfg => cfg.Targeting.TargetNameBlacklist = "狼族刺棘使"),

            Scenario(
                "unified-policy-delayed-deny-name-never-attacks",
                new[]
                {
                    StructuralAttackableTarget(100),
                    StructuralAttackableTarget(100),
                    MonsterTarget("碧磷僵尸", 100),
                    MonsterTarget("碧磷僵尸", 100)
                }.Concat(Enumerable.Repeat(MonsterTarget("碧磷僵尸", 100), 20)).ToArray(),
                result => result.Backend.Hotkeys.Contains("Tab")
                          && !result.Backend.Hotkeys.Contains("Q"),
                "统一名单启用时，L2 名称晚于血条到达也不得先发普攻",
                timeoutMs: 1200,
                configure: ConfigureUnifiedDenyPolicy),

            Scenario(
                "unified-policy-deny-normalizes-ocr-variant",
                Enumerable.Repeat(MonsterTarget("碧 磷僵戶", 100), 24).ToArray(),
                result => result.Backend.Hotkeys.Contains("Tab")
                          && !result.Backend.Hotkeys.Contains("Q"),
                "禁止项应忽略空格并保守吸收一个 OCR 错字",
                timeoutMs: 1200,
                configure: ConfigureUnifiedDenyPolicy),

            Scenario(
                "unified-policy-safe-name-still-attacks",
                Enumerable.Repeat(MonsterTarget("狼族刺棘使", 100), 30).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q"),
                "非禁止目标在两帧名称确认后仍应正常攻击",
                timeoutMs: 1200,
                configure: ConfigureUnifiedDenyPolicy),

            Scenario(
                "named-target-then-weak-bar-keeps-attacking",
                new[] { MonsterTarget("石老虎", 100) }
                    .Concat(Enumerable.Repeat(WeakTarget(100), 90))
                    .ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "Q") >= 1
                          && result.Backend.Hotkeys.Count(h => h == "Tab") <= 2,
                "先识别到名字、后续只剩血条时保持目标生命周期并持续普攻",
                timeoutMs: 1500,
                configure: cfg => cfg.AttackIntervalMs = 5000),

            Scenario(
                "transient-target-loss-does-not-reacquire",
                new[] { MonsterTarget("石老虎", 100), WeakTarget(100) }
                    .Concat(Enumerable.Repeat(NoTarget(), 2))
                    .Concat(Enumerable.Repeat(WeakTarget(95), 40))
                    .ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "Q") >= 1
                          && result.Backend.Hotkeys.Count(h => h == "Tab") <= 2,
                "目标短暂丢失应等待二次确认，不能一帧 OCR 失败就重新选怪",
                timeoutMs: 1500,
                configure: cfg =>
                {
                    cfg.Targeting.TargetLostDelayMs = 2000;
                    cfg.AttackIntervalMs = 5000;
                }),

            Scenario(
                "kill-mode-target-panel-lost-reacquires",
                new[] { MonsterTarget("石老虎", 100), WeakTarget(100) }
                    .Concat(Enumerable.Repeat(NoTarget(), 50))
                    .ToArray(),
                backend => backend.Hotkeys.Contains("Q") && backend.Hotkeys.Contains("Tab"),
                "曾确认怪名后目标面板持续丢失应换怪重选（不能永久干等假死亡）",
                timeoutMs: 2500,
                configure: cfg => cfg.Targeting.TargetLostDelayMs = 400),

            Scenario(
                "kill-mode-ghost-weak-engages-then-sticks",
                Enumerable.Repeat(WeakTarget(1), 180).ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Count(h => h == "Tab") <= 3
                          && result.Backend.Hotkeys.Count(h => h == "Q") >= 5,
                "打死换怪：无怪名弱血条选怪交战后应锁定普攻，不能因 OCR 假血反复重选",
                timeoutMs: 4000,
                configure: cfg =>
                {
                    cfg.Targeting.NoDamageReacquireMs = 3000;
                    cfg.AttackIntervalMs = 150;
                    cfg.Targeting.AcquireIntervalMs = 300;
                }),

            Scenario(
                "ghost-hp-noise-sticks-after-engage",
                // OCR noise oscillates like field logs; after engage, must not spam 选怪.
                Enumerable.Range(0, 120)
                    .Select(i => WeakTarget(new[] { 7, 17, 8, 22, 20, 6, 16 }[i % 7]))
                    .ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Count(h => h == "Tab") <= 3
                          && result.Backend.Hotkeys.Count(h => h == "Q") >= 5,
                "噪声弱血条交战后应锁定输出，不能每轮选怪切目标",
                timeoutMs: 4000,
                configure: cfg =>
                {
                    cfg.Targeting.AcquireIntervalMs = 400;
                    cfg.Targeting.ScanIntervalMs = 100;
                    cfg.AttackIntervalMs = 150;
                }),

            Scenario(
                "kill-mode-named-target-sticks-briefly-without-spam-select",
                new[] { MonsterTarget("石老虎", 100) }
                    .Concat(Enumerable.Repeat(WeakTarget(100), 80))
                    .ToArray(),
                result => result.Backend.Hotkeys.Contains("Q")
                          && result.Backend.Hotkeys.Count(h => h == "Tab") <= 2,
                "打死换怪交战后短时间内不应狂按选怪",
                timeoutMs: 2500,
                configure: cfg =>
                {
                    cfg.AttackIntervalMs = 400;
                    cfg.Targeting.AcquireIntervalMs = 400;
                }),

            Scenario(
                "grab-mode-postmessage-keeps-switching-without-no-damage-wait",
                Enumerable.Repeat(WeakTarget(100), 100).ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "Q") >= 2
                          && result.Backend.Hotkeys.Count(h => h == "Tab") >= result.Backend.Hotkeys.Count(h => h == "Q"),
                "抢怪模式后台输入不等待无伤害诊断，出手后继续切目标",
                timeoutMs: 4200,
                configure: cfg =>
                {
                    cfg.Targeting.SwitchMode = TargetSwitchMode.Grab;
                    cfg.InputDispatchMode = InputDispatchMode.PostMessage;
                    cfg.Targeting.NoDamageReacquireMs = 3000;
                }),

            Scenario(
                "grab-mode-sendinput-keeps-switching-without-no-damage-wait",
                Enumerable.Repeat(WeakTarget(100), 100).ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "Q") >= 2
                          && result.Backend.Hotkeys.Count(h => h == "Tab") >= result.Backend.Hotkeys.Count(h => h == "Q"),
                "抢怪模式前台输入不等待无伤害诊断，出手后继续切目标",
                timeoutMs: 4200,
                configure: cfg =>
                {
                    cfg.Targeting.SwitchMode = TargetSwitchMode.Grab;
                    cfg.InputDispatchMode = InputDispatchMode.SendInput;
                    cfg.Targeting.NoDamageReacquireMs = 3000;
                }),

            Scenario(
                "combat-skill-fires-after-target",
                new[]
                {
                    WeakTarget(100),
                    WeakTarget(99),
                    WeakTarget(96),
                },
                backend => backend.Hotkeys.Contains("Q") && backend.Hotkeys.Contains("W"),
                "确认目标后必须释放已启用伤害技能",
                configure: cfg =>
                {
                    cfg.SkillCooldownRecognitionEnabled = false;
                    cfg.Skills.Add(new SkillPreset
                    {
                        Name = "HarnessDamage",
                        Hotkey = "W",
                        IntervalMs = 100,
                        Enabled = true,
                        Kind = SkillKind.Damage,
                        Condition = SkillCondition.TargetExists
                    });
                }),

            Scenario(
                "player-target-blocks-skills",
                new[]
                {
                    PlayerTarget(100),
                    PlayerTarget(100),
                },
                backend => !backend.Hotkeys.Contains("Q") && !backend.Hotkeys.Contains("W") && backend.Hotkeys.Contains("Tab"),
                "玩家目标不能触发普攻或伤害技能",
                configure: cfg => cfg.Skills.Add(new SkillPreset
                {
                    Name = "HarnessDamage",
                    Hotkey = "W",
                    IntervalMs = 100,
                    Enabled = true,
                    Kind = SkillKind.Damage,
                    Condition = SkillCondition.TargetExists
                })),

            Scenario(
                "grab-mode-attacks-after-switch",
                new[]
                {
                    NoTarget(),
                    WeakTarget(100),
                    WeakTarget(100),
                },
                result => result.Backend.Hotkeys.Contains("Tab")
                          && result.Backend.Hotkeys.Contains("Q")
                          && HasGrabAttackThenSwitch(result.Backend.Hotkeys),
                "抢怪模式必须切目标、出手、再切下一个目标",
                configure: cfg => cfg.Targeting.SwitchMode = TargetSwitchMode.Grab),

            Scenario(
                "grab-mode-switches-after-each-attack",
                Enumerable.Repeat(WeakTarget(100), 80).ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "Q") >= 2
                          && result.Backend.Hotkeys.Count(h => h == "Tab") >= result.Backend.Hotkeys.Count(h => h == "Q"),
                "抢怪模式每次出手后必须继续切目标，不等待目标死亡",
                timeoutMs: 1200,
                configure: cfg => cfg.Targeting.SwitchMode = TargetSwitchMode.Grab),

            Scenario(
                "pet-food-pulses-until-full",
                Enumerable.Repeat(MonsterTargetWithPetHp("training target", 100, 35), 12)
                    .Concat(Enumerable.Repeat(MonsterTargetWithPetHp("training target", 100, 100), 30))
                    .ToArray(),
                result => result.Backend.Hotkeys.Count(h => h == "F10") >= 2
                          && result.Logs.Any(e => e.Message.Contains("100", StringComparison.Ordinal)),
                "Pet food must pulse repeatedly while pet HP is low and stop after the visual HP reaches full.",
                timeoutMs: 1800,
                configure: cfg =>
                {
                    cfg.PetFoodHotkey = "F10";
                    cfg.PetFoodThresholdPct = 50;
                    cfg.PetFoodCooldownMs = 150;
                    cfg.HealthRecoveryMode = PlayerHealthRecoveryMode.Medicine;
                    cfg.ManaRecoveryMode = PlayerManaRecoveryMode.Medicine;
                    cfg.HealHotkey = "";
                    cfg.ManaHotkey = "";
                }),
        };

        var failed = RunPlannerAssertions();
        failed += RunHealthAnalyzerAssertions();
        failed += RunEnvironmentGateServiceAssertions();
        failed += RunTargetPolicyAssertions();
        failed += RunTargetIdentityAssertions();
        failed += RunGlobalConfigMigrationAssertions();
        failed += await RunAutoBackendRefreshAssertion().ConfigureAwait(false);
        failed += await RunEnvironmentGateAssertion().ConfigureAwait(false);
        failed += await RunConfiguredAcquireHotkeyPriorityAssertion().ConfigureAwait(false);
        failed += await RunConfiguredAttackHotkeyPriorityAssertion().ConfigureAwait(false);
        failed += await RunVisualSkillInventoryAssertion().ConfigureAwait(false);
        failed += await RunSystemFeedbackAsyncPipelineAssertion().ConfigureAwait(false);
        failed += await RunVisualSkillPostActionAssertion().ConfigureAwait(false);
        failed += await RunCustomSkillPostActionRetryAssertion().ConfigureAwait(false);
        failed += await RunPetRestoreVisualFallbackAssertions().ConfigureAwait(false);
        foreach (var scenario in scenarios)
        {
            var result = await RunScenario(scenario).ConfigureAwait(false);
            if (!result)
                failed++;
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "Combat harness: PASS"
            : $"Combat harness: FAIL ({failed})");

        return failed == 0 ? 0 : 1;
    }

    private static int RunTargetPolicyAssertions()
    {
        var failed = 0;
        var service = new TargetSafetyService();

        void Assert(string name, bool passed, string detail)
        {
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name} | {detail}");
            if (!passed)
                failed++;
        }

        var config = CreateConfig();
        config.Targeting.Policy.Enabled = true;

        var catalogPolicy = new UnifiedTargetPolicyConfig { Enabled = true };
        var catalog = new TargetPolicyCatalogService(new FakeLogService());
        var officialMonsters = catalog.Load(TargetPolicyCategoryKind.Monster);
        Assert(
            "target-policy-official-monster-catalog",
            officialMonsters.Count >= 220
            && officialMonsters.Any(item => item.Name == "蜀道白猿" && item.GroupPath == "剑阁")
            && officialMonsters.Any(item => item.Name == "开明兽" && item.GroupPath == "昆仑山")
            && officialMonsters.All(item => item.Source.StartsWith("https://tl.changyou.com/data/monster/", StringComparison.Ordinal)),
            $"entries={officialMonsters.Count}");
        var catalogAdded = catalog.MergeInto(catalogPolicy, TargetPolicyCategoryKind.Boss);
        var catalogBoss = catalogPolicy.Categories.First(item => item.Category == TargetPolicyCategoryKind.Boss);
        Assert(
            "target-policy-boss-catalog-only",
            catalogAdded >= 10
            && catalogBoss.Entries.Any(item => item.Name == "混江龙" && item.AllowAttack)
            && catalogBoss.Entries.Any(item => item.Name == "出洞蛟" && !item.AllowAttack)
            && catalogPolicy.Categories.First(item => item.Category == TargetPolicyCategoryKind.Monster).Entries.Count == 0,
            $"added={catalogAdded}, boss={catalogBoss.Entries.Count}");

        var suppressedCatalogEntry = catalog.Load(TargetPolicyCategoryKind.Boss).First();
        var suppressedCatalogPolicy = new UnifiedTargetPolicyConfig { Enabled = true };
        suppressedCatalogPolicy.SuppressedBuiltInEntryIds.Add(suppressedCatalogEntry.Id);
        catalog.MergeInto(suppressedCatalogPolicy, TargetPolicyCategoryKind.Boss);
        var suppressedCatalogBoss = suppressedCatalogPolicy.Categories
            .First(item => item.Category == TargetPolicyCategoryKind.Boss);
        Assert(
            "target-policy-deleted-catalog-entry-stays-deleted",
            suppressedCatalogBoss.Entries.All(item =>
                !string.Equals(item.Id, suppressedCatalogEntry.Id, StringComparison.OrdinalIgnoreCase)),
            $"suppressed={suppressedCatalogEntry.Id}, remaining={suppressedCatalogBoss.Entries.Count}");

        var monster = service.Evaluate(config, MonsterTarget("普通野怪", 100));
        Assert("target-policy-unlisted-monster-allowed", monster.Allowed, monster.Reason);

        var forbiddenSummon = service.Evaluate(config, BossTarget("碧磷僵尸", 100));
        Assert("target-policy-built-in-deny-wins", !forbiddenSummon.Allowed, forbiddenSummon.Reason);

        var denyPolicyConfig = CreateConfig();
        ConfigureUnifiedDenyPolicy(denyPolicyConfig);
        var exactDenied = service.Evaluate(denyPolicyConfig, BossTarget("碧磷僵尸", 100));
        var normalizedDenied = service.Evaluate(denyPolicyConfig, BossTarget("碧 磷僵尸", 100));
        var oneOcrErrorDenied = service.Evaluate(denyPolicyConfig, BossTarget("碧磷僵戶", 100));
        var unrelatedAllowed = service.Evaluate(denyPolicyConfig, MonsterTarget("狼族刺棘使", 100));
        var missingNameDenied = service.Evaluate(denyPolicyConfig, StructuralAttackableTarget(100));
        Assert("target-policy-exact-deny", !exactDenied.Allowed, exactDenied.Reason);
        Assert("target-policy-normalized-deny", !normalizedDenied.Allowed, normalizedDenied.Reason);
        Assert("target-policy-one-ocr-error-deny", !oneOcrErrorDenied.Allowed, oneOcrErrorDenied.Reason);
        Assert("target-policy-unrelated-name-allowed", unrelatedAllowed.Allowed, unrelatedAllowed.Reason);
        Assert("target-policy-name-pending-fails-closed", !missingNameDenied.Allowed, missingNameDenied.Reason);

        config.Targeting.SwitchMode = TargetSwitchMode.BossWhitelist;
        var nonBossInBossMode = service.Evaluate(config, MonsterTarget("普通野怪", 100));
        var bossInBossMode = service.Evaluate(config, BossTarget("副本首领", 100));
        Assert("target-policy-boss-mode-rejects-monster", !nonBossInBossMode.Allowed, nonBossInBossMode.Reason);
        Assert("target-policy-boss-mode-allows-boss-sample", bossInBossMode.Allowed, bossInBossMode.Reason);

        config.Targeting.SwitchMode = TargetSwitchMode.KillThenSwitch;
        var playerCategory = config.Targeting.Policy.Categories.First(item => item.Category == TargetPolicyCategoryKind.Player);
        playerCategory.Enabled = true;
        playerCategory.AllowUnlisted = false;
        playerCategory.Entries.Add(new TargetPolicyEntry { Name = "测试红名", Enabled = true, AllowAttack = true });

        var playerBase = PlayerTarget(100) with { TargetNameText = "测试红名" };
        var unavailableEvidence = service.Evaluate(config, playerBase with
        {
            TargetIsHostileRedName = true,
            TargetRecentlyDamagedPlayer = true
        });
        var redButNoDamage = service.Evaluate(config, playerBase with
        {
            TargetHostilityEvidenceAvailable = true,
            TargetIsHostileRedName = true,
            TargetAggressionEvidenceAvailable = true,
            TargetRecentlyDamagedPlayer = false
        });
        var retaliation = service.Evaluate(config, playerBase with
        {
            TargetHostilityEvidenceAvailable = true,
            TargetIsHostileRedName = true,
            TargetAggressionEvidenceAvailable = true,
            TargetRecentlyDamagedPlayer = true
        });
        var retaliationIdentity = new TargetIdentityEvidence(
            TargetIdentity.Player,
            0.98,
            PerceptionSourceKind.Fused,
            "retaliation-harness")
        {
            HostilityEvidenceAvailable = true,
            IsHostileRedName = true,
            AggressionEvidenceAvailable = true,
            RecentlyDamagedPlayer = true
        };
        var retaliationTarget = new TargetJudgementService().Judge(
            "retaliation-harness",
            1,
            new LayoutSnapshot(
                260,
                82,
                PerceptionSourceKind.OnnxLayout,
                DateTimeOffset.UtcNow,
                new[] { new LayoutDetectionItem(LayoutClass.TargetPanel, new VisionBox(0, 0, 260, 82), 0.99) }),
            new TargetHpSlotResult(
                TargetHpSlotSignal.Alive,
                true,
                new VisionRectF(20, 20, 130, 24),
                0.99f,
                true,
                new VisionRectF(20, 20, 130, 12),
                0.99f,
                1.0f,
                "harness",
                Array.Empty<string>()),
            new OpenTextResult("测试红名", 0.95, OpenTextSlot.TargetName, PerceptionSourceKind.OpenText),
            retaliationIdentity,
            config);
        var mappedRetaliation = VisionCombatResultMapper.FromSnapshot(new RuntimePerceptionSnapshot
        {
            Target = retaliationTarget
        });
        Assert("target-policy-player-unavailable-evidence-fails-closed", !unavailableEvidence.Allowed, unavailableEvidence.Reason);
        Assert("target-policy-player-needs-recent-damage", !redButNoDamage.Allowed, redButNoDamage.Reason);
        Assert("target-policy-player-retaliation-needs-both-signals", retaliation.Allowed, retaliation.Reason);
        Assert(
            "target-policy-player-evidence-propagates-from-vision",
            mappedRetaliation.TargetHostilityEvidenceAvailable
            && mappedRetaliation.TargetIsHostileRedName
            && mappedRetaliation.TargetAggressionEvidenceAvailable
            && mappedRetaliation.TargetRecentlyDamagedPlayer,
            $"hostile={mappedRetaliation.TargetIsHostileRedName}, recent={mappedRetaliation.TargetRecentlyDamagedPlayer}");

        return failed;
    }

    private static int RunTargetIdentityAssertions()
    {
        var failed = 0;

        void Assert(string name, bool passed, string detail)
        {
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name} | {detail}");
            if (!passed)
                failed++;
        }

        var config = CreateConfig();
        var match = new TargetProtectionSampleMatch(
            "player",
            "玩家模板",
            "player-sample-harness",
            "harness",
            0.96);
        var identityService = new TemplateTargetIdentityService(
            new StaticTargetProtectionSampleService(match),
            new ResolutionProfileService(new FakeProfileService(), new FakeLogService()));
        var frame = new CaptureResult(400, 120, new byte[400 * 120 * 4]);
        var layout = new LayoutSnapshot(
            frame.Width,
            frame.Height,
            PerceptionSourceKind.OnnxLayout,
            DateTimeOffset.UtcNow,
            new[]
            {
                new LayoutDetectionItem(LayoutClass.TargetPanel, new VisionBox(240, 0, 160, 82), 0.99),
                new LayoutDetectionItem(LayoutClass.TargetHpBar, new VisionBox(278, 23, 120, 5), 0.99)
            });
        var identity = identityService.Analyze(frame, layout, config);
        var target = new TargetJudgementService().Judge(
            "player-template-harness",
            1,
            layout,
            new TargetHpSlotResult(
                TargetHpSlotSignal.Alive,
                true,
                new VisionRectF(20, 20, 130, 24),
                0.99f,
                true,
                new VisionRectF(20, 20, 130, 12),
                0.99f,
                1.0f,
                "harness",
                Array.Empty<string>()),
            OpenTextResult.Empty(OpenTextSlot.TargetName),
            identity,
            config);
        var mapped = VisionCombatResultMapper.FromSnapshot(new RuntimePerceptionSnapshot { Target = target });

        Assert(
            "target-template-player-enters-production-identity",
            identity.Identity == TargetIdentity.Player
            && identity.Source == PerceptionSourceKind.Fused
            && identity.Confidence >= 0.96,
            $"identity={identity.Identity}, source={identity.Source}, confidence={identity.Confidence:0.000}");
        Assert(
            "target-template-player-is-protected-before-combat",
            target.IsProtectedTarget && !target.IsActionableTarget,
            $"protected={target.IsProtectedTarget}, actionable={target.IsActionableTarget}");
        Assert(
            "target-template-player-maps-to-nonattackable",
            mapped.TargetLooksLikePlayer && mapped.TargetIsAttackable == false,
            $"player={mapped.TargetLooksLikePlayer}, attackable={mapped.TargetIsAttackable}");

        return failed;
    }

    private static int RunGlobalConfigMigrationAssertions()
    {
        var profiles = new FakeProfileService();
        var legacy = CreateConfig();
        legacy.SchemaVersion = 24;
        legacy.AttackHotkey = "Q";
        legacy.CombatRecognition.PlayerMp = new OcrRegionConfig
        {
            X = 82,
            Y = 54,
            Width = 128,
            Height = 17
        };
        legacy.Targeting.SwitchMode = TargetSwitchMode.BossWhitelist;
        legacy.Targeting.TargetNameBlacklist = "blocked-monster";
        legacy.Targeting.TargetNameWhitelist = "allowed-boss";
        profiles.Save("schema24", JsonSerializer.Serialize(legacy));

        var service = new GlobalConfigService(profiles, new FakeLogService());
        var loaded = service.Load("schema24");
        var monster = service.Current.Targeting.Policy.Categories
            .First(item => item.Category == TargetPolicyCategoryKind.Monster);
        var boss = service.Current.Targeting.Policy.Categories
            .First(item => item.Category == TargetPolicyCategoryKind.Boss);
        var passed = loaded
                     && service.Current.SchemaVersion == 29
                     && service.Current.InputDispatchMode == InputDispatchMode.Adaptive
                     && service.Current.AttackHotkey == "Q"
                     && service.Current.CombatRecognition.PlayerMp is { X: 82, Y: 39, Width: 128, Height: 5 }
                     && service.Current.Targeting.SwitchMode == TargetSwitchMode.BossWhitelist
                     && monster.Entries.Any(item => item.Name == "blocked-monster" && !item.AllowAttack)
                     && boss.Entries.Any(item => item.Name == "allowed-boss" && item.AllowAttack)
                     && string.IsNullOrEmpty(service.Current.Targeting.TargetNameBlacklist)
                     && string.IsNullOrEmpty(service.Current.Targeting.TargetNameWhitelist);

        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} profile-schema24-to29-target-policy-player-mp-and-adaptive-input | loaded={loaded}, attack={service.Current.AttackHotkey}, mode={service.Current.Targeting.SwitchMode}, input={service.Current.InputDispatchMode}, playerMp={service.Current.CombatRecognition.PlayerMp.X},{service.Current.CombatRecognition.PlayerMp.Y},{service.Current.CombatRecognition.PlayerMp.Width},{service.Current.CombatRecognition.PlayerMp.Height}");

        var deletedBuiltIn = CreateConfig();
        var deletedBuiltInId = "boss-bilin-zombie";
        var deletedBossCategory = deletedBuiltIn.Targeting.Policy.Categories
            .First(item => item.Category == TargetPolicyCategoryKind.Boss);
        deletedBossCategory.Entries.RemoveAll(item =>
            string.Equals(item.Id, deletedBuiltInId, StringComparison.OrdinalIgnoreCase));
        deletedBuiltIn.Targeting.Policy.SuppressedBuiltInEntryIds.Add(deletedBuiltInId);
        profiles.Save("deleted-built-in-target", JsonSerializer.Serialize(deletedBuiltIn));

        var reloadService = new GlobalConfigService(profiles, new FakeLogService());
        var reloaded = reloadService.Load("deleted-built-in-target");
        var reloadedBossCategory = reloadService.Current.Targeting.Policy.Categories
            .First(item => item.Category == TargetPolicyCategoryKind.Boss);
        var deletionPersisted = reloaded
                                && reloadedBossCategory.Entries.All(item =>
                                    !string.Equals(item.Id, deletedBuiltInId, StringComparison.OrdinalIgnoreCase))
                                && reloadService.Current.Targeting.Policy.SuppressedBuiltInEntryIds.Contains(
                                    deletedBuiltInId,
                                    StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"{(deletionPersisted ? "PASS" : "FAIL")} target-policy-built-in-delete-persists | loaded={reloaded}, id={deletedBuiltInId}");

        var contaminated = CreateConfig();
        contaminated.School = School.Tianshan;
        contaminated.AiSkills.Add(new SkillPreset
        {
            Name = "溪山行旅",
            Hotkey = "E",
            ExpectedVisualSkillId = "xy_016",
            Enabled = true
        });
        contaminated.AiSkills.Add(new SkillPreset
        {
            Name = "天山技能",
            Hotkey = "W",
            ExpectedVisualSkillId = "ts_001",
            Enabled = true
        });
        contaminated.AiSkills.Add(new SkillPreset
        {
            Name = "公共技能",
            Hotkey = "F10",
            ExpectedVisualSkillId = "common_001",
            Enabled = true
        });
        contaminated.Skills.Add(new SkillPreset
        {
            Name = "旧角色无标识技能",
            Hotkey = "R",
            Enabled = true
        });
        profiles.Save("cross-school-skills", JsonSerializer.Serialize(contaminated));

        var isolationService = new GlobalConfigService(profiles, new FakeLogService());
        var isolated = isolationService.Load("cross-school-skills")
                       && isolationService.Current.School == School.Tianshan
                       && isolationService.Current.AiSkills.Count == 2
                       && isolationService.Current.AiSkills.Any(skill => skill.ExpectedVisualSkillId == "ts_001")
                       && isolationService.Current.AiSkills.Any(skill => skill.ExpectedVisualSkillId == "common_001")
                       && isolationService.Current.AiSkills.All(skill => skill.ExpectedVisualSkillId != "xy_016")
                       && isolationService.Current.Skills.Any(skill => skill.Name == "旧角色无标识技能");
        Console.WriteLine($"{(isolated ? "PASS" : "FAIL")} profile-cross-school-skills-isolated | school={isolationService.Current.School}, aiSkills={isolationService.Current.AiSkills.Count}, customSkills={isolationService.Current.Skills.Count}");

        return (passed ? 0 : 1) + (deletionPersisted ? 0 : 1) + (isolated ? 0 : 1);
    }

    private static int RunEnvironmentGateServiceAssertions()
    {
        var failed = 0;
        var window = new BoundGameWindow { Hwnd = 0x1001, Pid = 1001, ClientWidth = 1920, ClientHeight = 1080 };
        var config = CreateConfig();

        var movingRuntime = new FakeRuntimeSnapshotService(new[]
        {
            SceneSnapshot("昆仑山", 100, 100),
            SceneSnapshot("昆仑山", 103, 100)
        });
        var movingGate = new CombatEnvironmentGateService(new RuntimeSnapshotPerceptionHub(movingRuntime), new FakeLogService());
        var movingFirst = movingGate.Evaluate(window, config);
        Thread.Sleep(420);
        var movingSecond = movingGate.Evaluate(window, config);
        failed += AssertGate(
            "environment-service-moving-coordinate-gate",
            !movingFirst.ShouldSkipCombat && !movingSecond.ShouldSkipCombat,
            $"first={movingFirst.ShouldSkipCombat}, second={movingSecond.ShouldSkipCombat}, reason={movingSecond.Reason}",
            "同地图坐标变化（追击走近攻击距离）不得暂停打怪");

        var switchRuntime = new FakeRuntimeSnapshotService(new[]
        {
            SceneSnapshot("昆仑山", 100, 100),
            SceneSnapshot("苏州", 10, 10),
            SceneSnapshot("苏州", 10, 10)
        });
        var switchGate = new CombatEnvironmentGateService(new RuntimeSnapshotPerceptionHub(switchRuntime), new FakeLogService());
        var switchFirst = switchGate.Evaluate(window, config);
        Thread.Sleep(420);
        var switchSecond = switchGate.Evaluate(window, config);
        Thread.Sleep(420);
        var switchThird = switchGate.Evaluate(window, config);
        failed += AssertGate(
            "environment-service-scene-switch-gate",
            !switchFirst.ShouldSkipCombat
            && !switchSecond.ShouldSkipCombat
            && !switchThird.ShouldSkipCombat,
            $"first={switchFirst.ShouldSkipCombat}, second={switchSecond.ShouldSkipCombat}, third={switchThird.ShouldSkipCombat}, reason={switchThird.Reason}",
            "地图变化只记录，不得暂停挂机；导航未到达由独立导航状态机负责");

        var noisyRuntime = new FakeRuntimeSnapshotService(new[]
        {
            SceneSnapshot("光明殿", 90, 34),
            SceneSnapshot("光明庭", 90, 34),
            SceneSnapshot("光明殿", 91, 34)
        });
        var noisyGate = new CombatEnvironmentGateService(new RuntimeSnapshotPerceptionHub(noisyRuntime), new FakeLogService());
        var noisyFirst = noisyGate.Evaluate(window, config);
        Thread.Sleep(420);
        var noisySecond = noisyGate.Evaluate(window, config);
        Thread.Sleep(420);
        var noisyThird = noisyGate.Evaluate(window, config);
        failed += AssertGate(
            "environment-service-single-frame-map-noise",
            !noisyFirst.ShouldSkipCombat && !noisySecond.ShouldSkipCombat && !noisyThird.ShouldSkipCombat,
            $"first={noisyFirst.ShouldSkipCombat}, second={noisySecond.ShouldSkipCombat}, third={noisyThird.ShouldSkipCombat}, reason={noisyThird.Reason}",
            "单帧地图文字抖动不得暂停打怪");

        var safeRuntime = new FakeRuntimeSnapshotService(new[]
        {
            SceneSnapshot("苏州", 10, 10)
        });
        var safeGate = new CombatEnvironmentGateService(new RuntimeSnapshotPerceptionHub(safeRuntime), new FakeLogService());
        var safe = safeGate.Evaluate(window, config);
        failed += AssertGate(
            "environment-service-safe-map-gate",
            !safe.ShouldSkipCombat && safe.Reason.Contains("安全区", StringComparison.Ordinal),
            $"skip={safe.ShouldSkipCombat}, reason={safe.Reason}",
            "安全地图目前仅记录不暂停（地图 OCR 噪声），不得冻结按键打怪");

        return failed;
    }

    private static bool HasGrabAttackThenSwitch(IReadOnlyList<string> hotkeys)
    {
        var firstAttack = -1;
        for (var i = 0; i < hotkeys.Count; i++)
        {
            if (hotkeys[i] == "Q")
            {
                firstAttack = i;
                break;
            }
        }

        if (firstAttack <= 0)
            return false;

        return hotkeys.Take(firstAttack).Any(h => h == "Tab")
               && hotkeys.Skip(firstAttack + 1).Any(h => h == "Tab");
    }

    private static int AssertGate(string name, bool passed, string detail, string expectation)
    {
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name} | {expectation} | {detail}");
        return passed ? 0 : 1;
    }

    private static async Task<int> RunEnvironmentGateAssertion()
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        var configService = new FakeGlobalConfigService(config);
        var backend = new FakeCombatBackend(Enumerable.Repeat(MonsterTarget("木桩", 100), 20).ToArray(), consumeGlobalCooldown: true);
        var telemetry = new FakeCombatLoopTelemetryService();
        var controller = new NormalHangController(
            log,
            configService,
            new FakeCombatBackendSelector(backend),
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            telemetry: telemetry,
            environmentGate: new FakeEnvironmentGateService(CombatEnvironmentGateResult.Skip(
                "当前地图 苏州 属于安全区域，本轮暂停选怪、普攻和技能。",
                "苏州",
                RuntimeSnapshotQuality.Reliable,
                90)));

        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1001,
            Pid = 1001,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });

        controller.Start();
        await WaitUntilAsync(
            () => telemetry.Entries.Any(e => e.Event == "decision" && e.Decision == "环境门禁"),
            1200).ConfigureAwait(false);
        controller.Stop();

        var passed = telemetry.Entries.Any(e => e.Event == "decision" && e.Decision == "环境门禁")
                     && backend.Hotkeys.Count == 0;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} environment-gate-skips-combat-input | 可靠安全地图门禁必须暂停选怪/普攻/技能 | hotkeys={backend.Hotkeys.Count}");
        if (!passed)
        {
            foreach (var entry in log.Entries.TakeLast(12))
                Console.WriteLine($"  {entry}");
        }

        return passed ? 0 : 1;
    }

    private static async Task<int> RunVisualSkillInventoryAssertion()
    {
        var matchedWithoutConfig = await RunVisualSkillInventoryCase(identityMatched: true).ConfigureAwait(false);
        var aiWithoutPolicyDoesNotFallback = await RunVisualSkillInventoryCase(
            identityMatched: true,
            customFallbackHotkey: "F9").ConfigureAwait(false);
        var unknownWithoutConfig = await RunVisualSkillInventoryCase(identityMatched: false).ConfigureAwait(false);
        var exactConfigured = await RunVisualSkillInventoryCase(identityMatched: true, expectedSkillId: "xy_004").ConfigureAwait(false);
        var timedConfigured = await RunVisualSkillInventoryCase(
            identityMatched: true,
            expectedSkillId: "xy_004",
            visualMode: false).ConfigureAwait(false);
        var mismatchConfigured = await RunVisualSkillInventoryCase(identityMatched: true, expectedSkillId: "xy_001").ConfigureAwait(false);
        var candidateConfigured = await RunVisualSkillInventoryCase(
            identityMatched: true,
            expectedSkillId: "xy_004",
            identityOverride: VisionSkillIdentityState.Candidate,
            identitySource: "seed_index").ConfigureAwait(false);
        var rightConfigured = await RunVisualSkillInventoryCase(
            identityMatched: true,
            expectedSkillId: "xy_004",
            surface: "right_skill_bar").ConfigureAwait(false);
        var staleRight = await RunVisualSkillInventoryCase(
            identityMatched: true,
            expectedSkillId: "xy_004",
            surface: "right_skill_bar",
            staleFrame: true).ConfigureAwait(false);
        var duplicateRight = await RunVisualSkillInventoryCase(
            identityMatched: true,
            expectedSkillId: "xy_004",
            surface: "right_skill_bar",
            duplicateSkill: true).ConfigureAwait(false);
        var switchOrder = await RunVisualSkillTargetSwitchOrderCase().ConfigureAwait(false);
        var window = new BoundGameWindow
        {
            Hwnd = 0x1002,
            Pid = 1002,
            ClientWidth = 1920,
            ClientHeight = 1080
        };
        var config = CreateConfig();
        config.SkillCooldownRecognitionEnabled = true;
        var skill = new SkillPreset
        {
            Name = "弹指神功",
            Hotkey = "W",
            ExpectedVisualSkillId = "xy_004",
            Cooldown = new SkillCooldownSlotConfig { Enabled = true }
        };
        var ready = new SkillCooldownRecognitionService(new StaticSkillInventoryPerceptionHub(true, slotKey: "E"))
            .Inspect(window, skill, config);
        var unrecognized = new SkillCooldownRecognitionService(new StaticSkillInventoryPerceptionHub(false))
            .Inspect(window, skill, config);
        skill.ExpectedVisualSkillId = "";
        var missingBinding = new SkillCooldownRecognitionService(new StaticSkillInventoryPerceptionHub(true))
            .Inspect(window, skill, config);
        skill.ExpectedVisualSkillId = "xy_001";
        var mismatchedBinding = new SkillCooldownRecognitionService(new StaticSkillInventoryPerceptionHub(true))
            .Inspect(window, skill, config);
        skill.ExpectedVisualSkillId = "xy_004";
        skill.Hotkey = "F2";
        var movedSlot = new SkillCooldownRecognitionService(new StaticSkillInventoryPerceptionHub(true, slotKey: "E"))
            .Inspect(window, skill, config);
        var passed = matchedWithoutConfig.Hotkeys.Contains("Q")
                     && !matchedWithoutConfig.Hotkeys.Contains("E")
                     && !aiWithoutPolicyDoesNotFallback.Hotkeys.Contains("F9")
                     && unknownWithoutConfig.Hotkeys.Contains("Q")
                     && !unknownWithoutConfig.Hotkeys.Contains("E")
                     && exactConfigured.Hotkeys.Contains("W")
                     && !exactConfigured.Hotkeys.Contains("E")
                     && timedConfigured.Hotkeys.Contains("W")
                     && !mismatchConfigured.Hotkeys.Contains("W")
                     && !candidateConfigured.Hotkeys.Contains("W")
                     && rightConfigured.ClientClicks.Contains((1700, 300))
                     && staleRight.ClientClicks.IsEmpty
                     && duplicateRight.ClientClicks.Contains((1744, 300))
                     && duplicateRight.ClientClicks.Count == 1
                     && switchOrder
                     && ready.Readiness == SkillCooldownReadiness.Ready
                     && unrecognized.Readiness == SkillCooldownReadiness.Unknown
                     && missingBinding.Readiness == SkillCooldownReadiness.Unknown
                     && mismatchedBinding.Readiness == SkillCooldownReadiness.Unknown
                     && movedSlot.Readiness == SkillCooldownReadiness.Ready;
        Console.WriteLine(
            $"{(passed ? "PASS" : "FAIL")} visual-skill-config-bound-loop | " +
            "仅已配置 SkillId 可释放；候选/未知/旧帧均 noop，键盘动作采用角色改键或用户预设，右栏采用视觉点击点 | " +
            $"no-config={string.Join(',', matchedWithoutConfig.Hotkeys)} no-fallback={string.Join(',', aiWithoutPolicyDoesNotFallback.Hotkeys)} exact={string.Join(',', exactConfigured.Hotkeys)} timed={string.Join(',', timedConfigured.Hotkeys)} mismatch={string.Join(',', mismatchConfigured.Hotkeys)} candidate={string.Join(',', candidateConfigured.Hotkeys)} right={rightConfigured.ClientClicks.Count} stale={staleRight.ClientClicks.Count} duplicate={duplicateRight.ClientClicks.Count} switch-order={switchOrder}");
        return passed ? 0 : 1;
    }

    private static async Task<FakeCombatBackend> RunVisualSkillInventoryCase(
        bool identityMatched,
        string? expectedSkillId = null,
        VisionSkillIdentityState? identityOverride = null,
        string identitySource = "icons_ingame",
        string surface = "bottom_skill_bar",
        bool staleFrame = false,
        bool duplicateSkill = false,
        bool visualMode = true,
        string? customFallbackHotkey = null)
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        config.SkillCooldownRecognitionEnabled = visualMode;
        config.Skills.Clear();
        config.AiSkills.Clear();
        if (!string.IsNullOrWhiteSpace(customFallbackHotkey))
        {
            config.Skills.Add(new SkillPreset
            {
                Name = "自设回落技能",
                Hotkey = customFallbackHotkey,
                IntervalMs = 0,
                Kind = SkillKind.Damage,
                Condition = SkillCondition.Always,
                Enabled = true
            });
        }
        if (expectedSkillId is not null)
        {
            var skill = new SkillPreset
            {
                Name = "弹指神功",
                Hotkey = "W",
                ExpectedVisualSkillId = expectedSkillId,
                IntervalMs = 0,
                Kind = SkillKind.Damage,
                Condition = SkillCondition.Always,
                Cooldown = new SkillCooldownSlotConfig { Enabled = true }
            };
            if (visualMode)
                config.AiSkills.Add(skill);
            else
                config.Skills.Add(skill);
        }
        var configService = new FakeGlobalConfigService(config);
        var backend = new FakeCombatBackend(Enumerable.Repeat(WeakTarget(100), 50).ToArray(), consumeGlobalCooldown: true);
        var perception = new StaticSkillInventoryPerceptionHub(
            identityMatched,
            slotKey: "E",
            identityOverride: identityOverride,
            identitySource: identitySource,
            surface: surface,
            staleFrame: staleFrame,
            duplicateSkill: duplicateSkill);
        var controller = new NormalHangController(
            log,
            configService,
            new FakeCombatBackendSelector(backend),
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            skillCooldown: new SkillCooldownRecognitionService(perception),
            perceptionHub: perception);
        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1002,
            Pid = 1002,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });
        controller.Start();
        await WaitUntilAsync(
            () => backend.Hotkeys.Contains("Q"),
            1200).ConfigureAwait(false);
        await Task.Delay(350).ConfigureAwait(false);
        controller.Stop();
        return backend;
    }

    private static async Task<bool> RunVisualSkillTargetSwitchOrderCase()
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        config.SkillCooldownRecognitionEnabled = true;
        config.SkillCooldownReadyDebounceMs = 200;
        config.AttackIntervalMs = 5000;
        config.Targeting.ScanIntervalMs = 50;
        config.Targeting.AcquireIntervalMs = 50;
        config.Skills.Clear();
        config.AiSkills.Clear();
        config.AiSkills.Add(new SkillPreset
        {
            Name = "弹指神功策略",
            Hotkey = "W",
            ExpectedVisualSkillId = "xy_004",
            IntervalMs = 0,
            Kind = SkillKind.Damage,
            Condition = SkillCondition.Always,
            Cooldown = new SkillCooldownSlotConfig { Enabled = true }
        });
        var backend = new FakeCombatBackend(
            new[]
            {
                WeakTarget(25),
                WeakTarget(24),
                DeadTarget(),
                DeadTarget(),
                WeakTarget(100),
                WeakTarget(99),
                WeakTarget(98),
                WeakTarget(97)
            }.Concat(Enumerable.Repeat(WeakTarget(96), 40)).ToArray(),
            consumeGlobalCooldown: true);
        var perception = new StaticSkillInventoryPerceptionHub(true, slotKey: "E");
        var controller = new NormalHangController(
            log,
            new FakeGlobalConfigService(config),
            new FakeCombatBackendSelector(backend),
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            skillCooldown: new SkillCooldownRecognitionService(perception),
            perceptionHub: perception);
        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1003,
            Pid = 1003,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin64\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });

        controller.Start();
        await WaitUntilAsync(() => backend.Hotkeys.Count(key => key == "Q") >= 2, 2200).ConfigureAwait(false);
        await Task.Delay(450).ConfigureAwait(false);
        controller.Stop();

        var hotkeys = backend.Hotkeys.ToList();
        var lastSwitch = hotkeys.LastIndexOf("Tab");
        if (lastSwitch < 0)
            return false;
        var firstAttackAfterSwitch = hotkeys.FindIndex(lastSwitch + 1, key => key == "Q");
        var firstSkillAfterSwitch = hotkeys.FindIndex(lastSwitch + 1, key => key == "W");
        return firstAttackAfterSwitch > lastSwitch
               && (firstSkillAfterSwitch < 0 || firstAttackAfterSwitch < firstSkillAfterSwitch)
               && !hotkeys.Contains("E");
    }

    private static async Task<int> RunVisualSkillPostActionAssertion()
    {
        var cooling = await RunVisualSkillPostActionCase(
            PostActionEvidenceMode.Cooling,
            500).ConfigureAwait(false);
        var feedback = await RunVisualSkillPostActionCase(
            PostActionEvidenceMode.FeedbackCooldown,
            500).ConfigureAwait(false);
        var noEvidence = await RunVisualSkillPostActionCase(
            PostActionEvidenceMode.None,
            1550).ConfigureAwait(false);

        var coolingVerified = cooling.SkillSends == 1
                              && cooling.Logs.Any(entry =>
                                  entry.Message.Contains("Ready", StringComparison.Ordinal)
                                  && entry.Message.Contains("Cooling", StringComparison.Ordinal));
        var feedbackBackoff = feedback.SkillSends == 1
                              && feedback.AsyncRequests == 1
                              && feedback.Logs.Any(entry =>
                                  entry.Message.Contains(nameof(SystemFeedbackSemanticKind.Cooldown), StringComparison.Ordinal));
        var noEvidenceBackoff = noEvidence.SkillSends == 1
                                && noEvidence.Logs.Any(entry =>
                                    entry.Message.Contains("1.5", StringComparison.Ordinal));
        var passed = coolingVerified && feedbackBackoff && noEvidenceBackoff;
        Console.WriteLine(
            $"{(passed ? "PASS" : "FAIL")} visual-skill-post-action-evidence | " +
            "技能释放后必须观察 Ready->Cooling、资源变化或系统反馈；无证据不得盲目连发 | " +
            $"cooling={cooling.SkillSends}/verified={coolingVerified} feedback={feedback.SkillSends}/backoff={feedbackBackoff} none={noEvidence.SkillSends}/backoff={noEvidenceBackoff}");
        return passed ? 0 : 1;
    }

    private static async Task<int> RunSystemFeedbackAsyncPipelineAssertion()
    {
        var detector = new DelayedSystemFeedbackDetector(250);
        using var pipeline = new SystemFeedbackAsyncPipeline(
            detector,
            new NullOpenTextRecognizer(),
            new FakeLogService());
        var pixels = new byte[16 * 16 * 4];
        CaptureResult Frame(long sequence) => new(16, 16, pixels)
        {
            Sequence = sequence,
            CapturedAt = DateTimeOffset.UtcNow,
            CaptureSource = "combat-harness"
        };

        var stopwatch = Stopwatch.StartNew();
        var first = pipeline.Request("async-window", Frame(1));
        stopwatch.Stop();
        pipeline.Request("async-window", Frame(2));
        pipeline.Request("async-window", Frame(3));
        pipeline.Request("async-window", Frame(4));

        await WaitUntilAsync(
            () =>
            {
                var read = pipeline.Read("async-window");
                return read.Latest?.FrameSequence == 4 && !read.InFlight;
            },
            2500).ConfigureAwait(false);
        var final = pipeline.Read("async-window");
        var callsBeforeDuplicate = detector.Sequences.Count;
        pipeline.Request("async-window", Frame(4));
        await Task.Delay(100).ConfigureAwait(false);
        var callsAfterDuplicate = detector.Sequences.Count;
        var nonBlocking = stopwatch.ElapsedMilliseconds < 100;
        var latestWins = final.Latest?.FrameSequence == 4
                         && !final.InFlight
                         && detector.Sequences.Contains(4)
                         && detector.Sequences.Count <= 2;
        var duplicateSuppressed = callsAfterDuplicate == callsBeforeDuplicate;
        var passed = first.InFlight && nonBlocking && latestWins && duplicateSuppressed;
        Console.WriteLine(
            $"{(passed ? "PASS" : "FAIL")} system-feedback-async-latest-wins | " +
            "异步反馈请求不得阻塞战斗线程，积压帧只保留最新帧 | " +
            $"request={stopwatch.ElapsedMilliseconds}ms detector=250ms calls=[{string.Join(',', detector.Sequences)}] " +
            $"latest={final.Latest?.FrameSequence ?? 0} duplicate={callsBeforeDuplicate}->{callsAfterDuplicate}");
        return passed ? 0 : 1;
    }

    private static async Task<int> RunCustomSkillPostActionRetryAssertion()
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        config.SkillCooldownRecognitionEnabled = false;
        config.AttackIntervalMs = 5000;
        config.Targeting.ScanIntervalMs = 50;
        config.Skills.Clear();
        config.Skills.Add(new SkillPreset
        {
            Id = "custom-retry",
            Name = "自设失败重试",
            Hotkey = "W",
            IntervalMs = 10000,
            Priority = 1,
            Enabled = true,
            Kind = SkillKind.Damage,
            Condition = SkillCondition.Always
        });
        var backend = new FakeCombatBackend(
            Enumerable.Repeat(WeakTarget(100), 80).ToArray(),
            consumeGlobalCooldown: true);
        var perception = new ActionAwareSkillPerceptionHub(
            () => backend.Hotkeys.Contains("W"),
            PostActionEvidenceMode.FeedbackBusy);
        var controller = new NormalHangController(
            log,
            new FakeGlobalConfigService(config),
            new FakeCombatBackendSelector(backend),
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            perceptionHub: perception);
        var window = new BoundGameWindow
        {
            Hwnd = 0x1004,
            Pid = 1004,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin64\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        };
        controller.Attach(window);
        _ = perception.Read(window, config);

        controller.Start();
        await WaitUntilAsync(
            () => backend.Hotkeys.Count(key => key == "W") >= 2,
            1800).ConfigureAwait(false);
        controller.Stop();

        var skillSends = backend.Hotkeys.Count(key => key == "W");
        var sawBusyRetry = log.Entries.Any(entry =>
            entry.Message.Contains(nameof(SystemFeedbackSemanticKind.MaterialBusy), StringComparison.Ordinal)
            && entry.Message.Contains("不计入完整技能 CD", StringComparison.Ordinal));
        var passed = skillSends >= 2 && sawBusyRetry;
        Console.WriteLine(
            $"{(passed ? "PASS" : "FAIL")} custom-skill-post-action-retry | " +
            "自设技能被游戏拒绝时不得误记完整用户 CD，必须短退避后重试 | " +
            $"sends={skillSends} busyRetry={sawBusyRetry}");
        return passed ? 0 : 1;
    }

    private static async Task<VisualSkillPostActionCaseResult> RunVisualSkillPostActionCase(
        PostActionEvidenceMode mode,
        int observeMilliseconds)
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        config.SkillCooldownRecognitionEnabled = true;
        config.SkillCooldownReadyDebounceMs = 200;
        config.AttackIntervalMs = 5000;
        config.Skills.Clear();
        config.AiSkills.Clear();
        config.AiSkills.Add(new SkillPreset
        {
            Name = "post-action-skill",
            Hotkey = "W",
            ExpectedVisualSkillId = "xy_004",
            IntervalMs = 0,
            Kind = SkillKind.Damage,
            Condition = SkillCondition.Always,
            Cooldown = new SkillCooldownSlotConfig { Enabled = true }
        });
        var backend = new FakeCombatBackend(
            Enumerable.Repeat(WeakTarget(100), 120).ToArray(),
            consumeGlobalCooldown: true);
        var perception = new ActionAwareSkillPerceptionHub(
            () => backend.Hotkeys.Contains("W"),
            mode);
        var controller = new NormalHangController(
            log,
            new FakeGlobalConfigService(config),
            new FakeCombatBackendSelector(backend),
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            skillCooldown: new SkillCooldownRecognitionService(perception),
            perceptionHub: perception);
        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1010,
            Pid = 1010,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin64\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });
        controller.Start();
        await WaitUntilAsync(() => backend.Hotkeys.Contains("W"), 1500).ConfigureAwait(false);
        await Task.Delay(observeMilliseconds).ConfigureAwait(false);
        controller.Stop();
        return new VisualSkillPostActionCaseResult(
            backend.Hotkeys.Count(key => key == "W"),
            log.Entries.ToArray(),
            perception.AsyncRequests);
    }

    private static async Task<int> RunConfiguredAttackHotkeyPriorityAssertion()
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        config.AttackHotkey = "A";
        config.SkillCooldownRecognitionEnabled = false;
        config.Skills.Clear();
        var backend = new FakeCombatBackend(
            Enumerable.Repeat(WeakTarget(100), 30).ToArray(),
            consumeGlobalCooldown: true);
        var controller = new NormalHangController(
            log,
            new FakeGlobalConfigService(config),
            new FakeCombatBackendSelector(backend),
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            gameHotkeys: new StaticGameHotkeyProfileService(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GameInputSemanticIds.SkillSlot(1)] = "Q"
                }));
        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1004,
            Pid = 1004,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });

        controller.Start();
        await WaitUntilAsync(() => backend.Hotkeys.Contains("A"), 1200).ConfigureAwait(false);
        controller.Stop();

        var passed = backend.Hotkeys.Contains("A") && !backend.Hotkeys.Contains("Q");
        Console.WriteLine(
            $"{(passed ? "PASS" : "FAIL")} configured-normal-attack-overrides-slot1 | " +
            $"用户普通攻击=A 必须优先于角色技能槽1=Q | hotkeys={string.Join(',', backend.Hotkeys)}");
        return passed ? 0 : 1;
    }

    private static async Task<int> RunConfiguredAcquireHotkeyPriorityAssertion()
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        config.Targeting.AcquireHotkey = "G";
        config.SkillCooldownRecognitionEnabled = false;
        config.Skills.Clear();
        var backend = new FakeCombatBackend(
            Enumerable.Repeat(NoTarget(), 30).ToArray(),
            consumeGlobalCooldown: true);
        var controller = new NormalHangController(
            log,
            new FakeGlobalConfigService(config),
            new FakeCombatBackendSelector(backend),
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            gameHotkeys: new StaticGameHotkeyProfileService(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [GameInputSemanticIds.TargetSelect] = "F7"
                }));
        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1005,
            Pid = 1005,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin64\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });

        controller.Start();
        await WaitUntilAsync(() => backend.Hotkeys.Contains("G"), 1200).ConfigureAwait(false);
        controller.Stop();

        var passed = backend.Hotkeys.Contains("G") && !backend.Hotkeys.Contains("F7");
        Console.WriteLine(
            $"{(passed ? "PASS" : "FAIL")} configured-target-select-overrides-client-mapping | " +
            $"Agent configured G must override attached client mapping F7 | hotkeys={string.Join(',', backend.Hotkeys)}");
        return passed ? 0 : 1;
    }

    private static async Task<int> RunPetRestoreVisualFallbackAssertions()
    {
        var unavailable = await RunPetRestoreVisualFallbackCase(
            matched: false,
            cooldown: VisionCooldownState.Unknown,
            clickX: 820,
            clickBottomOffset: 72,
            buffAvailable: true,
            fatigue: VisionTriState.Absent).ConfigureAwait(false);
        var cooling = await RunPetRestoreVisualFallbackCase(
            matched: true,
            cooldown: VisionCooldownState.Cooling,
            clickX: 820,
            clickBottomOffset: 72,
            buffAvailable: true,
            fatigue: VisionTriState.Absent).ConfigureAwait(false);
        var uncalibrated = await RunPetRestoreVisualFallbackCase(
            matched: false,
            cooldown: VisionCooldownState.Unknown,
            clickX: 0,
            clickBottomOffset: 0,
            buffAvailable: true,
            fatigue: VisionTriState.Absent).ConfigureAwait(false);
        var buffUnavailable = await RunPetRestoreVisualFallbackCase(
            matched: true,
            cooldown: VisionCooldownState.Ready,
            clickX: 820,
            clickBottomOffset: 72,
            buffAvailable: false,
            fatigue: VisionTriState.Unknown).ConfigureAwait(false);
        var fatigueUnknown = await RunPetRestoreVisualFallbackCase(
            matched: true,
            cooldown: VisionCooldownState.Ready,
            clickX: 820,
            clickBottomOffset: 72,
            buffAvailable: true,
            fatigue: VisionTriState.Unknown).ConfigureAwait(false);
        var fatiguePresent = await RunPetRestoreVisualFallbackCase(
            matched: true,
            cooldown: VisionCooldownState.Ready,
            clickX: 820,
            clickBottomOffset: 72,
            buffAvailable: true,
            fatigue: VisionTriState.Present).ConfigureAwait(false);

        var unavailablePassed = unavailable.Backend.ClientClicks.Count > 0
                                && unavailable.Log.Entries.Any(entry => entry.Message.Contains("安全回退", StringComparison.Ordinal));
        var coolingPassed = cooling.Backend.ClientClicks.Count == 0
                            && cooling.Log.Entries.Any(entry => entry.Message.Contains("仍在冷却", StringComparison.Ordinal));
        var uncalibratedPassed = uncalibrated.Backend.ClientClicks.Count == 0
                                 && uncalibrated.Log.Entries.Any(entry => entry.Message.Contains("点击点无效", StringComparison.Ordinal));
        var fatigueUnknownPassed = fatigueUnknown.Backend.ClientClicks.Count == 0
                                   && fatigueUnknown.Log.Entries.Any(entry => entry.Message.Contains("BUFF 安全门禁", StringComparison.Ordinal));
        var buffUnavailablePassed = buffUnavailable.Backend.ClientClicks.Count == 0
                                    && buffUnavailable.Log.Entries.Any(entry => entry.Message.Contains("BUFF 安全门禁", StringComparison.Ordinal));
        var fatiguePresentPassed = fatiguePresent.Backend.ClientClicks.Count == 0
                                   && fatiguePresent.Log.Entries.Any(entry => entry.Message.Contains("BUFF 安全门禁", StringComparison.Ordinal));

        Console.WriteLine(
            $"{(unavailablePassed ? "PASS" : "FAIL")} pet-restore-vision-unavailable-falls-back | " +
            $"视觉不可用时仅使用已校准安全点 | clicks={unavailable.Backend.ClientClicks.Count}");
        Console.WriteLine(
            $"{(coolingPassed ? "PASS" : "FAIL")} pet-restore-vision-cooling-blocks-fallback | " +
            $"视觉确认冷却时禁止回退误点 | clicks={cooling.Backend.ClientClicks.Count}");
        Console.WriteLine(
            $"{(uncalibratedPassed ? "PASS" : "FAIL")} pet-restore-uncalibrated-never-clicks | " +
            $"未校准坐标不允许猜测点击 | clicks={uncalibrated.Backend.ClientClicks.Count}");
        Console.WriteLine(
            $"{(fatigueUnknownPassed ? "PASS" : "FAIL")} pet-restore-fatigue-unknown-blocks | " +
            $"BUFF 身份启用但疲劳未知时不得点击或回退 | clicks={fatigueUnknown.Backend.ClientClicks.Count}");
        Console.WriteLine(
            $"{(buffUnavailablePassed ? "PASS" : "FAIL")} pet-restore-buff-unavailable-blocks | " +
            $"BUFF 身份不可用时必须 fail closed | clicks={buffUnavailable.Backend.ClientClicks.Count}");
        Console.WriteLine(
            $"{(fatiguePresentPassed ? "PASS" : "FAIL")} pet-restore-fatigue-present-blocks | " +
            $"疲劳 Buff 存在时不得点击宠物主动技能 | clicks={fatiguePresent.Backend.ClientClicks.Count}");

        return (unavailablePassed ? 0 : 1)
               + (coolingPassed ? 0 : 1)
               + (uncalibratedPassed ? 0 : 1)
               + (buffUnavailablePassed ? 0 : 1)
               + (fatigueUnknownPassed ? 0 : 1)
               + (fatiguePresentPassed ? 0 : 1);
    }

    private static async Task<PetRestoreFallbackCaseResult> RunPetRestoreVisualFallbackCase(
        bool matched,
        VisionCooldownState cooldown,
        int clickX,
        int clickBottomOffset,
        bool buffAvailable = false,
        VisionTriState fatigue = VisionTriState.Unknown)
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        config.HealthRecoveryMode = PlayerHealthRecoveryMode.Symbiosis;
        config.ManaRecoveryMode = PlayerManaRecoveryMode.Medicine;
        config.HealHotkey = "";
        config.ManaHotkey = "";
        config.HpThresholdPct = 70;
        config.SymbiosisHealActivationMode = PetSkillActivationMode.ClientClick;
        config.SymbiosisHealClickX = clickX;
        config.SymbiosisHealClickBottomOffset = clickBottomOffset;
        config.SymbiosisHealCooldownMs = 30000;
        config.PetFoodHotkey = "";
        var lowHealth = MonsterTargetWithPetHp("training target", 100, 100) with
        {
            PlayerHpText = "30",
            PlayerHpPercent = 30,
            PlayerMpText = "100",
            PlayerMpPercent = 100
        };
        var backend = new FakeCombatBackend(Enumerable.Repeat(lowHealth, 50).ToArray(), consumeGlobalCooldown: true);
        var configService = new FakeGlobalConfigService(config);
        var controller = new NormalHangController(
            log,
            configService,
            new FakeCombatBackendSelector(backend),
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            perceptionHub: new StaticPetRestorePerceptionHub(matched, cooldown, buffAvailable, fatigue),
            visionActionPlanner: new StaticVisionActionPlanner(),
            visionActionExecutor: new FakeVisionActionExecutor());
        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1003,
            Pid = 1003,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });

        controller.Start();
        await Task.Delay(450).ConfigureAwait(false);
        controller.Stop();
        return new PetRestoreFallbackCaseResult(backend, log);
    }

    private static async Task<int> RunAutoBackendRefreshAssertion()
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        config.CombatBackendPreference = CombatBackendPreference.Auto;
        var configService = new FakeGlobalConfigService(config);
        var external = new FakeCombatBackend(Enumerable.Repeat(NoTarget(), 20).ToArray(), consumeGlobalCooldown: true);
        var builtIn = new FakeCombatBackend(Enumerable.Repeat(NoTarget(), 20).ToArray(), consumeGlobalCooldown: true, CombatBackendKind.InternalPlugin);
        var selector = new SwitchingCombatBackendSelector(external, builtIn);
        var telemetry = new FakeCombatLoopTelemetryService();
        var controller = new NormalHangController(
            log,
            configService,
            selector,
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            telemetry: telemetry);

        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1001,
            Pid = 1001,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });

        controller.Start();
        await WaitUntilAsync(
            () => telemetry.Entries.Any(e => e.Event == "decision" && e.Decision == "内置挂机监控"),
            1200).ConfigureAwait(false);
        controller.Stop();

        var passed = selector.ResolveCount >= 2
                     && telemetry.Entries.Any(e => e.Event == "decision" && e.Decision == "内置挂机监控")
                     && external.Hotkeys.Count == 0
                     && builtIn.Hotkeys.Count == 0;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} auto-backend-refresh-switches-to-monitor | 自动判断运行中重判为内置监控后不得继续发送攻击键 | resolves={selector.ResolveCount}, externalHotkeys={external.Hotkeys.Count}, builtInHotkeys={builtIn.Hotkeys.Count}");
        if (!passed)
        {
            foreach (var entry in log.Entries.TakeLast(12))
                Console.WriteLine($"  {entry}");
        }

        return passed ? 0 : 1;
    }

    private static int RunPlannerAssertions()
    {
        var planner = new CombatLoopPlanner();
        var failed = 0;

        failed += AssertPlan(
            "planner-dead-before-weak-target",
            planner.Plan(new CombatLoopPlannerInput(
                TargetSwitchMode.KillThenSwitch,
                IsTargetDead: true,
                IsUnconfirmedWeakTarget: true,
                CanContinueWeakTarget: true,
                ShouldForceWeakTargetReacquire: false,
                ShouldTreatTargetLost: false,
                HadTarget: true,
                HasActionableTarget: true,
                CanUsePureKeyBaseline: false,
                HasPendingFallbackAttack: false,
                RequiresRecognizedTarget: false,
                NoActionReason: "weak")),
            CombatLoopPlanAction.HandleDeadOrLost,
            CombatLoopDecisionKind.TargetDeadSwitch,
            "目标血量 0 必须优先于弱目标分支");

        failed += AssertPlan(
            "planner-weak-target-continues-output",
            planner.Plan(new CombatLoopPlannerInput(
                TargetSwitchMode.KillThenSwitch,
                IsTargetDead: false,
                IsUnconfirmedWeakTarget: true,
                CanContinueWeakTarget: true,
                ShouldForceWeakTargetReacquire: false,
                ShouldTreatTargetLost: false,
                HadTarget: true,
                HasActionableTarget: true,
                CanUsePureKeyBaseline: false,
                HasPendingFallbackAttack: false,
                RequiresRecognizedTarget: false,
                NoActionReason: "weak")),
            CombatLoopPlanAction.ContinueWeakTarget,
            CombatLoopDecisionKind.ContinueWeakTarget,
            "弱血条但无保护信号时必须继续输出，不能只切怪");

        failed += AssertPlan(
            "planner-no-target-pure-key-baseline",
            planner.Plan(new CombatLoopPlannerInput(
                TargetSwitchMode.KillThenSwitch,
                IsTargetDead: false,
                IsUnconfirmedWeakTarget: false,
                CanContinueWeakTarget: false,
                ShouldForceWeakTargetReacquire: false,
                ShouldTreatTargetLost: false,
                HadTarget: false,
                HasActionableTarget: false,
                CanUsePureKeyBaseline: true,
                HasPendingFallbackAttack: false,
                RequiresRecognizedTarget: false,
                NoActionReason: "no target")),
            CombatLoopPlanAction.ExecutePureKeyBaseline,
            CombatLoopDecisionKind.PureKeyBaseline,
            "无 OCR 目标但允许基础挂机时必须走选怪+普攻兜底");

        failed += AssertPlan(
            "planner-existing-target-loss-waits",
            planner.Plan(new CombatLoopPlannerInput(
                TargetSwitchMode.KillThenSwitch,
                IsTargetDead: false,
                IsUnconfirmedWeakTarget: false,
                CanContinueWeakTarget: false,
                ShouldForceWeakTargetReacquire: false,
                ShouldTreatTargetLost: false,
                HadTarget: true,
                HasActionableTarget: false,
                CanUsePureKeyBaseline: true,
                HasPendingFallbackAttack: false,
                RequiresRecognizedTarget: false,
                NoActionReason: "lost")),
            CombatLoopPlanAction.WaitTargetLost,
            CombatLoopDecisionKind.TargetLostWait,
            "已有目标短暂丢失时必须等待二次确认，不能立刻乱切");

        failed += AssertPlan(
            "planner-pending-fallback-after-acquire",
            planner.Plan(new CombatLoopPlannerInput(
                TargetSwitchMode.KillThenSwitch,
                IsTargetDead: false,
                IsUnconfirmedWeakTarget: false,
                CanContinueWeakTarget: false,
                ShouldForceWeakTargetReacquire: false,
                ShouldTreatTargetLost: false,
                HadTarget: false,
                HasActionableTarget: false,
                CanUsePureKeyBaseline: false,
                HasPendingFallbackAttack: true,
                RequiresRecognizedTarget: false,
                NoActionReason: "pending")),
            CombatLoopPlanAction.ExecutePendingFallbackAttack,
            CombatLoopDecisionKind.PendingFallbackAttack,
            "选怪后 OCR 未刷新时必须允许兜底普攻");

        failed += AssertPlan(
            "planner-weak-target-pending-fallback-wins",
            planner.Plan(new CombatLoopPlannerInput(
                TargetSwitchMode.KillThenSwitch,
                IsTargetDead: false,
                IsUnconfirmedWeakTarget: true,
                CanContinueWeakTarget: false,
                ShouldForceWeakTargetReacquire: false,
                ShouldTreatTargetLost: false,
                HadTarget: false,
                HasActionableTarget: false,
                CanUsePureKeyBaseline: true,
                HasPendingFallbackAttack: true,
                RequiresRecognizedTarget: false,
                NoActionReason: "weak pending")),
            CombatLoopPlanAction.ExecutePendingFallbackAttack,
            CombatLoopDecisionKind.PendingFallbackAttack,
            "选怪后的弱血条不能吞掉兜底普攻");

        return failed;
    }

    private static int RunHealthAnalyzerAssertions()
    {
        var failed = 0;

        failed += AssertHealth(
            "health-no-decision",
            CombatLoopHealthAnalyzer.Analyze(Array.Empty<CombatLoopTelemetryEntry>()),
            report => report.HasError && report.Findings.Any(f => f.Code == "no-decision"),
            "没有 decision 必须明确诊断为控制器未启动或遥测未写入");

        failed += AssertHealth(
            "health-weak-only-heavy",
            CombatLoopHealthAnalyzer.Analyze(Enumerable.Range(0, 5).Select(_ => Telemetry("decision", decision: "弱目标续打", targetHp: 100))),
            report => report.Findings.Any(f => f.Code == "weak-only-heavy")
                      && report.Findings.Any(f => f.Code == "weak-no-structure-heavy")
                      && report.Findings.Any(f => f.Code == "sample-coverage-low"),
            "长期只有弱血条必须提示目标区域/样本覆盖问题，不能当成健康链路");

        failed += AssertHealth(
            "health-protection-without-sample",
            CombatLoopHealthAnalyzer.Analyze(Enumerable.Range(0, 6).Select(_ => Telemetry(
                "decision",
                decision: "保护目标",
                targetHp: 98,
                targetLooksLikePlayer: true,
                targetHasSchoolMarker: true,
                targetHasPlayerPortrait: true))),
            report => report.Findings.Any(f => f.Code == "protection-without-sample")
                      && report.Findings.Any(f => f.Code == "sample-coverage-low"),
            "保护目标没有样本支撑时必须提示可能是结构图色误判");

        failed += AssertHealth(
            "health-action-without-sent-attack",
            CombatLoopHealthAnalyzer.Analyze(new[]
            {
                Telemetry("decision", decision: "攻击当前目标", targetName: "石老虎", targetHp: 100, targetSampleKind: "monster"),
                Telemetry("action", attackResult: "等待普攻间隔")
            }),
            report => report.HasError && report.Findings.Any(f => f.Code == "no-sent-attack"),
            "进入 action 但没有成功普攻必须报错，定位只选怪不攻击");

        failed += AssertHealth(
            "health-attack-chain-ok",
            CombatLoopHealthAnalyzer.Analyze(new[]
            {
                Telemetry("decision", decision: "攻击当前目标", targetName: "石老虎", targetHp: 100, targetSampleKind: "monster"),
                Telemetry("action", attackResult: "已发送")
            }),
            report => !report.HasError && report.Findings.Any(f => f.Code == "attack-chain-ok"),
            "有强目标和成功普攻时应标记基础攻击链路健康");

        return failed;
    }

    private static int AssertPlan(
        string name,
        CombatLoopPlan plan,
        CombatLoopPlanAction expectedAction,
        CombatLoopDecisionKind expectedKind,
        string expectation)
    {
        var passed = plan.Action == expectedAction && plan.Decision.Kind == expectedKind;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name} | {expectation} | action={plan.Action} kind={plan.Decision.Kind}");
        return passed ? 0 : 1;
    }

    private static int AssertHealth(
        string name,
        CombatLoopHealthReport report,
        Func<CombatLoopHealthReport, bool> assert,
        string expectation)
    {
        var passed = assert(report);
        var findings = report.Findings.Count == 0
            ? "-"
            : string.Join(",", report.Findings.Select(f => f.Code));
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name} | {expectation} | findings={findings}");
        return passed ? 0 : 1;
    }

    private static CombatLoopTelemetryEntry Telemetry(
        string eventName,
        string decision = "",
        string attackResult = "",
        string targetName = "",
        int? targetHp = null,
        string targetSampleKind = "",
        bool targetLooksLikePlayer = false,
        bool targetLooksLikePet = false,
        bool targetProtectionAmbiguous = false,
        bool targetHasSchoolMarker = false,
        bool targetHasPlayerPortrait = false,
        bool targetHasPlayerStatusBars = false) => new()
        {
            Event = eventName,
            Decision = decision,
            AttackResult = attackResult,
            TargetName = targetName,
            TargetHp = targetHp,
            TargetSampleKind = targetSampleKind,
            TargetLooksLikePlayer = targetLooksLikePlayer,
            TargetLooksLikePet = targetLooksLikePet,
            TargetProtectionAmbiguous = targetProtectionAmbiguous,
            TargetHasSchoolMarker = targetHasSchoolMarker,
            TargetHasPlayerPortrait = targetHasPlayerPortrait,
            TargetHasPlayerStatusBars = targetHasPlayerStatusBars
        };

    private static CombatScenario Scenario(
        string name,
        IReadOnlyList<CombatRecognitionResult> frames,
        Func<FakeCombatBackend, bool> assert,
        string expectation,
        bool consumeGlobalCooldown = true,
        int timeoutMs = 1800,
        Action<GlobalConfig>? configure = null,
        Action<FakeCombatBackend>? configureBackend = null)
        => Scenario(
            name,
            frames,
            result => assert(result.Backend),
            expectation,
            consumeGlobalCooldown,
            timeoutMs,
            configure,
            configureBackend);

    private static CombatScenario Scenario(
        string name,
        IReadOnlyList<CombatRecognitionResult> frames,
        Func<ScenarioResult, bool> assert,
        string expectation,
        bool consumeGlobalCooldown = true,
        int timeoutMs = 1800,
        Action<GlobalConfig>? configure = null,
        Action<FakeCombatBackend>? configureBackend = null)
        => new(name, frames, assert, expectation, consumeGlobalCooldown, timeoutMs, configure, configureBackend);

    private static async Task<bool> RunScenario(CombatScenario scenario)
    {
        var log = new FakeLogService();
        var config = CreateConfig();
        scenario.Configure?.Invoke(config);
        var configService = new FakeGlobalConfigService(config);
        var backend = new FakeCombatBackend(scenario.Frames, scenario.ConsumeGlobalCooldown);
        scenario.ConfigureBackend?.Invoke(backend);
        var selector = new FakeCombatBackendSelector(backend);
        var telemetry = new FakeCombatLoopTelemetryService();
        var states = new System.Collections.Concurrent.ConcurrentBag<string>();
        var controller = new NormalHangController(
            log,
            configService,
            selector,
            new DisabledReturnPointRuntime(),
            new StateMachineEngine(log),
            telemetry: telemetry);
        controller.StateChanged += (_, _) => states.Add(controller.CurrentStateName);

        controller.Attach(new BoundGameWindow
        {
            Hwnd = 0x1001,
            Pid = 1001,
            Title = "Harness Game",
            ProcessName = "Game",
            ProcessPath = @"C:\Game\TLBB\Bin\Game.exe",
            Bitness = ClientBitness.X64,
            ClientType = ClientType.Tlbb64,
            ClientWidth = 1920,
            ClientHeight = 1080
        });

        controller.Start();
        var scenarioResult = new ScenarioResult(backend, states, log, telemetry);
        await WaitUntilAsync(() => scenario.Assert(scenarioResult), scenario.TimeoutMs).ConfigureAwait(false);
        controller.Stop();

        var passed = scenario.Assert(scenarioResult);
        var hotkeys = backend.Hotkeys.Count == 0 ? "-" : string.Join(",", backend.Hotkeys);
        var stateSnapshot = states.ToArray();
        var stateText = stateSnapshot.Length == 0 ? "-" : string.Join(">", stateSnapshot.Distinct());
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {scenario.Name} | {scenario.Expectation} | hotkeys={hotkeys} | states={stateText}");

        if (!passed)
        {
            foreach (var entry in log.Entries.TakeLast(12))
                Console.WriteLine($"  {entry}");
        }

        return passed;
    }

    private static void ConfigureUnifiedDenyPolicy(GlobalConfig config)
    {
        config.Targeting.L2TargetNameEnabled = true;
        config.Targeting.Policy = new UnifiedTargetPolicyConfig
        {
            Enabled = true,
            Categories =
            [
                new TargetPolicyCategoryConfig
                {
                    Category = TargetPolicyCategoryKind.Monster,
                    Enabled = true,
                    AllowUnlisted = true
                },
                new TargetPolicyCategoryConfig
                {
                    Category = TargetPolicyCategoryKind.Boss,
                    Enabled = true,
                    AllowUnlisted = true,
                    Entries =
                    [
                        new TargetPolicyEntry
                        {
                            Name = "碧磷僵尸",
                            Enabled = true,
                            AllowAttack = false
                        }
                    ]
                },
                new TargetPolicyCategoryConfig
                {
                    Category = TargetPolicyCategoryKind.Player,
                    Enabled = false,
                    AllowUnlisted = false
                }
            ]
        };
    }

    private static GlobalConfig CreateConfig() => new()
    {
        CombatBackendPreference = CombatBackendPreference.ExternalOcr,
        InputDispatchMode = InputDispatchMode.PostMessage,
        AttackHotkey = "Q",
        AttackIntervalMs = 100,
        PublicCooldownMs = 300,
        Targeting = new TargetingPreset
        {
            SwitchMode = TargetSwitchMode.KillThenSwitch,
            Filter = TargetFilter.MonsterOnly,
            BlockPlayerTargets = true,
            AcquireHotkey = "Tab",
            ScanIntervalMs = 50,
            AcquireIntervalMs = 120,
            NoDamageReacquireMs = 15000,
            AutoAcquireWhenNoTarget = true,
            TargetLostDelayMs = 100
        },
        Skills = new List<SkillPreset>()
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < timeoutMs)
        {
            if (condition())
                return;
            await Task.Delay(30).ConfigureAwait(false);
        }
    }

    private static CombatRecognitionResult NoTarget() => new("", "", "100", "100", "100", null, 100, 100, 100);

    private static CombatRecognitionResult WeakTarget(int hp) => new("", hp.ToString(), "100", "100", "100", hp, 100, 100, 100);

    private static CombatRecognitionResult DeadTarget() => new("", "0", "100", "100", "100", 0, 100, 100, 100);

    private static CombatRecognitionResult StructuralAttackableTarget(int hp) => new("", hp.ToString(), "100", "100", "100", hp, 100, 100, 100)
    {
        TargetIsAttackable = true
    };

    private static CombatRecognitionResult MonsterTarget(string name, int hp) => new(name, hp.ToString(), "100", "100", "100", hp, 100, 100, 100)
    {
        TargetSampleKind = "monster",
        TargetIsAttackable = true
    };

    private static CombatRecognitionResult MonsterTargetWithPetHp(string name, int hp, int petHp) =>
        MonsterTarget(name, hp) with
        {
            PetHpText = petHp.ToString(),
            PetHpPercent = petHp,
            PetDeploymentState = PetDeploymentState.Deployed
        };

    private static CombatRecognitionResult BossTarget(string name, int hp) => new(name, hp.ToString(), "100", "100", "100", hp, 100, 100, 100)
    {
        TargetSampleKind = "boss",
        TargetIsAttackable = true
    };

    private static CombatRecognitionResult PlayerTarget(int hp) => new("玩家", hp.ToString(), "100", "100", "100", hp, 100, 100, 100)
    {
        TargetLooksLikePlayer = true
    };

    private static CombatRecognitionResult AmbiguousTarget(int hp) => new("", hp.ToString(), "100", "100", "100", hp, 100, 100, 100)
    {
        TargetProtectionAmbiguous = true,
        TargetSampleKind = "player",
        TargetSampleLabel = "low-confidence-player",
        TargetSampleScore = 0.94
    };

    private static CombatRecognitionResult StructuralAmbiguousTarget(int hp) => new("", hp.ToString(), "100", "100", "100", hp, 100, 100, 100)
    {
        TargetProtectionAmbiguous = true
    };

    private static RuntimeSnapshot SceneSnapshot(string mapName, int x, int y) => new()
    {
        Provider = RuntimeStateProviderKind.ScreenshotOcr,
        Quality = RuntimeSnapshotQuality.Reliable,
        ConfidencePercent = 85,
        Scene = new SceneRuntimeSnapshot
        {
            Quality = RuntimeSnapshotQuality.Reliable,
            Source = "harness-scene",
            ConfidencePercent = 85,
            SceneUsable = true,
            MapFieldReady = true,
            CoordinateFieldReady = true,
            ReturnPointFieldReady = true,
            GateReason = "harness reliable scene",
            MapName = mapName,
            MapNameSource = "harness-map",
            MapNameConfidencePercent = 90,
            X = x,
            Y = y,
            CoordinateSource = "harness-coordinate",
            CoordinateConfidencePercent = 80,
            IsDungeon = false
        }
    };
}

internal sealed record CombatScenario(
    string Name,
    IReadOnlyList<CombatRecognitionResult> Frames,
    Func<ScenarioResult, bool> Assert,
    string Expectation,
    bool ConsumeGlobalCooldown,
    int TimeoutMs,
    Action<GlobalConfig>? Configure,
    Action<FakeCombatBackend>? ConfigureBackend);

internal sealed record ScenarioResult(
    FakeCombatBackend Backend,
    System.Collections.Concurrent.ConcurrentBag<string> States,
    FakeLogService Log,
    FakeCombatLoopTelemetryService TelemetryService)
{
    public IReadOnlyList<LogEntry> Logs => Log.Entries;
    public IReadOnlyList<CombatLoopTelemetryEntry> Telemetry => TelemetryService.Entries;
}

internal sealed record PetRestoreFallbackCaseResult(
    FakeCombatBackend Backend,
    FakeLogService Log);

internal sealed class FakeCombatBackend : ICombatBackend
{
    private readonly IReadOnlyList<CombatRecognitionResult> _frames;
    private readonly bool _consumeGlobalCooldown;
    private readonly CombatBackendKind _kind;
    private readonly ConcurrentDictionary<string, int> _remainingFailuresByHotkey = new(StringComparer.OrdinalIgnoreCase);
    private int _readIndex = -1;

    public FakeCombatBackend(
        IReadOnlyList<CombatRecognitionResult> frames,
        bool consumeGlobalCooldown,
        CombatBackendKind kind = CombatBackendKind.ExternalOcr)
    {
        _frames = frames;
        _consumeGlobalCooldown = consumeGlobalCooldown;
        _kind = kind;
    }

    public CombatBackendKind Kind => _kind;
    public string DisplayName => "Harness";
    public ConcurrentQueue<string> HotkeyEvents { get; } = new();
    public ConcurrentQueue<(string Hotkey, long Ticks)> TimedHotkeyEvents { get; } = new();
    public ConcurrentQueue<(int X, int Y)> ClientClicks { get; } = new();
    public IReadOnlyList<string> Hotkeys => HotkeyEvents.ToArray();
    public IReadOnlyList<(string Hotkey, long Ticks)> TimedHotkeys => TimedHotkeyEvents.ToArray();
    public int ReadDelayMs { get; set; }

    public void FailNextHotkey(string hotkey, int count = 1)
    {
        if (string.IsNullOrWhiteSpace(hotkey) || count <= 0)
            return;

        _remainingFailuresByHotkey.AddOrUpdate(hotkey, count, (_, existing) => existing + count);
    }

    public CombatBackendStatus GetStatus(BoundGameWindow window, GlobalConfig config) =>
        new(Kind, DisplayName, true, "Harness backend ready");

    public CombatRecognitionResult Read(BoundGameWindow window, GlobalConfig config)
    {
        if (ReadDelayMs > 0)
            Thread.Sleep(ReadDelayMs);

        var index = Math.Min(Interlocked.Increment(ref _readIndex), Math.Max(0, _frames.Count - 1));
        return _frames[index];
    }

    public bool TryParseHotkey(string hotkey, out string error)
    {
        error = string.IsNullOrWhiteSpace(hotkey) ? "empty hotkey" : "";
        return error.Length == 0;
    }

    public bool TryConsumeGlobalCooldown(int cooldownMs) => _consumeGlobalCooldown;

    public bool CastHotkey(BoundGameWindow window, string hotkey, GlobalConfig config)
    {
        if (_remainingFailuresByHotkey.TryGetValue(hotkey, out var remaining) && remaining > 0)
        {
            if (remaining <= 1)
                _remainingFailuresByHotkey.TryRemove(hotkey, out _);
            else
                _remainingFailuresByHotkey[hotkey] = remaining - 1;
            return false;
        }

        HotkeyEvents.Enqueue(hotkey);
        TimedHotkeyEvents.Enqueue((hotkey, Environment.TickCount64));
        return true;
    }

    public bool ClickClient(BoundGameWindow window, int x, int y, GlobalConfig config)
    {
        ClientClicks.Enqueue((x, y));
        return true;
    }
}

internal sealed class FakeCombatBackendSelector(FakeCombatBackend backend) : ICombatBackendSelector
{
    public ICombatBackend Resolve(BoundGameWindow window, GlobalConfig config, out CombatBackendStatus status)
    {
        status = backend.GetStatus(window, config);
        return backend;
    }
}

internal sealed class SwitchingCombatBackendSelector(FakeCombatBackend first, FakeCombatBackend next) : ICombatBackendSelector
{
    private int _resolveCount;

    public int ResolveCount => _resolveCount;

    public ICombatBackend Resolve(BoundGameWindow window, GlobalConfig config, out CombatBackendStatus status)
    {
        var count = Interlocked.Increment(ref _resolveCount);
        var backend = count <= 1 ? first : next;
        status = backend.GetStatus(window, config);
        return backend;
    }
}

internal sealed class FakeEnvironmentGateService(CombatEnvironmentGateResult result) : ICombatEnvironmentGateService
{
    public CombatEnvironmentGateResult Evaluate(BoundGameWindow window, GlobalConfig config) => result;
}

internal sealed class FakeRuntimeSnapshotService(IReadOnlyList<RuntimeSnapshot> snapshots) : IRuntimeSnapshotService
{
    private int _index = -1;

    public RuntimeSnapshot Read(BoundGameWindow window, RuntimeStateProviderKind preferredProvider)
    {
        var index = Math.Min(Interlocked.Increment(ref _index), Math.Max(0, snapshots.Count - 1));
        return snapshots[index];
    }

    public IReadOnlyList<RuntimeSnapshot> ReadAll(BoundGameWindow window) => snapshots;
}

internal sealed class RuntimeSnapshotPerceptionHub(IRuntimeSnapshotService snapshots) : IPerceptionHub
{
    public RuntimePerceptionSnapshot Read(BoundGameWindow window, GlobalConfig config, bool includeOpenText = false)
    {
        var scene = snapshots.Read(window, RuntimeStateProviderKind.ScreenshotOcr).Scene;
        return new RuntimePerceptionSnapshot
        {
            Width = window.ClientWidth,
            Height = window.ClientHeight,
            Minimap = new MinimapPerception
            {
                MapName = scene.MapName ?? string.Empty,
                X = scene.X,
                Y = scene.Y,
                IsDungeon = scene.IsDungeon,
                Source = scene.Quality == RuntimeSnapshotQuality.Unavailable
                    ? PerceptionSourceKind.Unavailable
                    : PerceptionSourceKind.Fused
            }
        };
    }
}

internal sealed class StaticSkillInventoryPerceptionHub(
    bool identityMatched,
    string slotKey = "E",
    VisionSkillIdentityState? identityOverride = null,
    string identitySource = "icons_ingame",
    string surface = "bottom_skill_bar",
    bool staleFrame = false,
    bool duplicateSkill = false) : IPerceptionHub
{
    public RuntimePerceptionSnapshot Read(BoundGameWindow window, GlobalConfig config, bool includeOpenText = false) =>
        new()
        {
            FrameSequence = 10,
            Width = window.ClientWidth,
            Height = window.ClientHeight,
            SkillBar = new SkillBarPerception
            {
                VisualSlots = BuildSlots(),
                Source = PerceptionSourceKind.Fused
            }
        };

    private IReadOnlyList<VisionSkillSlotPerception> BuildSlots()
    {
        var isRight = string.Equals(surface, "right_skill_bar", StringComparison.OrdinalIgnoreCase);
        var identity = identityOverride ?? (identityMatched
            ? VisionSkillIdentityState.Matched
            : VisionSkillIdentityState.Unknown);
        var slots = new List<VisionSkillSlotPerception>
        {
            BuildSlot(
                index: 29,
                slotId: isRight ? "r1c1" : "r3c2",
                row: isRight ? 0 : 2,
                column: isRight ? 0 : 1,
                clickX: isRight ? 1700 : null,
                cooldown: duplicateSkill ? VisionCooldownState.Cooling : VisionCooldownState.Ready,
                score: duplicateSkill ? 0.99 : identityMatched ? 0.98 : 0.40,
                identity)
        };
        if (duplicateSkill)
        {
            slots.Add(BuildSlot(
                index: 30,
                slotId: "r1c2",
                row: 0,
                column: 1,
                clickX: 1744,
                cooldown: VisionCooldownState.Ready,
                score: 0.98,
                identity));
        }

        return slots;
    }

    private VisionSkillSlotPerception BuildSlot(
        int index,
        string slotId,
        int row,
        int column,
        int? clickX,
        VisionCooldownState cooldown,
        double score,
        VisionSkillIdentityState identity) =>
        new()
        {
            FrameSequence = staleFrame ? 9 : 10,
            Index = index,
            SlotId = slotId,
            Row = row,
            Column = column,
            Surface = surface,
            Key = string.Equals(surface, "bottom_skill_bar", StringComparison.OrdinalIgnoreCase) ? slotKey : "",
            ActivationKind = string.Equals(surface, "right_skill_bar", StringComparison.OrdinalIgnoreCase) ? "mouse" : "keyboard",
            SlotBox = clickX.HasValue ? new VisionBox(clickX.Value - 16, 284, 32, 32) : new VisionBox(400, 900, 32, 32),
            ClickX = clickX,
            ClickY = clickX.HasValue ? 300 : null,
            SkillId = identityMatched ? "xy_004" : "",
            SkillName = identityMatched ? "弹指神功" : "",
            Identity = identity,
            IdentitySource = identitySource,
            Cooldown = cooldown,
            HangUsable = identityMatched && identity is VisionSkillIdentityState.Matched,
            Castable = identityMatched && identity is VisionSkillIdentityState.Matched && cooldown == VisionCooldownState.Ready,
            PolicyClass = "direct_damage",
            ReleaseRule = identityMatched ? "allowed_by_policy" : "identity_unknown",
            MatchScore = score,
            MatchMargin = identityMatched ? 0.22 : 0.01,
            Confidence = 0.98
        };
}

internal enum PostActionEvidenceMode
{
    Cooling,
    FeedbackCooldown,
    FeedbackBusy,
    None
}

internal sealed record VisualSkillPostActionCaseResult(
    int SkillSends,
    IReadOnlyList<LogEntry> Logs,
    int AsyncRequests);

internal sealed class ActionAwareSkillPerceptionHub(
    Func<bool> actionSent,
    PostActionEvidenceMode mode) : IPerceptionHub
{
    private long _sequence;
    private int _asyncRequests;
    private RuntimePerceptionSnapshot? _latest;

    public int AsyncRequests => Volatile.Read(ref _asyncRequests);

    public RuntimePerceptionSnapshot Read(
        BoundGameWindow window,
        GlobalConfig config,
        PerceptionReadOptions options)
    {
        if (options.RequestSystemFeedbackTextAsync)
            Interlocked.Increment(ref _asyncRequests);
        return Capture(window);
    }

    public RuntimePerceptionSnapshot Read(
        BoundGameWindow window,
        GlobalConfig config,
        bool includeOpenText = false) => Capture(window);

    public bool TryGetLatest(string windowId, out RuntimePerceptionSnapshot snapshot)
    {
        snapshot = _latest!;
        return snapshot is not null;
    }

    private RuntimePerceptionSnapshot Capture(BoundGameWindow window)
    {
        var snapshot = CreateSnapshot(window);
        _latest = snapshot;
        return snapshot;
    }

    private RuntimePerceptionSnapshot CreateSnapshot(BoundGameWindow window)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var sent = actionSent();
        var cooling = sent && mode == PostActionEvidenceMode.Cooling;
        var feedback = sent && mode is PostActionEvidenceMode.FeedbackCooldown or PostActionEvidenceMode.FeedbackBusy
            ? new[]
            {
                new SystemFeedbackObservation(
                    sequence,
                    new VisionBox(850, 80, 220, 28),
                    1,
                    new OpenTextResult(
                        "cooldown",
                        1,
                        OpenTextSlot.Toast,
                        PerceptionSourceKind.OpenText,
                        "combat-harness"),
                    mode == PostActionEvidenceMode.FeedbackBusy ? "busy" : "cooldown",
                    mode == PostActionEvidenceMode.FeedbackBusy
                        ? SystemFeedbackSemanticKind.MaterialBusy
                        : SystemFeedbackSemanticKind.Cooldown,
                    DateTimeOffset.UtcNow)
            }
            : Array.Empty<SystemFeedbackObservation>();
        var slot = new VisionSkillSlotPerception
        {
            FrameSequence = sequence,
            Index = 29,
            SlotId = "r3c2",
            Row = 2,
            Column = 1,
            Surface = "bottom_skill_bar",
            Key = "E",
            ActivationKind = "keyboard",
            SlotBox = new VisionBox(400, 900, 32, 32),
            SkillId = "xy_004",
            SkillName = "post-action-skill",
            Identity = VisionSkillIdentityState.Matched,
            IdentitySource = "icons_ingame",
            Cooldown = cooling ? VisionCooldownState.Cooling : VisionCooldownState.Ready,
            HangUsable = true,
            Castable = !cooling,
            PolicyClass = "direct_damage",
            ReleaseRule = "allowed_by_policy",
            MatchScore = 0.99,
            MatchMargin = 0.25,
            Confidence = 0.99
        };
        return new RuntimePerceptionSnapshot
        {
            FrameSequence = sequence,
            Width = window.ClientWidth,
            Height = window.ClientHeight,
            SelfBars = new SelfBarsPerception
            {
                PlayerMpPercent = 80,
                Source = PerceptionSourceKind.Fused
            },
            SkillBar = new SkillBarPerception
            {
                VisualSlots = new[] { slot },
                Source = PerceptionSourceKind.Fused
            },
            SystemFeedback = new SystemFeedbackPerception
            {
                FrameSequence = sequence,
                RequestedFrameSequence = sequence,
                DetectionRequested = true,
                TextRecognitionRequested = true,
                TextRecognitionCompleted = true,
                Available = true,
                Observations = feedback,
                ModelVersion = "combat-harness",
                Source = PerceptionSourceKind.Fused
            }
        };
    }
}

internal sealed class DelayedSystemFeedbackDetector(int delayMilliseconds) : ISystemFeedbackRegionDetectService
{
    public ConcurrentQueue<long> Sequences { get; } = new();

    public SystemFeedbackPerception Detect(
        CaptureResult frame,
        IOpenTextRecognizer openText,
        bool includeOpenText = true)
    {
        Thread.Sleep(delayMilliseconds);
        Sequences.Enqueue(frame.Sequence);
        return new SystemFeedbackPerception
        {
            FrameSequence = frame.Sequence,
            RequestedFrameSequence = frame.Sequence,
            DetectionRequested = true,
            TextRecognitionRequested = includeOpenText,
            TextRecognitionCompleted = includeOpenText,
            Available = true,
            Observations = new[]
            {
                new SystemFeedbackObservation(
                    frame.Sequence,
                    new VisionBox(2, 2, 10, 4),
                    1,
                    OpenTextResult.Empty(OpenTextSlot.Toast, "harness"),
                    "cooldown",
                    SystemFeedbackSemanticKind.Cooldown,
                    frame.CapturedAt)
            },
            ModelVersion = "combat-harness-delayed",
            Source = PerceptionSourceKind.Fused
        };
    }
}

internal sealed class StaticPetRestorePerceptionHub(
    bool matched,
    VisionCooldownState cooldown,
    bool buffAvailable = false,
    VisionTriState fatigue = VisionTriState.Unknown) : IPerceptionHub
{
    public RuntimePerceptionSnapshot Read(BoundGameWindow window, GlobalConfig config, bool includeOpenText = false) =>
        new()
        {
            Width = window.ClientWidth,
            Height = window.ClientHeight,
            BuffBar = new BuffAnchorPerception
            {
                Available = buffAvailable,
                Fatigue = fatigue,
                PetActiveSkillBlocked = !buffAvailable || fatigue != VisionTriState.Absent,
                Source = buffAvailable ? PerceptionSourceKind.Fused : PerceptionSourceKind.Unavailable
            },
            SkillBar = new SkillBarPerception
            {
                PetRestoreSkills = new[]
                {
                    new PetRestoreSkillPerception
                    {
                        Slot = "pet-health",
                        Variant = "symbiosis",
                        BusinessClass = "health_restore_skill",
                        Cooldown = cooldown,
                        ClickX = 860,
                        ClickY = 1000,
                        Confidence = matched ? 0.98 : 0.30,
                        IsMatched = matched
                    }
                },
                Source = PerceptionSourceKind.Fused
            }
        };
}

internal sealed class StaticVisionActionPlanner : IVisionActionPlanner
{
    public VisionActionDecision Plan(string mode, string requestJson) =>
        new(true, VisionActionKind.Click, 860, 1000, $"harness {mode}", requestJson);
}

internal sealed class FakeVisionActionExecutor : IVisionActionExecutor
{
    public int Executions { get; private set; }

    public bool TryExecuteClick(BoundGameWindow window, VisionActionDecision decision, GlobalConfig config)
    {
        Executions++;
        return true;
    }
}

internal sealed class DisabledReturnPointRuntime : IReturnPointRuntimeService
{
    public bool HasEnabledReturnPoint => false;
    public void Reset() { }
    public ReturnPointRuntimeResult Evaluate(BoundGameWindow window) =>
        new(ReturnPointRuntimeAction.None, false, "disabled");
}

internal sealed class FakeGlobalConfigService(GlobalConfig current) : IGlobalConfigService
{
    public GlobalConfig Current { get; } = current;
    public event EventHandler? Changed;
    public void SetSchool(School school) => Current.School = school;
    public bool SchoolProvidesHeal(School school) => school == School.Emei;
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    public void Save(string profileName) { }
    public bool Load(string profileName) => false;
    public void ExportToFile(string path) { }
    public bool ImportFromFile(string path) => false;
}

internal sealed class StaticTargetProtectionSampleService(TargetProtectionSampleMatch? match)
    : ITargetProtectionSampleService
{
    public TargetProtectionSampleMatch? Match(
        CaptureResult client,
        CombatRecognitionConfig config,
        TargetingPreset targeting) => match;
}

internal sealed class StaticGameHotkeyProfileService(
    IReadOnlyDictionary<string, string> mappings) : IGameHotkeyProfileService
{
    public GameHotkeyProfileLoadResult BindToRole(BoundGameWindow window, string roleName) =>
        new(
            GameHotkeyProfileStatus.Loaded,
            roleName,
            "harness",
            mappings.Select((item, index) => new GameHotkeyBinding(
                index + 1,
                item.Key,
                item.Value,
                0,
                true,
                "harness")).ToArray(),
            "harness");

    public bool TryResolve(nint hwnd, string semanticId, out GameHotkeyBinding binding)
    {
        if (mappings.TryGetValue(semanticId, out var hotkey))
        {
            binding = new GameHotkeyBinding(1, semanticId, hotkey, 0, true, "harness");
            return true;
        }

        binding = null!;
        return false;
    }

    public GameHotkeyProfileLoadResult? GetAttached(nint hwnd) => null;
    public void Unbind(nint hwnd) { }
}

internal sealed class FakeProfileService : IProfileService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyList<string> ListProfileNames() =>
        _values.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray();

    public string? CurrentProfileName { get; private set; }

    public void Save(string name, string jsonPayload)
    {
        _values[name] = jsonPayload;
        CurrentProfileName = name;
    }

    public string? Load(string name)
    {
        if (!_values.TryGetValue(name, out var value))
            return null;
        CurrentProfileName = name;
        return value;
    }

    public void Delete(string name)
    {
        _values.Remove(name);
        if (string.Equals(CurrentProfileName, name, StringComparison.Ordinal))
            CurrentProfileName = null;
    }
}

internal sealed class FakeLogService : ILogService
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();
    public event EventHandler<LogEntry>? Appended;
    public event EventHandler? Cleared;

    public void Log(LogEntry entry)
    {
        _entries.Enqueue(entry);
        Appended?.Invoke(this, entry);
    }

    public void Log(LogLevel level, string module, string message, string window = "-") =>
        Log(new LogEntry { Level = level, Module = module, Message = message, Window = window });

    public void Trace(string module, string message, string window = "-") => Log(LogLevel.Trace, module, message, window);
    public void Info(string module, string message, string window = "-") => Log(LogLevel.Info, module, message, window);
    public void Warn(string module, string message, string window = "-") => Log(LogLevel.Warn, module, message, window);
    public void Error(string module, string message, string window = "-") => Log(LogLevel.Error, module, message, window);
    public IDisposable Suppress(Func<LogEntry, bool> predicate) => NoopDisposable.Instance;

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    public string ExportToText() => string.Join(Environment.NewLine, _entries.Select(e => e.ToString()));

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

internal sealed class FakeCombatLoopTelemetryService : ICombatLoopTelemetryService
{
    private readonly ConcurrentQueue<CombatLoopTelemetryEntry> _entries = new();

    public string CurrentFilePath => "memory://combat-loop";
    public IReadOnlyList<CombatLoopTelemetryEntry> Entries => _entries.ToArray();

    public void Record(CombatLoopTelemetryEntry entry) => _entries.Enqueue(entry);
}
