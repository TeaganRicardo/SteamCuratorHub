# Steam Playtime and User Stats Feasibility

Updated: 2026-09-23
Related: #2

## Summary

Steam Curator Hub must separate playtime simulation and achievement/stat modification into different adapters.

## Playtime

Candidate route: Steam client protocol based implementation.

Evidence:

- `node-steam-user` exposes `gamesPlayed()` for reporting one or more AppIDs.
- ASF's `GamesPlayedWhileIdle` demonstrates that owned AppIDs can accumulate hours without installing or launching the game.
- ASF documents a limit of multiple simultaneous AppIDs and notes that Steam playing state conflicts must be handled when a real game is launched.

Conclusion:

Multi-AppID idle playtime is technically feasible. #5 must verify server-side playtime increase on the target Mac account.

## Achievements and Statistics

Public Steam Web API write endpoints are unsuitable for this product. Valve's `SetUserStatsForGame` requires a publisher authentication key and is not a client-side player API.

Steamworks client APIs provide:

- `SetAchievement`
- `ClearAchievement`
- `SetStat` for integer and float values
- `StoreStats`

These APIs are designed for games, so third-party modification requires a client-context implementation.

Third-party evidence:

- SAM-style tools implement achievement and stat modification through Steam client interfaces.
- Current forks have demonstrated Apple Silicon macOS support, but this is not a substitute for project validation.

## Architecture Impact

Recommended boundaries:

```
PlaytimeTransport
  start(appIds)
  stop(appId)
  observe()
  readBack()

UserStatsTransport
  loadSchema(appId)
  read()
  modifyAchievements()
  modifyStats()
  storeAndVerify()
```

UI and task workflows should not depend directly on Steam protocol details.

## #5 Validation Requirements

Required real Mac verification:

1. Use at least one owned Windows-only/non-installed AppID.
2. Run two AppIDs simultaneously and verify Steam server playtime changes.
3. Test individual and batch achievement unlock.
4. Test relock where supported.
5. Test integer and float statistics.
6. Re-read after submission; UI state alone is not evidence.

Any unsupported game/item must be recorded as unsupported with reason.
