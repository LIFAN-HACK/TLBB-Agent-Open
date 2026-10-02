# TlbbAgent 云端服务

该目录是与 Windows 桌面客户端隔离的 .NET 10 模块化单体。当前骨架提供六个明确的业务边界、进程健康检查、模块完整性检查和集成测试，不包含临时账号、伪支付、默认密钥或演示数据库。

## 模块边界

- Identity：账号、认证、会话和安全策略。
- Billing：商品、订单、支付、退款和订阅账务。
- Entitlement：从订单或运营授权推导用户有效权益。
- Licensing：设备注册、授权租约和模型密钥授予。
- Release：客户端与模型版本、渠道、灰度和撤回。
- Audit：安全、财务和管理员操作审计。

模块通过 API 主机显式组合。后续持久化采用同一 PostgreSQL 实例内的独立 schema；模块不得直接查询其他模块的数据表，只能通过公开契约或可靠消息协作。

## 本地验证

从 server 目录运行：

    dotnet restore TlbbAgent.Server.sln
    dotnet build TlbbAgent.Server.sln --no-restore
    dotnet test TlbbAgent.Server.sln --no-build

本机若未全局安装 .NET 10，可使用官方 dotnet-install 脚本安装到用户目录。不要把 SDK、连接串、证书或环境密钥提交到本目录。

## 运行

    dotnet run --project src/TlbbAgent.Server.Api

默认开发端口由 launchSettings.json 设置为 5080。容器固定监听 8080。

- GET /health/live：仅确认进程可以响应。
- GET /health/ready：确认所有必需模块均已加载且名称唯一。

健康响应只公开总体状态，不输出依赖地址、异常或内部配置。

## 容器构建

构建上下文必须是 server 目录：

    docker build -f src/TlbbAgent.Server.Api/Dockerfile -t tlbbagent-server:dev .

运行镜像使用非 root 用户，且不额外安装 curl 或 wget。容器健康探测由反向代理或外部监控请求 /health/ready 完成。

## 秘密管理

仓库只保存无秘密的默认配置。开发环境使用 .NET User Secrets 或进程环境变量；测试和生产环境使用部署平台的 Secret/KMS。任何真实支付密钥、JWT/数据保护密钥、数据库口令、对象存储凭据和签名私钥均不得写入 Git。
