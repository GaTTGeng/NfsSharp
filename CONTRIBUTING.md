# Contributing

Thank you for contributing to NfsSharp.

## Development Setup

Install the .NET 8, .NET 9, and .NET 10 SDKs, then run:

```powershell
dotnet restore NfsSharp.sln
dotnet build NfsSharp.sln --configuration Release --no-restore
dotnet test NfsSharp.sln --configuration Release --no-build --no-restore
```

## Pull Requests

- Open an issue before large API or protocol changes.
- Keep changes focused and include tests for behavioral fixes.
- Preserve nullable annotations and asynchronous cancellation support.
- Update the README or changelog when public behavior changes.
- Do not commit `bin`, `obj`, IDE metadata, test results, or package artifacts.

## Protocol Changes

NFS and RPC behavior should be grounded in the relevant protocol specification. Include focused tests for XDR encoding, response parsing, status handling, and error paths. Integration changes should document the NFS server implementation used for verification.

## Commit and Release Policy

Use clear commit messages. Maintainers publish versioned releases through `.github/workflows/release-nuget.yml`; contributors should not add package credentials or API keys to the repository.

By participating in this project, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
