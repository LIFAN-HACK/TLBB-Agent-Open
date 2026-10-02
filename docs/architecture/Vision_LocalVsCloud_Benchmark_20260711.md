# 视觉模型本地与云端对比测试

日期：2026-07-11  
工具：`tools/TlbbAgent.VisionDeploymentHarness`  
样本：V2 数据集 `pet_000013.png`（2560×1369）  
模型：R6，SHA256 `4321d65b3047d26f07db88821f07dd066f0c7914cef5415544d555cb849aa53b`

测试机：Intel Core i5-12400F（6 核 12 线程），31.8 GiB 内存；本轮使用 CPU 推理。

## 1. 测试口径

- 本地：原始 BGRX 截图直接执行相同 L1 + L2。
- 云端等价链：截图编码 -> 签名 HTTP 上传 -> 独立服务解码 -> 相同 L1 + L2 -> 签名 JSON 返回 -> 本地安全门。
- 回环测试只测协议、编码和同机服务开销，不冒充真实公网或云 GPU 数据。
- 公网投影 = 编码 p50 + 上传耗时 + RTT + 服务端解码/推理 p50；未加入排队、TLS 握手和丢包重传，因此属于偏乐观估计。
- 同机 CPU 负载波动明显，不应跨轮次直接比较本地绝对耗时；应关注同轮端到端结果、传输体积和输出一致性。

## 2. PNG 无损结果

| 项目 | 实测 |
|---|---:|
| 本地总链 p50 | 575.8 ms |
| PNG 编码 p50 | 357.4 ms |
| 上传体积 | 5.90 MiB/帧 |
| 服务端解码 p50 | 61.0 ms |
| 服务端推理 p50 | 586.6 ms |
| 回环完整端到端 p50 | 1023.9 ms |
| 返回 JSON | 3.9 KiB |
| 输出一致性 | 完全一致 |

乐观公网投影：

| 上行带宽 / RTT | 预计端到端 |
|---|---:|
| 20 Mbps / 20 ms | 3498.8 ms |
| 50 Mbps / 20 ms | 2014.5 ms |
| 100 Mbps / 20 ms | 1519.7 ms |

结论：全帧 PNG 不适合 150～350 ms 级战斗感知循环。

## 3. JPEG 85 结果

| 项目 | 实测 |
|---|---:|
| JPEG 编码 p50 | 23.4 ms |
| 上传体积 | 954.5 KiB/帧 |
| 服务端解码 p50 | 36.0 ms |
| 服务端推理 p50 | 789.4 ms |
| 回环完整端到端 p50 | 875.0 ms |
| 输出一致性 | 运行语义一致，非像素级一致 |

观察到的差异：

- `player_status_panel` 框轻微漂移，但在 3 px 容差内。
- 人物 HP `99% -> 98%`。
- 人物 MP `99% -> 98%`。
- 宠物状态、技能槽、小地图状态和坐标保持一致。

乐观公网投影：

| 上行带宽 / RTT | 预计端到端 |
|---|---:|
| 20 Mbps / 20 ms | 1259.7 ms |
| 50 Mbps / 20 ms | 1025.2 ms |
| 100 Mbps / 20 ms | 947.0 ms |

结论：JPEG 显著降低带宽，但单样本不足以批准生产使用；必须做多分辨率、多血量阈值和技能 CD 批量回归，并给阈值判断保留滞回区间。

## 4. 安全测试

以下拒绝路径均已自动化通过：

- 有效签名请求与响应通过。
- 上传内容被篡改时拒绝。
- nonce 重放时拒绝。
- 过期请求时间戳拒绝。
- 过期点击建议拒绝。
- 客户区越界点击拒绝。
- 云端模型哈希与本地允许值不一致时拒绝。

工具不会向真实游戏窗口发送点击；动作安全测试使用离线坐标建议。

## 5. 复测命令

本机回环 PNG：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run `
  --project 'tools\TlbbAgent.VisionDeploymentHarness\TlbbAgent.VisionDeploymentHarness.csproj' `
  -c Release -- `
  --iterations 5 --encoding png
```

本机回环 JPEG：

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run `
  --project 'tools\TlbbAgent.VisionDeploymentHarness\TlbbAgent.VisionDeploymentHarness.csproj' `
  -c Release -- `
  --iterations 5 --encoding jpeg --jpeg-quality 85
```

云主机启动服务：

```powershell
$env:TLBB_VISION_CLOUD_SECRET = '<至少 32 字节的随机密钥>'
& 'C:\Program Files\dotnet\dotnet.exe' run `
  --project 'tools\TlbbAgent.VisionDeploymentHarness\TlbbAgent.VisionDeploymentHarness.csproj' `
  -c Release -- `
  --serve --listen 'http://127.0.0.1:5088'
```

该 HTTP 监听地址应放在 TLS 反向代理后，不应直接暴露公网。

从客户端测试真实云端：

```powershell
$env:TLBB_VISION_CLOUD_SECRET = '<与服务端一致的内测密钥>'
& 'C:\Program Files\dotnet\dotnet.exe' run `
  --project 'tools\TlbbAgent.VisionDeploymentHarness\TlbbAgent.VisionDeploymentHarness.csproj' `
  -c Release -- `
  --endpoint 'https://vision.example.com' `
  --iterations 20 --encoding jpeg --jpeg-quality 85
```

## 6. 下一轮

1. 用真实云主机 CPU/GPU 地址运行相同工具，记录服务器硬件、地区、并发数、p50/p95/p99。
2. 加入本地 L1 后仅上传人物、宠物、技能栏和小地图裁剪的混合模式。
3. 批量运行不同分辨率和血量临界点样本，决定 JPEG 质量与阈值滞回。
4. 评估每窗口每秒流量、并发用户 GPU 成本和服务降级策略。
