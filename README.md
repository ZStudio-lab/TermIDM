# TermIDM

TermIDM is a WinUI 3 download manager for Windows 10 and 11, backed by a native C++ transfer engine. The desktop UI follows the supplied IDM-inspired reference: a dark dashboard, a download navigation rail, icon toolbar, and compact rows for file, status, size, progress, speed, and ETA.

## Repository layout

- `TermIDM.Desktop/` — WinUI 3 desktop application and device license verification.
- `main.cpp` — native download engine.
- `LicenseIssuer/` — small ASP.NET Core API that signs device-bound activation tokens.
- `index.html` — single-file dark landing page for GitHub Pages.
- `build.ps1` — local build/publish/package script. Does not change version unless `-IncrementVersion` is provided.
- `deploy-release.ps1` — increments version, builds, stages/commits, tags, pushes, and creates a GitHub release.
- `VERSION` — semantic version source of truth.

## Build a Windows release

Install .NET 8 SDK, MinGW-w64 (`g++` and `windres`) and the libcurl MinGW development package described by `build.ps1`. Then run:

```powershell
./build.ps1
```

Each build increments the patch number. The self-contained WinUI app and native engine are packaged in `release/TermIDM-v<version>-Windows-x64.zip`; `release/TermIDM-Windows-x64.zip` is the stable “latest” asset. Pass `-NoVersionIncrement` only for a local rebuild of the current version. Build outputs are ignored by Git.

## GitHub release automation

Create a GitHub repository and configure it as `origin`. Sign in with GitHub CLI (`gh auth login`), check out the release branch, and run:

```powershell
./deploy-release.ps1
```

The script verifies GitHub CLI authentication and `origin` before changing the version. It builds the release, increments the patch number, commits source changes, adds an annotated `v<version>` tag, pushes both, and uploads the versioned and stable ZIP assets to a GitHub Release. If no origin or GitHub CLI authentication is configured, it exits before building.

## Device-bound activation

The app displays a SHA-256 device ID at first launch. It fingerprints the Windows `MachineGuid`, verifies an ECDSA P-256 signature embedded in the key, and binds the token to that ID. Accepted tokens are protected at rest with Windows DPAPI. The website requests a key from the separate issuer API; it never contains a signing private key. Phone is validated as a request field but is not placed in the activation token. This provides normal device binding, not a hardware-rooted anti-cloning guarantee: a manually cloned Windows image could duplicate its MachineGuid.

The public verification key lives in `TermIDM.Desktop/license-public-key.pem`. The corresponding private signing key must remain in a secret manager on the API host. A development key was generated outside this repository at `%LOCALAPPDATA%\TermIDM\LicenseAuthority\license-authority-private.pem`; do not commit, publish, or copy this file into a website. Configure the issuer host's secret `TERMIDM_LICENSE_PRIVATE_KEY_PEM` with its PEM value and configure `TERMIDM_LICENSE_ALLOWED_ORIGIN` to the exact HTTPS GitHub Pages origin. Deploy `LicenseIssuer/` to an HTTPS ASP.NET Core host before adding that endpoint to the `license-api-endpoint` meta tag in `index.html`.

For a new signing key, generate a P-256 keypair using a protected secret store, replace the public key file in the desktop app, rebuild/release the app, then put the private key only in the issuer's secret manager. Replacing the key invalidates activations signed by the old key unless the verifier is deliberately updated to trust both keys during a migration. Configure the website's `license-api-endpoint` meta value to the issuer's HTTPS `/api/licenses` URL after deployment.

This issuer provides device binding and signed token integrity; a public, unauthenticated form does not establish paid entitlement or identity verification. If commercial entitlements are required, add server-side approval/issuance policy before exposing the API publicly.

## Publish the landing page

Publish the repository root with GitHub Pages. The download CTA points to the stable GitHub release asset. The license form stays safely disabled until `license-api-endpoint` is set to the deployed issuer URL.

## License

MIT. See [LICENSE](LICENSE).
