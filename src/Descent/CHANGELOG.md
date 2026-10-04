# Descent changelog

## 0.1.1 — 2026-09-28 (game update, untested)

The 2026-09-27 game update removed `MissionLength` and the per-difficulty gen settings:
`DungeonLayoutDef` now carries one `genSettings`, one `mainPath` and a difficulty-tier range, and
`InitBuilder(refs, mode, realm, int _difficultyTier, hazards, seed)` keeps the layouts whose range
holds the tier (tier < 0, or none in range, skips the filter; disassembly of
`<InitBuilder>g__GatherLayouts`). `InitBuilderHook` now pins `_difficultyTier` to the floor's tier
for the floor's seed, and `FloorPlan.Validate` passes the tier to `GenerateLayout` (its default,
-1, would check a layout from any tier). `FloorSpec.Length` and the length ramp are gone.
Hotkeys ported from `UnityEngine.Input` (throws under the Input System) to `Keyboard.current`
(`Hotkeys.cs`). `ReturnToLobby`, `MissionSuccess` and the stub guard re-checked against the new
dump (own addresses; the empty-method stub moved from 0x35FC20 to 0x42A210, the guard reads it at
runtime). Realm 6 (Crypts, new) has a name; the realm bands still use 0–3.

## 0.1.0 — 2026-09-07 (untested)

First build. A run is a seed; sixteen floors in seed-shuffled realm bands, tier rising every
two floors from tier 1, difficulty / length / hazard level stepping behind it, boss battle on
the last floor. The hub board (or Backspace) validates the floor's layout with the game's own
pass, arms a countdown, and launches through `GameManager.LoadDungeon`. A Harmony prefix on
`GameManager.ReturnToLobby` turns the exit teleporter into a descent: bank the floor through
the game's own end-of-mission code (`SetRewardStats` → `SaveLoot` → counters reset), advance
the run, load the next floor. Failure and forfeit pass through untouched and keep the floor.
Run state in `UserData/Descent/runs.json` and the room property `dd.run`; events 170–179.
Dev hotkeys Backspace / End / Slash. Gate and transport carried over from LootOverhaul.
