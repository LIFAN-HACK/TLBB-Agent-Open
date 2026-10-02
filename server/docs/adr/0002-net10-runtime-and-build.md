# ADR 0002：服务端独立使用 .NET 10

- 状态：已接受
- 日期：2026-07-10

## 背景

现有桌面客户端使用 net8.0-windows，仓库根目录的构建属性包含 Windows、x64 和客户端版本信息。云端需要 Linux 容器、长期支持版本和独立演进节奏。

## 决策

server 目录目标框架统一为 net10.0，并使用自己的 Directory.Build.props、Directory.Packages.props 和 global.json，不修改或引用桌面客户端项目。

global.json 以 10.0.100 为最低特性带并允许滚动到已安装的最新 10.0 特性带。CI 和容器使用 .NET 10；开发机可通过官方 dotnet-install 安装用户级 SDK。

## 影响

- 桌面端可以继续使用现有 SDK 8，服务端不会继承其 Windows/x64 设置。
- 在 server 目录外执行 dotnet 命令可能不会发现该 global.json；开发与 CI 命令必须把工作目录设为 server。
- 升级 SDK 或依赖版本时必须通过还原、编译和集成测试。
