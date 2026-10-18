# Native achievement/stat helper

Build from the repository root:

```sh
node desktop/native/build.mjs
node --test desktop/test/native-stats.test.mjs
```

The Objective-C++ helper uses Foundation for JSON and loads the operator's
installed Steam client at runtime. It has no SDK dependency. Development and CI
must run fixtures only. Tests do not connect Steam or inspect account/game data.
A plain metadata request hashes/inspects the supplied library without loading it.
`--version` also never loads Steam.

The application imports `NativeStatsService` from `services/native-stats.mjs`.
`read({appId})` is the preview: it returns the full SteamID64 as `accountId`,
achievements with names/descriptions/hidden/unlock time/global percentages when
available, and typed numeric statistics. `apply` requires that preview, the same
account, and previous values. A helper process is dedicated to one AppID and one
command. Each apply consumes its preview, checks current values again, stages
permitted changes, observes the matching StoreStats callback, then the service
starts a **different** helper to request refreshed stats and compare each item.

A direct helper request is one JSON object on stdin, one response on stdout:

```json
{"operation":"read","appId":480}
```

To test without Steam, include a `fixture` object (or construct the service with
`fixture:true`). No real library is inspected or loaded in that mode:

```json
{"operation":"read","appId":480,"fixture":{"accountId":"76561198000000000","achievements":[],"stats":[]}}
```

For apply, send `expectedAccountId`, `expectedLibrarySha256` obtained from the
preview, and `changes.achievements:[{id,unlocked,before}]` /
`changes.stats:[{id,type,value,before}]`. The service supplies before values only
from its saved preview when omitted; the native helper always requires them.
Individual API rejection, schema/type/range protection, store rejection,
callback timeout and refreshed-value mismatch retain separate results. `unknown`
is never converted to verified merely because the local client reflects a value.
There is no automatic mutation retry. Read again before deciding a new attempt.

Current native route is guarded to **macOS arm64**, an arm64 Mach-O client slice,
exact `SteamClient018`, `SteamUser012`, `SteamUtils005`,
`STEAMUSERSTATS_INTERFACE_VERSION013`, AppID context and valid account identity.
The preview's SHA-256 also prevents applying against an updated client binary.
These are compatibility guards, not evidence that this private ABI was tested
against a live account. `privateAbiLocallyVerified:false` reports that boundary.
A missing interface or process crash fails with an explicit error/unknown outcome;
there is no fallback to a different vtable version. Windows native support remains
an explicit capability gap, and no game is assumed installed or platform-native.

Only `appcache/stats/UserGameStatsSchema_<the requested AppID>.bin` is read for
numeric enumeration and protection; no login/session file or player stats file
is opened. A missing/unsupported schema disables writes and marks enumeration
unknown. Average-rate stats are shown but cannot be directly assigned. Actual
owned-game access, changes and persistence on uninstalled Windows-only games
still require operator verification. Local read can affect Steam playing presence.

A `verified` live item means matching store callback plus separate-process client
refresh; `verification.independentServerRead` remains false. Fixture results
always report `scope:fixture` and `steamVerified:false`, even when the synthetic
comparison passes. Never describe fixture output as actual Steam acceptance.

Build outputs remain under ignored `native/build/`. Retain THIRD_PARTY_NOTICES.md
with distributions. Do not commit a Steam dylib, SDK, session, or user data.
