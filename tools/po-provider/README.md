# Native proof-of-origin worker

This separately licensed GPL-3.0-only component reuses the existing
[BgUtils POT provider 2.0.0](https://github.com/Brainicism/bgutil-ytdlp-pot-provider/tree/2.0.0)
as a private subprocess. It runs in Node.js with JSDOM and native canvas support;
it does not launch a browser, expose an HTTP server, or import a Google account.

The desktop application owns the process, serializes requests, validates output,
and kills the entire process on cancellation or timeout. Tokens and signed media
URLs must never be logged or persisted. The upstream integrity/minter cache stays
in memory between requests; per-video tokens are retained only by the host.

## Pipe protocol

Start `node.exe worker.cjs`, write one UTF-8 JSON line per request, and read one
UTF-8 JSON line per response. Keep the pipe open to reuse the initialized minter.

Request: `{"video_id":"QoXDQa9L12A"}`.

Success has `gvs_token` (a secret URL-safe string) and `expires_at` (Unix seconds).
The token is bound to the requested video ID. It is intended for anonymous Web
GVS requests; it is not a player token or account authorization.

Failure has only `error`: `invalid_request`, `po_generation_failed`, or
`po_worker_failed`. Upstream messages and token values do not enter diagnostics.
The worker validates video IDs and accepts no cookie, URL, account, or script input.
It exits on stdin EOF. An active request can still require the host's timeout.

`node.exe worker.cjs --check-runtime` checks native DOM/canvas loading without
network access and returns `{"ready":true}` on success.

## Build and redistribution

Run `scripts/bootstrap-po-provider.ps1 -Architecture x64` from the repository.
It downloads a pinned, SHA-256-verified Node.js runtime, restores the locked npm
packages without install scripts, and bundles the existing provider with esbuild.
The resulting folder is `.native/win-x64/po-provider`; ARM64 uses its matching
Node.js and native canvas packages.

The original canvas package has no Windows ARM64 prebuild. The package manifest
uses the existing `@napi-rs/canvas` implementation as the `canvas` dependency for
both architectures. npm's `--legacy-peer-deps` is intentional: JSDOM's optional
peer version range refers to the original canvas package's version scheme.
Compatibility must be verified with both the DOM/canvas check and a real minted
proof accepted for a media range beyond YouTube's initial playback allowance.

`vendor/bgutil/session_manager.ts` is unmodified upstream source. `build.mjs`
transpiles/bundles it without maintaining another implementation of the challenge
or minter. `vendor/bgutil/UPSTREAM.md` records its origin. The generated JavaScript,
source, build script, lockfile, GPL license and dependency licenses are distributed
together. The pinned Node.js runtime includes its own license. The desktop
application's MIT license does not replace this worker's GPL-3.0-only license.

The helper executes Google's current challenge code, so network availability and
protocol changes remain external dependencies. A successfully generated proof
alone is not a guarantee that every video is available or playable.

## Validation record

On Windows x64, the packaged Node.js 22.22.3 worker with native NAPI canvas minted
a first anonymous proof in 7.0 seconds and another through its retained minter in
0.002 seconds. Standard-error output was empty and stdin EOF exited with code 0.
The matching core 0.9.1 candidate accepted `anonymous_attestation_context` followed
by `set_po_tokens`: for `QoXDQa9L12A`, AAC and Opus each changed from HTTP 403 to
HTTP 206 with all 65,536 requested bytes at offset 2,097,152. This is a live sample,
not an availability guarantee. Native ARM64 execution requires a Windows ARM64 host.
