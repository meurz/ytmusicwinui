# Vendored BgUtils provider

`session_manager.ts` is copied without modification from
[bgutil-ytdlp-pot-provider 2.0.0](https://github.com/Brainicism/bgutil-ytdlp-pot-provider/tree/2.0.0),
commit `37169ee2656e08c5c2e5dc9df4c598c0cb4c88a8`,
`server/src/session_manager.ts`.

Copyright belongs to the upstream contributors, including Brainicism and grqz.
It is distributed under GPL-3.0-only; see `../../LICENSE`.
The surrounding worker and build adapter are also GPL-3.0-only.

The desktop application launches this component as a separate process using a
private JSON-lines pipe. The provider's source, build inputs and license are
included with the distributed worker. It does not run an HTTP server.
