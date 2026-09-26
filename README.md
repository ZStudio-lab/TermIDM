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

Each build increments the patch number. The self-contained WinUI app and native engine are written to `release/TermIDM-v<version>-Windows-x64/` and packaged as `release/TermIDM-v<version>-Windows-x64.zip`; `release/TermIDM-Windows-x64.zip` is the stable “latest” asset. Run `TermIDM.exe` directly from the versioned folder or extract the ZIP. Pass `-NoVersionIncrement` only for a local rebuild of the current version. Build outputs are ignored by Git.

To create a per-user Windows installer with Start Menu and optional desktop shortcuts, install Inno Setup 6 and run:

```powershell
./build-installer.ps1
```

The installer uses the TermIDM icon, installs under the current user's LocalAppData, and does not request administrator rights. It is written to `release/TermIDM-Setup-v<version>-Windows-x64.exe`; `release/TermIDM-Setup-Windows-x64.exe` is the stable latest build. The release automation uploads both installer names alongside the ZIPs.

## GitHub release automation

Create a GitHub repository and configure it as `origin`. Sign in with GitHub CLI (`gh auth login`), check out the release branch, and run:

```powershell
./deploy-release.ps1
```

The script verifies GitHub CLI authentication and `origin` before changing the version. It builds the release, increments the patch number, commits source changes, adds an annotated `v<version>` tag, pushes both, and uploads the versioned and stable ZIP assets to a GitHub Release. If no origin or GitHub CLI authentication is configured, it exits before building.

## Device-bound activation

The app displays a SHA-256 device ID at first launch. It accepts either a server-signed `Pass-User_...` activation key or the website's `TIDM-ENC-v1` encrypted string tagged with that device ID. Accepted tokens are protected at rest with Windows DPAPI. The encrypted string is a local convenience format, not an issuer signature; the signed-key flow uses the separate issuer API. The website does not contain the signing private key.

The public verification key lives in `TermIDM.Desktop/license-public-key.pem`. The corresponding private signing key must remain in a secret manager on the API host. A development key was generated outside this repository at `%LOCALAPPDATA%\TermIDM\LicenseAuthority\license-authority-private.pem`; do not commit, publish, or copy this file into a website. The root `render.yaml` and `LicenseIssuer/Dockerfile` define a Render deployment. Create a Blueprint from this repository and provide the `TERMIDM_LICENSE_PRIVATE_KEY_PEM` and `TERMIDM_LICENSE_ENCRYPTION_PRIVATE_KEY_PEM` PEM values as dashboard secrets. The encryption private key is stored locally at `%LOCALAPPDATA%\TermIDM\LicenseAuthority\license-encryption-private.pem`; its matching public key is embedded in `index.html`. Keep `TERMIDM_LICENSE_ALLOWED_ORIGIN` set to `https://zstudio-lab.github.io`. The `/health` endpoint reports ready only when both keys are valid. After the service is live, put its HTTPS `/api/licenses` URL in the `license-api-endpoint` meta tag in `index.html` and redeploy the Pages site.

For a new signing key, generate a P-256 keypair using a protected secret store, replace the public key file in the desktop app, rebuild/release the app, then put the private key only in the issuer's secret manager. Replacing the key invalidates activations signed by the old key unless the verifier is deliberately updated to trust both keys during a migration. Configure the website's `license-api-endpoint` meta value to the issuer's HTTPS `/api/licenses` URL after deployment.

This issuer provides device binding and signed token integrity; a public, unauthenticated form does not establish paid entitlement or identity verification. If commercial entitlements are required, add server-side approval/issuance policy before exposing the API publicly.

## Publish the landing page

Publish the repository root with GitHub Pages. The download CTA points to the stable GitHub release asset. The license form stays safely disabled until `license-api-endpoint` is set to the deployed issuer URL. The Render free service may spin down while idle, so its first request after inactivity can take longer; use an always-on plan for consistently immediate activation responses.

## License

MIT. See [LICENSE](LICENSE).
