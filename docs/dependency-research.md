# Dependency research

Research date: 2026-09-17. Existing components and implementations were checked before building the desktop client. Versions below were confirmed by downloading the NuGet packages and inspecting their manifests; API names were checked against the packages' XML documentation.

| Requirement | Adopted implementation | Verified version | Why it fits |
| --- | --- | --- | --- |
| Native desktop controls and Fluent styling | [Windows App SDK / WinUI 3](https://learn.microsoft.com/windows/apps/winui/winui3/) | [1.8.260804001](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/1.8.260804001) | Native navigation, lists, sliders, dialogs, menus, title bars and resources. Keep the known 1.8 line for compatibility rather than migrating to a new major line. |
| Window sizing and state | [WinUIEx](https://dotmorten.github.io/WinUIEx/) | [2.9.3](https://www.nuget.org/packages/WinUIEx/2.9.3) | Reuses `WindowEx.Width`, `Height`, `MinWidth`, `MinHeight` and `PersistenceId`. The package targets WinUI 1.8. |
| Settings cards | [Windows Community Toolkit](https://learn.microsoft.com/dotnet/communitytoolkit/windows/settingscontrols/settingscard) | [8.2.251219](https://www.nuget.org/packages/CommunityToolkit.WinUI.Controls.SettingsControls/8.2.251219) | Reuses `CommunityToolkit.WinUI.Controls.SettingsCard`; includes the native WinUI target. |
| Observable state | [MVVM Toolkit](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) | [8.4.2](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2) | Reuses observable property and command infrastructure. |
| Local credential encryption | [.NET ProtectedData / Windows DPAPI](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata) | [8.0.0](https://www.nuget.org/packages/System.Security.Cryptography.ProtectedData/8.0.0) | Encrypts imported sessions for the current Windows user without custom cryptography. |
| Music Web API and native media resolution | [youtube-music-core](https://github.com/meurz/youtube-music-core) | [v0.9.1](https://github.com/meurz/youtube-music-core/releases/tag/v0.9.1), commit `105fe13d98980a6ba24c24a3a323016c7ad3a502` | Reuses the existing Rust core, shipped C ABI, managed client and Windows source adapter; no duplicate protocol implementation. |
| Localization and language selection | [Windows App SDK MRTCore](https://learn.microsoft.com/windows/apps/windows-app-sdk/mrtcore/mrtcore-overview) | Existing Windows App SDK 1.8 | Compiles `.resw` translations into the native PRI. `ResourceManager`, `ResourceContext.QualifierValues["Language"]` and `ResourceMap.TryGetValue` provide language selection for the C# interface without another framework. |
| Native audio, adaptive source, system media controls | [Windows.Media.Playback.MediaPlayer](https://learn.microsoft.com/uwp/api/windows.media.playback.mediaplayer), [AdaptiveMediaSource](https://learn.microsoft.com/uwp/api/windows.media.streaming.adaptive.adaptivemediasource), [SMTC](https://learn.microsoft.com/windows/uwp/audio-video-camera/integrate-with-systemmediatransportcontrols) | Windows SDK, minimum build 19041 | Native AAC/DASH playback and Windows media integration. |

## Scope of custom code

Application code composes these components into the music pages, applies the gray-green theme, maps core results to view models, and coordinates queues, playback and session persistence. The existing core owns requests, parsing, stream resolution and cookie maintenance. The host does not reimplement Google authorization, a browser player, decryption, or codec/demux libraries.

Native SABR needs a progressive demux adapter before it can feed the Windows player. The first desktop version uses the existing clear audio sources supported by the Windows adapter. Advanced core capabilities are not automatically desktop features.

## Reproducibility and licensing

Direct NuGet versions are pinned in the project. The core source is a pinned submodule; native binaries are downloaded from its matching release. The bootstrap script compares both the published checksum entry and the archive against a hard-coded SHA-256 hash before extracting the DLL. The build is self-contained for .NET and Windows App SDK and retains the WinUI resource index.

WinUIEx, Community Toolkit, .NET and the Rust core use MIT licenses. Windows App SDK binary distribution also carries Microsoft's package license terms. See [third-party notices](../THIRD_PARTY_NOTICES.md). The original manifests and source repositories remain the authoritative licensing records.

## Localization verification

The MRTCore APIs above were compiled on Windows against the pinned package before integration. WinUI3Localizer 2.3.0 was also confirmed on NuGet, but the existing MRTCore dependency covers explicit language lookup and restart-based selection for this C# interface. The app supports system language selection, Simplified Chinese, Traditional Chinese and English. Language preference is stored separately from account sessions. Compiled resource lookup must be checked in the published app because source `.resw` files alone do not establish PRI packaging correctness.

## Browser-free PO helper (2026-09-17)

The maintained [BgUtils provider](https://github.com/Brainicism/bgutil-ytdlp-pot-provider)
2.0.0 was evaluated before introducing another implementation. Its existing
homepage challenge, integrity-token and video-bound minter are reused in an
isolated GPL-3.0-only worker; the original source is vendored unchanged with its
license. The host communicates over private standard streams and retains the
same native Windows media player.

The provider's original canvas 3.2.3 release has a Windows x64 prebuild but no
Windows ARM64 prebuild. The existing @napi-rs/canvas 1.0.9 package supplies native
N-API binaries for both architectures and is tested through JSDOM before use.
Node.js 22.22.3 supplies official x64/ARM64 runtime ZIPs with pinned SHA-256 hashes.
Dependency versions and npm integrity hashes are recorded in the helper lockfile.

Anonymous research on `QoXDQa9L12A` reproduced the same AAC/Opus URL returning
403 for a range beyond 2 MiB without a proof and 206 for all requested bytes with
a freshly minted video-bound proof. Full AAC and Opus responses also reached their
reported content lengths. Tokens and signed URLs remained only in memory.
This establishes the need for a GVS proof in that sample; it does not establish
universal availability or replace signed-in account acceptance testing.

Production-service verification on Windows x64 recovered the sample's real HTTP
403 with one forced proof refresh. Native playback reached `MediaEnded` after
211.92 seconds, compared with 211.93 seconds remaining after sustained recovery.
Midpoint seek, pause, same-video proof reuse, rapid replacement, cancellation,
dispatcher notifications, and owned-worker disposal passed. An anonymous library
login requirement also left subsequent public playback usable. Actual WinUI
checks played Sicily, Gold Rush Town, and It Rained That Day, including seek and
automatic queue advancement. These anonymous checks do not cover personal accounts.

## Automatic prefetch (2026-09-17)

Reviewed the [Community Toolkit IncrementalLoadingCollection](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/windows/collections/incrementalloadingcollection)
and the existing [WinUI ScrollViewer ViewChanged API](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.scrollviewer.viewchanged?view=windows-app-sdk-1.8).
The collection component is designed for a ListView owning a single incremental
source. This application already composes multiple sections with independent
opaque cursors inside one native ScrollViewer, alongside a separate feed cursor.
It therefore retains the existing WinUI ListView/GridView and uses the outer
ScrollViewer's viewport and layout events to request each nearby cursor.
ObservableCollection appends results without rebuilding the page or resetting
selection. The shared host adapter coalesces events, serializes loads, cancels
with its view, and caps each failed cursor at three attempts. Local native
viewport checks cover prefetch distance, short-page filling, cancellation,
concurrency and bounded retries; no new CI checks are introduced.
