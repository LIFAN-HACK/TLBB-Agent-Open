# BlueDiamond Imported Data Index

更新时间：2026-06-20

本文记录 TLBB Agent 的旧数据迁移入口、导入范围和验证结果。原始授权数据目录只读，未修改、未删除、未 patch。

## 迁移原则

- 不复制 `xx.dll`。
- 不复制旧 EXE。
- 不复制 `账号.txt`。
- 不引入旧登录、注册、充值、会员验证、远端授权。
- 旧 `config.ini` 只保留脱敏后的 schema 参考。
- 脱敏规则：移除 `[root]` 段，移除包含账号、帐号、密码、会员、授权、登录、注册、充值、绑定、改密、到期、过期、`P_UserLogin`、`zhucema`、`Cloud`、`xx.dll` 的行。
- 每个导入项的来源路径、目标路径、类型、目标模块、优先级、状态、哈希记录在 `data/imported/index.json`。

## 可重复执行导入命令

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- legacy-import
```

可选参数：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --no-build --project .\tools\TlbbAgent.Diagnostics\TlbbAgent.Diagnostics.csproj -- legacy-import <source-root> <imported-root>
```

默认来源：

```text
<LEGACY_DATA_ROOT>
```

默认目标：

```text
<REPO_ROOT>\data\imported
```

## 导入位置

- 根目录：`data/imported/`
- 索引：`data/imported/index.json`

## 当前导入结果

本次诊断输出：

```text
Legacy import diagnostics
Source root=<LEGACY_DATA_ROOT>
Imported root=<REPO_ROOT>\data\imported
Total items=30
Copied items=29
Sanitized items=6
Missing items=0
Directory items=2
Index path=<REPO_ROOT>\data\imported\index.json
Index exists=True
Auth hits=0
```

## 导入分类

| 分类 | 内容 | 目标用途 |
|---|---|---|
| UU 字库 | `uu/aj.txt` | `TemplateOcrService` 核心点阵 OCR |
| OCR 字库 | `ocr/zk_*.txt`、`ocr/001_*.txt`、`ocr/数字*.txt` | 字库解析、数字识别、目标/NPC/状态识别 |
| 副本字库 | `ocr/fuben_生活王1.4.0_A窗口.txt`、`ocr/fuben_生活王1.4.0_B窗口.txt` | 副本/NPC 识别 |
| 坐标路线 | `routes/自定义坐标_马2.31.ini` | 挂机返点、马贼、反贼、和尚 |
| 采集坐标 | `routes/生活王1.4.0_A窗口_挖矿采药自填坐标/`、`routes/生活王1.4.0_B窗口_挖矿采药自填坐标/` | 挖矿、采药、采集 |
| 生活配置 | `life/pz_生活王1.4.0_A窗口.ini` | 采集映射、技能间隔、生活模块 schema |
| 脱敏配置 | `legacy-config/*.sanitized.ini` | `ProfileConfigService` schema 参考 |
| 旧文本样本 | `legacy-text/*.txt` | 清包、背包处理、师门物品处理 |
| 题库样本 | `question-bank/bmp.db.enc` | 自有题库后续研究，当前不解密 |

## 跳过项

| 来源 | 原因 |
|---|---|
| `<LEGACY_DATA_ROOT>\跑商OL\5.txt` | 内容仅引用 `xx.dll`，无可复用路线信息，状态记录为 `skipped-auth-reference` |
| `xx.dll` | 旧授权链，不引入 |
| 旧 EXE | 新项目不是 patch/包装旧 EXE |
| `账号.txt` | 旧账号/登录/授权数据，不引入 |

## OCR 导入验证

```text
Dictionary files: 15
Loaded glyphs: 36432
Renderable glyphs: 36432
Synthetic OCR dark: pass=True
Synthetic OCR bright: pass=True
Synthetic OCR format 001: pass=True
Synthetic OCR format aj/zk: pass=True
```

## 后续工作

1. 用真实 32 位游戏截图校准 OCR 区域。
2. P0 模块接入时必须从 `index.json` 读取或记录对应来源路径。
3. P1 跑商需要继续分析 EXE 行为或寻找更多蓝钻自有路线样本；当前没有可直接迁移的文本路线。
