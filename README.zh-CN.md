# NfsSharp

[![CI](https://github.com/MattGRP/NfsSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/MattGRP/NfsSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/NfsSharp.Client.svg)](https://www.nuget.org/packages/NfsSharp.Client)
[![License](https://img.shields.io/github/license/MattGRP/NfsSharp.svg)](LICENSE)

[English](README.md)

NfsSharp 是一个面向 .NET 的托管 NFS 客户端 SDK。它提供异步 API，可用于发现导出目录、挂载导出、浏览目录、读写文件、管理链接和属性，以及查询文件系统能力，无需调用本机 NFS 命令行工具。

高层 `NfsClient` facade 当前支持基于 TCP 的 NFSv3。较低层的 `NfsV4Client` 提供实验性的 NFSv4.0、NFSv4.1 和 NFSv4.2 COMPOUND API，但目前还没有达到与 NFSv3 客户端相同的兼容性和集成验证水平。

## NuGet 包

| 包 | 用途 |
| --- | --- |
| [`NfsSharp.Client`](https://www.nuget.org/packages/NfsSharp.Client) | 高层 NFSv3 客户端，以及实验性的直接 NFSv4 COMPOUND API。 |
| [`NfsSharp.Protocol`](https://www.nuget.org/packages/NfsSharp.Protocol) | XDR 基础类型、NFSv3/NFSv4 模型、状态码和 RPCSEC_GSS 抽象。 |

大多数应用只需要安装 `NfsSharp.Client`，它会传递引用 `NfsSharp.Protocol`。

## 环境要求

- .NET 8、.NET 9 或 .NET 10。
- 可通过 TCP 访问的 NFSv3 服务端。
- 能访问 portmapper/rpcbind、mountd 和 NFS 服务。
- 服务端允许配置的 AUTH_SYS 用户身份访问。
- 部分服务端要求客户端使用特权源端口。该选项默认开启，进程可能需要更高权限。

当前包不面向 .NET Standard，因此不支持 .NET Framework。

## 安装

```powershell
dotnet add package NfsSharp.Client
```

只使用协议层时：

```powershell
dotnet add package NfsSharp.Protocol
```

## 快速开始

```csharp
using NfsSharp.Client;

await using var client = new NfsClientBuilder()
    .WithCredentials(userId: 1000, groupId: 1000)
    .WithPrivilegedSourcePort(false)
    .Build();

await client.ConnectAsync("nfs.example.internal");

var exports = await client.GetExportedDevicesAsync();
foreach (var export in exports)
{
    Console.WriteLine(export.Path);
}

await client.MountDeviceAsync("/srv/data");

var entries = await client.GetItemListAsync(".");
foreach (var entry in entries)
{
    Console.WriteLine(entry.Name);
}

await using var destination = File.Create("download.bin");
await client.ReadAsync("backups/latest.bin", destination);

await client.UnMountDeviceAsync();
```

## 已支持能力

- 导出目录发现、挂载和卸载。
- 路径查询和属性查询。
- READDIR 和 READDIRPLUS 目录读取。
- 流式与偏移量文件读写。
- 文件和目录创建、删除与重命名。
- 符号链接和硬链接。
- 权限、所有者、时间戳和文件大小更新。
- ACCESS、READLINK、COMMIT、FSSTAT、FSINFO 和 PATHCONF。
- 重试、超时、Socket 参数、目录缓存、日志和 AUTH_SYS 身份配置。
- RPCSEC_GSS 扩展点和托管协商抽象。
- 通过 `NfsV4Client` 提供实验性的 NFSv4.0、NFSv4.1 和 NFSv4.2 COMPOUND 操作。

## 当前范围

- 基于 TCP 的 NFSv3 是当前主要支持的协议，也是高层 facade 唯一暴露的协议。
- 尚未实现 NFSv2。
- NFSv4 API 仍处于实验阶段，尚缺少专门的自动化测试和多服务端集成验证。
- 端到端行为取决于服务端导出策略、身份映射、防火墙、rpcbind 和 mountd 配置。
- 自动化测试目前主要覆盖协议编码和本地行为；针对多种 NFS 服务端的集成测试仍属于后续路线图。

## 构建与测试

```powershell
dotnet restore NfsSharp.sln
dotnet build NfsSharp.sln --configuration Release --no-restore
dotnet test NfsSharp.sln --configuration Release --no-build --no-restore
```

## 持续集成与发布

- `.github/workflows/ci.yml` 在 push 和 pull request 时执行 restore、build、test、pack，并上传包产物。
- `.github/workflows/release-nuget.yml` 使用 NuGet Trusted Publishing 发布包。
- `v1.0.0` 这样的 Git tag 会生成版本为 `1.0.0` 的 NuGet 包。

## 参与贡献

请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 安全问题

请按照 [SECURITY.md](SECURITY.md) 私下报告安全漏洞。

## 许可证

NfsSharp 使用 [MIT License](LICENSE)。
