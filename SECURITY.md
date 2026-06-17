# Security Policy

## Supported Versions

Security fixes are applied to the latest released version. Older versions may require upgrading.

## Reporting a Vulnerability

Do not open a public issue for a suspected vulnerability.

Use GitHub private vulnerability reporting for the repository when available. Otherwise, contact the repository owner privately through their GitHub profile and include:

- The affected package and version.
- A minimal reproduction or proof of concept.
- The expected impact.
- Any suggested mitigation.

Please allow maintainers reasonable time to investigate before public disclosure.

## Credential Safety

NfsSharp does not require repository-stored NuGet API keys. Releases use NuGet Trusted Publishing and GitHub Actions OIDC. Never commit NFS credentials, Kerberos material, NuGet keys, or private server addresses.
