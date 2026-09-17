# Third-party notices

This application reuses the following projects. Their names and trademarks belong to their respective owners. YouTube and YouTube Music are Google trademarks; this client is independent and is not endorsed by Google.

| Component | License / terms | Source |
| --- | --- | --- |
| youtube-music-core 0.9.1 | MIT; bundled dependencies have their own notices | https://github.com/meurz/youtube-music-core/tree/v0.9.1 |
| Windows App SDK / WinUI | Microsoft Windows App SDK NuGet license terms; source components under MIT | https://github.com/microsoft/WindowsAppSDK |
| WinUIEx | MIT | https://github.com/dotMorten/WinUIEx |
| Windows Community Toolkit | MIT | https://github.com/CommunityToolkit/Windows |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| .NET runtime, libraries and ProtectedData | MIT; runtime third-party notices apply | https://github.com/dotnet/runtime |

Source licenses and notices for the pinned Rust core are available in `external/core/LICENSE`, `external/core/THIRD_PARTY_NOTICES.md`, and its `vendor` directory. Published desktop packages include those notices under `licenses/core`, plus the direct packages' license texts in `licenses` and the runtime's distributed notices where supplied by its publish targets. NuGet package licenses are available on each package page linked from `docs/dependency-research.md`.

## Native proof-of-origin subprocess

`tools/po-provider` is a separate **GPL-3.0-only** component that reuses
bgutil-ytdlp-pot-provider 2.0.0 (commit `37169ee2656e08c5c2e5dc9df4c598c0cb4c88a8`).
The desktop application's MIT license does not replace its license. Distributed
worker folders include the upstream source, worker/build source, lockfile and
GPL license, together with dependency licenses and Node.js's license.

The worker uses BgUtils 4.0.3 (MIT), JSDOM 29.1.1 (MIT),
@napi-rs/canvas 1.0.9 (MIT, with native third-party components), and Node.js
22.22.3 (MIT with its distributed third-party notices). See
[the worker documentation](tools/po-provider/README.md) and its
[upstream attribution](tools/po-provider/vendor/bgutil/UPSTREAM.md).

## Playback proof subprocess

The portable archive also contains the separately licensed `po-provider` program.
The MIT desktop application communicates with it through a private JSON pipe.
The helper reuses [bgutil-ytdlp-pot-provider 2.0.0](https://github.com/Brainicism/bgutil-ytdlp-pot-provider/tree/2.0.0) under GPL-3.0-only, with BgUtils, YouTube.js, JSDOM, and native canvas dependencies. Its GPL license, upstream source, build instructions, pinned dependency lockfile, and dependency notices accompany the program in `po-provider`. Node.js is distributed with `po-provider/NODE-LICENSE`. See [the helper documentation](tools/po-provider/README.md) for its protocol and reproducible build.
