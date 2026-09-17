# Native viewport prefetch checks

This opt-in Windows executable links the production `ScrollPrefetcher` service.
It creates an isolated native WinUI window outside the desktop without activating
it. It does not start the music client, read sessions, access the network, or
change another window.

```powershell
dotnet run --project tests/ScrollPrefetchSmoke -c Release -- --run
```

The checks use real `ScrollViewer` layout and `ChangeView` notifications to cover
distant and approaching sentinels, automatic filling of short content, serialized
loads under repeated layout signals, cursor completion, page cancellation,
disposal during a pending load, transient retries, and bounded persistent errors.
Callback counters represent page responses; viewport geometry and the scheduler
are the production implementation rather than replacements.

Without `--run`, the executable only prints usage. `ServiceDirectory` can point
to a candidate checkout. This local functional check is intentionally not in CI.
