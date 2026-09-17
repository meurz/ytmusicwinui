# Production proof recovery check

This Windows-only, opt-in executable links the production `CoreService`,
`SessionStore`, `PlaybackProofProvider`, `PlaybackService`, models and upstream
interop project. There is no replacement core adapter. It uses a unique temporary
store on the output drive and cannot restore the application's account. Playback
volume is zero. The bundled provider and matching native DLL must be available.

```powershell
dotnet run --project tests/PlaybackProofSmoke -- --live
dotnet run --project tests/PlaybackProofSmoke -- --live --full
```

Without `--live`, it only prints usage. The default reference is `QoXDQa9L12A`,
which exhibited later-range HTTP 403 without delivery proof during development.
Use `--video VIDEO_ID` to choose another public reference that requires proof.
A reference that no longer requires proof fails the recovery assertion rather
than silently turning this into a generic playback check.

The check observes the real service's forced/proactive proof callback, requires
15 seconds of advancing native playback after verification, exercises midpoint
seek and pause, reselects the same video to verify reuse of the cached proof,
performs five rapid replacements, and checks cancellation/draining and disposal.
It inspects only the cached object's identity and the owned worker process ID;
it never reads or logs the token or signed media source. The owned worker must
have exited after disposal. No other Node process is enumerated or stopped.

`--full` replaces the seek/pause stage with a natural `MediaEnded` check. Its
wall-clock measurement starts only after proof recovery and sustained playback,
so an early end from the rejected original source cannot satisfy the assertion.
The remaining cache, cancellation and cleanup checks still run afterward.

For local candidate testing, MSBuild properties `ServiceDirectory`,
`ModelsDirectory`, `CoreInteropProject`, and `NativeCoreDirectory` can point to
another checkout. `NativeCoreDirectory` must contain `youtube_music_core.dll`
and the `po-provider` directory. Rebuild after changing production sources.
This check is intentionally not part of CI: network policy and public music
availability can change. Output contains only fixed categories and timings.
