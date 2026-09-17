# ytmusicwinui

A native Windows desktop client for YouTube Music, built with WinUI 3 and [youtube-music-core](https://github.com/meurz/youtube-music-core). It uses Fluent controls with a restrained gray-green theme, Community Toolkit settings cards and MVVM helpers, and WinUIEx window management.

This is an independent client, not an official Google application.

## Requirements

- Windows 10 version 2004 (build 19041) or later; Windows 11 recommended.
- x64 or ARM64 Windows, matching the downloaded package.
- An internet connection. Personal libraries require importing an active YouTube Music browser session.

## Run

Download the matching portable ZIP from [Releases](https://github.com/meurz/ytmusicwinui/releases), verify it against `SHA256SUMS`, extract the entire archive and open `ytmusicwinui.exe`. Keep its DLLs and resource files together. The package includes .NET and Windows App SDK; no separate runtime installation is required.

```powershell
Get-FileHash .\ytmusicwinui-win-x64.zip -Algorithm SHA256
```

The client provides native navigation, search, library and playlist browsing, a persistent player, queue and lyrics surfaces, and account/session settings. Feature availability also depends on the signed-in account and the core response. No browser is embedded for audio playback.

## Build

Install the .NET 9 SDK and the Windows SDK / Windows application development tools (Visual Studio 2022). Build from a local Windows drive rather than a WSL UNC path.

```powershell
git clone --recurse-submodules https://github.com/meurz/ytmusicwinui.git
cd ytmusicwinui
.\scripts\build.ps1 -Architecture x64 -Publish
.\artifacts\ytmusicwinui-win-x64\ytmusicwinui.exe
```

Use `-Architecture arm64` for Windows on ARM. The script downloads the pinned core release, checks its SHA-256 hash, restores NuGet packages, and creates a self-contained desktop folder. It does not compile Rust. To control NuGet cache placement, set `NUGET_PACKAGES` before building. Temporary native downloads stay in the repository's ignored `.cache` and `.native` directories.

CI builds both architectures and uploads portable ZIPs with checksums. Version tags matching the project version publish those archives as GitHub Releases.

## Playback and accounts

Audio uses the native Windows media player with the core's existing Windows source adapter. The initial host targets clear AAC/DASH playback. Core support for PO tokens or SABR does not imply that every restricted track can play in this host; progressive SABR demux is not integrated. There is no DRM or browser playback bridge.

Session import uses your existing YouTube Music cookies. Sessions are encrypted locally using Windows DPAPI for the current user. Cookie maintenance can preserve a valid session; an expired or revoked session can require a new import. Never commit, share, or attach cookies or account session files to an issue.

Live signed-in playback and personal library acceptance require a valid account session. Automated builds do not authenticate a Google account and cannot establish account-specific playback coverage.

## Development

See [dependency research](docs/dependency-research.md) for the existing libraries and APIs used here, and [third-party notices](THIRD_PARTY_NOTICES.md) for licensing. The Rust core is pinned at v0.9.0 (C ABI 2 / JSON protocol 2.0).

Licensed under [MIT](LICENSE).
