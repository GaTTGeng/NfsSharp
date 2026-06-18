# NfsSharp

[![CI](https://github.com/GaTTGeng/NfsSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/GaTTGeng/NfsSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/NfsSharp.Client.svg)](https://www.nuget.org/packages/NfsSharp.Client)
[![NuGet Downloads](https://img.shields.io/nuget/dt/NfsSharp.Client.svg)](https://www.nuget.org/packages/NfsSharp.Client)
[![License](https://img.shields.io/github/license/GaTTGeng/NfsSharp.svg)](LICENSE)

[简体中文](README.zh-CN.md)

NfsSharp is a managed NFS client SDK for .NET. It provides asynchronous APIs for discovering exports, mounting an export, browsing directories, reading and writing files, managing links and attributes, and querying file-system capabilities without invoking native NFS command-line tools.

The high-level `NfsClient` facade currently supports NFSv3 over TCP. A lower-level `NfsV4Client` provides experimental NFSv4.0, NFSv4.1, and NFSv4.2 COMPOUND APIs; that surface is not yet covered by the same compatibility and integration guarantees as the NFSv3 client.

## Packages

| Package | Purpose |
| --- | --- |
| [`NfsSharp.Client`](https://www.nuget.org/packages/NfsSharp.Client) | High-level NFSv3 client plus experimental direct NFSv4 COMPOUND APIs. |
| [`NfsSharp.Protocol`](https://www.nuget.org/packages/NfsSharp.Protocol) | XDR primitives, NFSv3/NFSv4 models, status codes, and RPCSEC_GSS abstractions. |

Most applications should install `NfsSharp.Client`; it brings in `NfsSharp.Protocol` transitively.

## Requirements

- .NET 8, .NET 9, or .NET 10.
- An NFSv3 server reachable over TCP.
- Access to portmapper/rpcbind, mountd, and the NFS service.
- Server permissions matching the configured AUTH_SYS identity.
- A privileged source port may be required by some servers. It is enabled by default and may require elevated process permissions.

.NET Framework is not supported because the packages do not currently target .NET Standard.

## Installation

```powershell
dotnet add package NfsSharp.Client
```

For protocol-only scenarios:

```powershell
dotnet add package NfsSharp.Protocol
```

## Quick Start

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

## Direct NFSv3 Client

Applications that prefer a direct mounted client can use `NfsV3Client`:

```csharp
using NfsSharp.Client;
using NfsSharp.Protocol;

var options = new NfsClientOptions
{
    UserId = 1000,
    GroupId = 1000,
    UsePrivilegedSourcePort = false,
    CommandTimeout = TimeSpan.FromSeconds(30)
};

await using var client = await NfsV3Client.ConnectAsync(
    "nfs.example.internal",
    "/srv/data",
    options,
    CancellationToken.None);

var attributes = await client.GetAttributesAsync("documents/report.pdf", CancellationToken.None);
Console.WriteLine($"{attributes.Size} bytes");
```

## Supported Operations

- Export discovery and mount/unmount.
- Path lookup and attribute queries.
- Directory listing through READDIR and READDIRPLUS.
- Streaming and offset-based file reads and writes.
- File and directory creation and deletion.
- Rename, symbolic links, and hard links.
- Permission, ownership, timestamp, and file-size updates.
- ACCESS, READLINK, COMMIT, FSSTAT, FSINFO, and PATHCONF.
- Configurable retries, timeouts, socket options, directory caching, logging, and AUTH_SYS identity.
- RPCSEC_GSS extension points, including a managed negotiation abstraction.
- Experimental NFSv4.0, NFSv4.1, and NFSv4.2 COMPOUND operations through `NfsV4Client`.

## Current Scope

- NFSv3 over TCP is the primary supported protocol and the only protocol exposed by the high-level facade.
- NFSv2 is not implemented.
- NFSv4 APIs are experimental and currently lack dedicated automated and multi-server integration coverage.
- End-to-end behavior depends on the server's export policy, identity mapping, firewall, rpcbind, and mountd configuration.
- The automated test suite primarily covers protocol encoding and local behavior. Integration tests against multiple NFS server implementations remain a roadmap item.

## Build

```powershell
dotnet restore NfsSharp.sln
dotnet build NfsSharp.sln --configuration Release --no-restore
dotnet test NfsSharp.sln --configuration Release --no-build --no-restore
```

## Pack

```powershell
dotnet pack NfsSharp.sln --configuration Release --no-build --output artifacts/packages
```

NuGet metadata is defined in `src/Directory.Build.props`. Package artifacts include this README, SourceLink information, and symbol packages.

## Continuous Integration and Release

- `.github/workflows/ci.yml` restores, builds, tests, packs, and uploads package artifacts for pushes and pull requests.
- `.github/workflows/release-nuget.yml` builds versioned packages and publishes them to NuGet.org using Trusted Publishing.
- A tag such as `v1.0.0` resolves to NuGet package version `1.0.0`.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Security

Please report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Support

See [SUPPORT.md](SUPPORT.md) for usage questions and bug reports.

## License

NfsSharp is licensed under the [MIT License](LICENSE).
