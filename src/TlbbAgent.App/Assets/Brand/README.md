# TLBB Agent 品牌素材

本目录保存应用当前使用的品牌资源。界面品牌由《新天龙八部》官方字标、TLBB Agent 衍生应用图标和原创武侠主海报组成。

## 当前生效资源

- `official-xtl-primary-logo.png`：从《新天龙八部》官方页面 Logo 精确裁出的主体字标，用于应用标题栏。
- `tlbb-agent-logo-v2.png`：以官方字标为视觉基因重新构图的透明背景 TLBB Agent 徽标。
- `tlbb-agent-icon-source-v2.png`：为 Windows 小尺寸显示补足安全边距的 1024×1024 图标源图。
- `tlbb-agent-v2.ico`：包含 16、24、32、48、64、128、256 像素帧的 Windows 应用图标。
- `tlbb-agent-poster.png`：原创 16:9 武侠主视觉，用作工作区海报背景。

官方来源：

- 官方网站：<https://tl.changyou.com/>
- 官方页面使用的 Logo 素材：<https://i1.cy.com/xtl/main/2026/0409/img/logo.png>

## TLBB Agent 徽标生成记录

生成方式：Codex 内置图像生成能力，使用 `official-xtl-primary-logo.png` 作为唯一参考图；随后在本地移除纯绿色背景、校验透明通道并生成多尺寸 ICO。

最终提示词：

```text
Use case: logo-brand / precise-object-edit
Asset type: compact application logo derived from the official New Tian Long Ba Bu PC logo
Input image: Image 1 is the official game logo; keep its core red dragon and gold brush-calligraphy appearance recognizably intact.
Primary request: Recompose the official logo into a compact near-square crest for TLBB Agent. Keep the original Chinese wuxia wordmark character and red dragon sweep as the hero, simplify surrounding details, and add only a small cyan-blue Agent signal ring tucked behind the dragon so it feels like an official companion tool rather than an unrelated technology logo.
Style/medium: premium official-game companion app badge, crisp painted 2D logo, strong small-size contrast
Composition/framing: centered near-square crest, balanced negative space, readable silhouette at 32px and 128px
Scene/backdrop: perfectly flat solid #00ff00 chroma-key background for local removal
Constraints: preserve the reference logo's visual identity; no eye, no sword, no extra English words, no futuristic circuit clutter, no character portrait, no watermark; do not use green in the subject; the entire background must be one uniform #00ff00 with no shadows, gradients, texture, reflection, or floor plane
```

## 主海报生成记录

```text
Use case: stylized-concept
Asset type: 16:9 desktop application hero background / main poster visual
Primary request: Create an original cinematic wuxia MMORPG-inspired key art background for TLBB Agent, suitable behind a dark AI automation dashboard.
Scene/backdrop: vast ancient Chinese mountain valley above a sea of clouds, distant cliffside temple roofs and a subtle old city silhouette, drifting mist and restrained energy trails suggesting visual perception and intelligent guidance
Subject: one small original lone martial artist silhouette on a distant ridge, facing the landscape; no recognizable licensed character
Style/medium: premium cinematic game key art, painterly realism, sophisticated and atmospheric rather than busy
Composition/framing: ultra-wide 16:9; strong depth; keep the center and left-middle relatively calm and dark for UI readability; visual emphasis toward the far right and upper edges; no hard focal object behind central content
Lighting/mood: moonlit blue-hour atmosphere with soft volumetric light, calm, mysterious, capable
Color palette: deep navy, ink blue, cool cyan haze, restrained warm-gold highlights matching a modern dark dashboard
Constraints: no text, no logos, no UI, no watermark, no trademarked characters, no copied game locations, no excessive particles, no bright white center, no crowded foreground
```

应用通过深色渐变遮罩维持文字对比度，海报不参与点击和业务交互。

## 权利说明

《新天龙八部》名称、官方字标及相关商标权利归其权利人所有。当前字标与衍生徽标仅作为项目开发中的产品视觉方案；若面向公众发布、宣传或商业分发，应先确认已获得相应品牌与商标授权。原创海报不包含官方角色或原作场景复刻。
