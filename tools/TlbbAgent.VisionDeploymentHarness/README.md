# Vision Deployment Harness

用于比较同一视觉模型的本地推理与 HTTP 远端推理，并验证协议签名、重放保护、模型固定和本地点击安全门。

默认直接启动本机回环服务并执行对比，不控制游戏窗口：

```powershell
dotnet run -c Release -- --iterations 5 --encoding png
```

支持参数：

- `--image <path>`：指定测试截图。
- `--vision-root <path>`：指定视觉模型工程目录。
- `--encoding png|jpeg`：上传编码。
- `--jpeg-quality <40-100>`：JPEG 质量。
- `--iterations <n>`：测试轮数。
- `--serve --listen <url>`：独立服务模式。
- `--endpoint <https-url>`：测试真实远端服务。

独立服务通过环境变量 `TLBB_VISION_CLOUD_SECRET` 读取内测签名密钥。非本机客户端只接受 HTTPS 地址。
