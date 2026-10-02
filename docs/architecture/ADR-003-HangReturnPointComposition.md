# ADR-003: 挂机返点作为按键打怪子能力

日期：2026-06-21  
状态：Accepted

## 背景

历史版本 中“挂机返点”不是一个孤立玩法，它服务于普通挂机：角色边打怪，偏离挂机点、死亡、卡点或需要土灵返回时，返点逻辑临时接管动作，然后回到打怪循环。

龙伴行要求“同一时间只能运行一个主模式”。如果把“按键打怪”和“挂机返点”都做成平级主模式，会产生实际冲突：

- 启动“按键打怪”时，返点不会执行。
- 启动“挂机返点”时，角色只会回点，不会打怪。

这不符合历史版本 的使用方式。

## 决策

“返点配置”保留在挂机 Tab 里作为配置 tag，但不作为独立主模式启动。

普通挂机 `NormalHangController` 在每轮战斗判断前调用：

```text
IReturnPointRuntimeService.Evaluate(window)
```

如果返点子能力触发：

- 死亡返点
- 土灵返点
- 普通定点返点
- 卡点恢复

则普通挂机本轮暂停攻击、回血回蓝和技能释放，由返点子能力接管动作。返点动作没有触发时，普通挂机继续执行原来的打怪、换怪、回血回蓝和技能循环。

## 实现

新增：

- `IReturnPointRuntimeService`
- `ReturnPointRuntimeService`
- `ReturnPointRuntimeResult`
- `ReturnPointRuntimeAction`

调整：

- `NormalHangController` 注入 `IReturnPointRuntimeService`。
- `NormalHangController` 每轮读取战斗快照后，优先评估返点子能力。
- UI 中 `hang.return` 显示为“返点配置”，不再作为可启动主模式。
- `MainViewModel.SelectedController` 对 `hang.return` 返回 `null`，所以左侧启动按钮不会启动独立返点控制器。

保留：

- `ReturnPointPage` 和 `ReturnPointPageViewModel`，用于坐标录入、多坐标保存、土灵返点、死亡返点、卡点恢复等配置。
- 旧 `ReturnPointController` 暂时保留，不作为当前主入口；后续可在确认没有外部依赖后删除或改为诊断专用。

## 验证

`normal-hang` 诊断新增 `Return-subroutine` 用例：

- 模拟返点子能力触发。
- 断言普通挂机没有继续发送攻击/技能热键。
- 断言日志出现“返点子能力已接管”。

结果：

```text
Return-subroutine send-key calls=0
Return-subroutine logs=3
PASS
```
