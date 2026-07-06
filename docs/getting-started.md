# Getting Started

NfsSharp provides managed .NET APIs for NFSv3 export discovery, mounting, directory traversal, file I/O, metadata, and file-system capability queries without invoking native NFS command-line tools.

## Install

```powershell
dotnet add package NfsSharp.Client
```

Use the protocol package directly when you only need XDR, ONC RPC, NFS model types, status codes, or RPCSEC_GSS abstractions:

```powershell
dotnet add package NfsSharp.Protocol
```

## Connect and list exports

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
```

`WithPrivilegedSourcePort(false)` is useful for development environments and containerized test servers. Some production NFS servers require privileged source ports, which may require elevated process permissions.

## Mount an export and read a file

```csharp
using NfsSharp.Client;

await using var client = new NfsClientBuilder()
    .WithCredentials(userId: 1000, groupId: 1000)
    .WithPrivilegedSourcePort(false)
    .Build();

await client.ConnectAsync("nfs.example.internal");
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

## Use the direct NFSv3 client

Applications that need direct mounted-client APIs can use `NfsV3Client`:

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

## Inspect write and commit results

Use result-returning APIs when your application needs NFSv3 persistence details such as the committed stability mode or write verifier:

```csharp
using NfsSharp.Client;
using NfsSharp.Protocol;

var options = new NfsClientOptions
{
    UserId = 1000,
    GroupId = 1000,
    UsePrivilegedSourcePort = false,
    StableHow = NfsWriteStableHow.FileSync
};

await using var client = await NfsV3Client.ConnectAsync(
    "nfs.example.internal",
    "/srv/data",
    options,
    CancellationToken.None);

var file = await client.CreateAndOpenFileAsync(
    "uploads/report.bin",
    null,
    CancellationToken.None);

var write = await client.WriteAtWithResultAsync(
    file.Handle,
    offset: 0,
    "hello"u8.ToArray(),
    CancellationToken.None);

Console.WriteLine($"Wrote {write.Count} bytes with {write.Committed} stability.");

var commit = await client.CommitWithResultAsync(
    file.Handle,
    offset: 0,
    count: (uint)write.Count,
    CancellationToken.None);

Console.WriteLine(Convert.ToHexString(commit.WriteVerifier));
```

## Guard attribute updates

For optimistic metadata updates, read the current `CtimeTimestamp` and pass it to `SetAttributesGuardedAsync`. The server rejects the update with `NOT_SYNC` if the file changed after the attributes were read. `CtimeTimestamp` preserves the raw NFS nanosecond precision required by the guard.

```csharp
using NfsSharp.Client;
using NfsSharp.Protocol;

var attributes = await client.GetAttributesAsync("uploads/report.bin", CancellationToken.None);
if (attributes.CtimeTimestamp is not null)
{
    await client.SetAttributesGuardedAsync(
        "uploads/report.bin",
        new NfsSetAttributes { Mode = 0x180 },
        attributes.CtimeTimestamp.Value,
        CancellationToken.None);
}
```

## Verify compatibility

NFS behavior depends on the server implementation, export policy, identity mapping, firewall, rpcbind, and mountd configuration. Review the [NFS compatibility matrix](nfs-compatibility.md) before relying on a behavior in production.
