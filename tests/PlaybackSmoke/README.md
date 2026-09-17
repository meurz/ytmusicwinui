# Native playback check

This opt-in Windows executable compiles the real `PlaybackService` and models.
Its small `CoreService` adapter forwards to the real pinned Rust DLL through the
upstream interop client, with an anonymous configuration. It cannot read, restore,
or import the application's account. Volume is always zero.

After `scripts/bootstrap-core.ps1 -Architecture x64`:

```powershell
dotnet run --project tests/PlaybackSmoke -- --live
dotnet run --project tests/PlaybackSmoke -- --live --retry
dotnet run --project tests/PlaybackSmoke -- --live --opus
dotnet run --project tests/PlaybackSmoke -- --live --full
```

Without `--live`, it prints usage and makes no requests. `--video VIDEO_ID`
selects another public reference; the default is `dQw4w9WgXcQ`. The quick check
requires a real advancing playback clock, native seek, SMTC Playing/Paused,
five rapid source replacements, cancellation/draining, reuse, dispatcher-thread
notifications, and clean disposal. `--opus` deliberately makes the AAC request
unsupported in the forwarding adapter, exercising the service's fallback while
still resolving and playing real Opus audio. `--full` uses a single-item queue
and requires native `MediaEnded` after the expected real wall-clock duration.

The check prints only fixed categories, timings, and sanitized service status.
It does not print or persist signed media URLs, descriptors, or credentials.
Network availability and public media permissions can change: a passing initial
4 KiB CDN probe does not establish full-song availability. On 2026-09-17 the
anonymous `4D7u5KF7SP8` reference rejected later ranges with HTTP 403, while the
Rick Astley reference allowed native AAC and Opus playback and seeking. Do not
replace a failing playback assertion with a successful initial-URL probe.

`--retry` injects one initial source-resolution failure, then verifies that the player button retries and completes real playback. For automatic delivery-proof acquisition and renewal using the production host, use [PlaybackProofSmoke](../PlaybackProofSmoke/README.md).
