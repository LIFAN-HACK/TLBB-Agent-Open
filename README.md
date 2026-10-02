# 龙伴行 TLBB Agent：一套能“看懂”天龙八部画面的本地 AI 视觉模型

想知道 AI 是怎么“看”游戏画面的吗？**龙伴行（TLBB Agent）** 现在开源啦！

它是一个跑在 Windows 上的 .NET 8 / WPF 桌面客户端，核心是一整套为《天龙八部》画面训练的 **YOLOv8 视觉模型**，用 ONNX Runtime 在本机 CPU 上推理，不用上传截图、也不强制要显卡。

**视觉模型亮点 👀**

- **两级识别（L1 → L2）**：L1 先在整帧里找到大区域：目标面板、目标血条、人物/宠物状态、技能栏、小地图，七类一次搞定；L2 再在裁出来的小块里读血量、技能 CD、地图名、坐标这些细节。
- **专项小模型**：右侧技能栏用 640/1280 双档融合；还有 Buff 锚点，背包/商店/银行/月卡/材料五类交互窗口，任务面板，系统提示区等。每个模型都附了模型卡和验证数据（比如交互窗口模型在测试集上 mAP50 达到 0.995）。
- **多分辨率适配**：785×617 到 2576×1408 的窗口都验证过。
- **宁可不动，也不乱点**：只要置信度低、坐标越界或者身份不明，结果一律是 unknown/noop。仓库里还有个“实机只读视觉验证”脚本，只看画面、不碰键鼠，适合先拿来体验识别效果。

**三步上手 🚀**

1. 准备 Windows + .NET 8 SDK，把仓库 clone 下来；
2. 准备模型权重（`.onnx`）：**权重包待发布**，发布后会放在 Releases。拿到之后解压到 `src/TlbbAgent.Core/models/vision/` 下对应的目录（也可以用环境变量 `TLBB_AGENT_VISION_ARTIFACTS` 指定位置）。仓库里已经有每个模型的配置、类别表和模型卡；
3. 运行 `dotnet build .\TlbbAgent.sln -c Release`，再执行 `tools\Publish-CurrentProduct.ps1`，最后双击 `启动龙伴行.cmd`。

**一起来玩 🤝**

欢迎来读读代码、提 Issue 反馈识别不准的画面，或者 PR 帮忙补样本、补门派技能图标、补新分辨率。Star 一下，一起把它调教得更聪明！

> 本项目仅供视觉识别技术学习与研究。使用自动化工具可能违反游戏用户协议，请自行评估风险。

---

## 许可证 / License

本项目采用 **[PolyForm Noncommercial License 1.0.0](LICENSE.md)**。

- ✅ 可免费用于学习、研究、个人非商业用途，欢迎提交 Issue 和 PR 参与贡献；
- ❌ **任何商业用途**（包括但不限于售卖、付费分发、付费代挂、集成到商业产品或服务）都需要事先获得作者的**书面许可**；
- 📝 申请商业授权：请新建一个 GitHub Issue，标题为 **“商业授权申请 / Commercial license request”**，或在 GitHub 上联系 [@LIFAN-HACK](https://github.com/LIFAN-HACK)。

This project is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE.md)**.

- ✅ Free for study, research, personal non-commercial use, and contributions;
- ❌ **Any commercial use** requires prior written permission from the author;
- 📝 To request a commercial license, open a GitHub Issue titled **"商业授权申请 / Commercial license request"** or contact [@LIFAN-HACK](https://github.com/LIFAN-HACK) on GitHub.

---

## 仓库结构

```text
src/              .NET 8 / WPF 桌面客户端（App / Contracts / Core）
src/TlbbAgent.Core/models/vision
                  视觉模型配置、类别表、模型卡与 UI 模板（不含 .onnx 权重）
data/skill_library
                  门派技能图标库与匹配索引
tools/            诊断、采样、视觉验证与各类 Harness 工具源码
server/           .NET 10 云端模块化单体骨架与测试
docs/architecture 架构决策记录与视觉接入说明
docs/requirements 视觉模型与功能的接入/交接文档
.github/          CI、依赖更新和安全检查
```

## 本地构建

桌面端要求 Windows 与 .NET 8 SDK：

```powershell
dotnet restore .\TlbbAgent.sln
dotnet build .\TlbbAgent.sln -c Release --no-restore
```

- 发布并启动：`tools\Publish-CurrentProduct.ps1`，然后运行 `启动龙伴行.cmd`。
- 只读视觉验证：先构建 `tools\TlbbAgent.Diagnostics`，再运行 `实机只读视觉验证.cmd`（不发送键鼠、不写内存，结果在 `artifacts\live-userdesktop-dispatch`）。
- 视觉推理使用 `Microsoft.ML.OnnxRuntime`（CPU），主程序按 x64 运行，可绑定 32/64 位游戏客户端。
- 部分诊断工具使用以下可选环境变量指向本地目录：`TLBB_VISION_ROOT`（视觉训练工作区）、`TLBB_LEGACY_DATA_ROOT`（旧数据导入源）、`TLBB_QUEST_LOG_HARD_NEGATIVE_FIXTURE`（任务面板 hard-negative 截图）。

云端工程独立使用 .NET 10 SDK，具体命令与模块边界见 `server/README.md`。

## 提交须知

以下内容请勿提交到仓库：

- ONNX、PyTorch 等模型权重与模型密钥；
- 内存转储、运行截图、用户配置、日志和数据库；
- 任何账号、令牌、证书、SSH、云服务等凭据；
- 含角色名、聊天内容等个人信息的截图；
- 第三方二进制、逆向工作目录和嵌套仓库；
- 未确认授权与许可证的训练样本或第三方源码。

更多规范见 [CONTRIBUTING.md](CONTRIBUTING.md)。安全问题请按 [SECURITY.md](SECURITY.md) 通过 GitHub 私下报告，不要创建公开 Issue。
