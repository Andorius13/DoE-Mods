# FBT stretch fix — plan (temporary, safe to delete)

Target version: **CustomAvatars 0.42.3** (current `0.42.2` in
`src/CustomAvatars/Core.cs:9`, `:28`, `CustomAvatars.csproj:7`).

Reporter: Flinters, running 0.42.0. Logs `26-9-4_4-22-23.log`,
`26-9-4_5-28-6.log`, recon `recon-20260904-042235.md`,
`recon-20260904-052818.md`.

---

## 1. Diagnosis

### 1a. Resolved already (no code change) — role misassignment

First session refused to calibrate:

```
FBT: RightFoot lateral position taken from the tracker (+323033.1 cm from the rig's stance)
*** FBT: the RightFoot offset came out 8401.02 m — that is not a mounting offset...
```

The guard at `FbtCalibrator.cs:296` did its job. After the reporter set the
device roles in SteamVR, calibration is clean:

```
*** FBT calibrated: Hip       = LHR-A6FFDDD4, offset 22.2cm
*** FBT calibrated: RightFoot = LHR-D9DA7736, offset 9.8cm
*** FBT calibrated: LeftFoot  = LHR-18E45AB9, offset 7.9cm
```

Three distinct serials, plausible offsets. Not chased further.

### 1b. The live bug — hold-last-pose is in world space

`EnsureProxies` (`FbtManager.cs:321-326`) creates the three tracker proxies as
**unparented root GameObjects** (`DontDestroyOnLoad`, no `SetParent` — the only
`SetParent` in the file attaches the offset *child* to the proxy). Every frame
`DriveProxies` writes an absolute world pose:

```csharp
proxy.transform.SetPositionAndRotation(device.WorldPos, device.WorldRot);
```

When a tracker's pose goes invalid, `DriveProxies` `continue`s
(`FbtManager.cs:288-293`) and the proxy retains that **absolute world pose**.

The comment at `FbtManager.cs:279-280` — "the proxy keeps its last pose, which
reads as a frozen foot rather than a leg snapping to origin" — is right only
while the player is stationary. Under locomotion the held pose is nailed to a
spot in the dungeon while the player walks away, so the error grows without
bound.

The reporter's hip puck drops out constantly (`Calibrating_OutOfRange`, 21
times in one five-second window). Result: pelvis target frozen in the world,
both foot targets tracking correctly (zero foot blips all session), VRIK
stretching the legs between a receding pelvis and his moving feet. Exactly the
reported "legs stretch into oblivion once he moves", and "only tracking the
hips" is the pelvis reading as *stuck*.

Correlation, from `26-9-4_5-28-6.log`:

| time | event |
|---|---|
| 05:29:03.745 | healthy — `needed 41.8cm, stretch x1.00, shoulder off 3.9cm` |
| 05:29:13.590 | first blip — `Hip LHR-A6FFDDD4: Calibrating_OutOfRange` |
| 05:29:14.087 | `needed 105.5cm, stretch x1.50, shoulder off 15.8cm` |
| 05:29:15–23 | pinned at `needed ~122cm, stretch x1.50` for ten seconds |
| 05:29:25.095 | blips stop → `needed 67.6cm, stretch x1.13` |
| 05:29:27.735 | recovered → `needed 59cm, stretch x1.00` |

The `arms:` lines are the visible symptom because they are the only per-frame
limb diagnostic that exists. The avatar's shoulder rides the game rig's chest,
the chest rides the pelvis, the pelvis is hauled to a stale world point — so
the arm "needs" 122 cm of reach. The legs stretch silently alongside.

---

## 2. Fix 1 — hold the pose in play space (primary)

`ReadDevice` already computes tracking-space `LocalPos`/`LocalRot`
(`TrackerReader.cs:210-211`) before mapping through `XRRig.Transform`. Cache
those per role and re-project on a blip, so a stuck tracker rides along with
the play space and reads as a frozen limb — what the existing comment intended.

In `FbtManager`:

- Add `Vector3[] _lastLocalPos = new Vector3[3]`,
  `Quaternion[] _lastLocalRot = new Quaternion[3]`, `bool[] _hasLastLocal`.
- On a **valid** read: drive the proxy as today, then store
  `device.LocalPos` / `device.LocalRot` into the role's slot.
- On an **invalid / absent** read: if `_hasLastLocal[role]` and
  `XRRig.Transform` is alive, write
  `rig.TransformPoint(_lastLocalPos[role])` and
  `rig.rotation * _lastLocalRot[role]` into the proxy instead of leaving the
  world pose untouched. If there is no rig or no cached local pose, fall
  through to today's behaviour.
- Clear `_hasLastLocal` in `OnCalibrationLocked` and wherever the rig is
  restored, so a stale pose can't survive a recalibration.

Keep the existing `_blipCount` / `_blipDetail` aggregation unchanged.

## 3. Fix 2 — staleness timeout (primary)

There is currently no time limit at all; a dropout drags forever. After a
tracker has been invalid for longer than a threshold, stop driving that target
so the failure degrades to vanilla IK rather than a deformed skeleton.

- `float[] _staleSince = new float[3]` in `FbtManager`, set on the first
  invalid frame, cleared on the first valid one.
- New config key `FbtStaleSeconds` (default `1.0`), alongside the existing FBT
  keys in `ModConfig`.
- `FbtRig` currently hardcodes the weights in `AssertPerFrame`
  (`FbtRig.cs:143-152`): `pelvisPositionWeight = 1f`,
  `leftLeg.positionWeight = 1f`, `rightLeg.positionWeight = 1f`. Add three
  public multipliers (`HipWeight`, `LeftFootWeight`, `RightFootWeight`,
  default `1f`) and multiply them in, leaving the config-driven rotation
  weights as they are.
- `FbtManager.DriveProxies` sets each multiplier from the role's staleness —
  eased to `0` over ~0.25 s once past the threshold, back to `1` on recovery,
  so it fades rather than pops.
- `DriveProxies` runs immediately before `_localRig.AssertPerFrame()` in
  `UpdateLocal` (`FbtManager.cs:265-266`), so the weights land the same frame.
- Log once per transition (throttled like the blip line): which role went
  stale and for how long.

Peer rigs go through the same `FbtRig`, so they inherit the fix; remote poses
arrive via `TrackerSync` in root-local space already and need no change.

## 4. Fix 3 — diagnostics (carry over from the first session)

Not the current bug, but the first session cost three rounds of hardware
fiddling because none of this was in the log.

- **Tracking-result gate.** `ReadDevice` (`TrackerReader.cs:204-206`) reads
  `device.Result` and then gates only on `bPoseIsValid` plus a finite check.
  Require `Result == ETrackingResult.Running_OK` for a pose to count as valid,
  and add a plausibility band on `LocalPos` (a room is ~10 m, not 3 km). This
  is what would have caught the 8401 m tracker at the read, by name.
- **Dump on calibration rejection.** `DumpNow()` is reachable only from
  `FbtManager.Toggle()`'s two refusal paths (`FbtManager.cs:111`, `:126`).
  Because FBT starts enabled from settings, `Toggle()` never runs and no
  enumeration is ever written — confirmed: neither recon file contains a
  `## SteamVR trackers (FBT recon)` section. Call `DumpNow()` from the reject
  paths in `FbtCalibrator.TryLock`.
- **Log role → serial → position at assignment.** `AssignRoles`
  (`FbtCalibrator.cs:322-390`) logs nothing about what it chose unless the lock
  succeeds. One line per role at assignment time makes a mis-binding visible
  even on a successful calibration.

## 5. Secondary observation — calibration settle (investigate, don't fix blind)

Three calibrations in one session produced:

| attempt | Hip | RightFoot | LeftFoot | rig state before |
|---|---|---|---|---|
| 05:29:02 | 22.2 cm | 9.8 cm | 7.9 cm | never wired (truly idle) |
| 05:29:36 | 29.7 cm | 25.4 cm | 20.6 cm | released ~3 s earlier |
| 05:31:42 | 27.4 cm | 23.8 cm | 18.8 cm | released ~2 s earlier |

Straps do not move 15 cm. The `UpdateLocal` comment
(`FbtManager.cs:207-212`) already documents this exact failure and the release
fix is in place ("rig released (calibrating against the idle rig)"), yet the
drift persists — the two post-release attempts agree with each other and both
differ from the one clean attempt. That pattern suggests `Restore` is being
followed by a lock before VRIK has relaxed back to procedural stance.

Likely fix: a settle delay after `Restore` before `TryLock` will accept a
squeeze. **Confirm against a fresh log before changing anything** — the
alternative explanation (he simply stood differently) is not ruled out.

## 6. Reporter-side, no code

His hip puck is genuinely unhealthy — `Calibrating_OutOfRange` at that rate is
not normal. Check base-station coverage at waist height and whether the puck
faces into his body. The mod must not tear the avatar apart when it happens,
which is what 2 and 3 above address, but the puck is the trigger.

---

## 7. Verification

- Wire FBT, then deliberately occlude the hip puck (hand over it) while
  walking. Before: pelvis anchors to the floor and the body stretches. After:
  the pelvis rides along, then the hip target fades out after ~1 s and the body
  returns to vanilla IK.
- Same for one foot puck.
- Uncover: weights ease back to 1, no pop.
- Confirm the `arms:` line stays at `stretch x1.00` throughout.
- Confirm `Restore` still puts every saved value back (`FbtRig.cs:168-195`) —
  the new weight multipliers must not leak into the restore path.
- Grep the new log for `tracker blip`, `stale`, and `stretch x1.5`.

## 8. Files touched

- `src/CustomAvatars/Fbt/FbtManager.cs` — play-space hold, staleness, weights
- `src/CustomAvatars/Fbt/FbtRig.cs` — weight multipliers in `AssertPerFrame`
- `src/CustomAvatars/Fbt/TrackerReader.cs` — tracking-result gate, sanity band
- `src/CustomAvatars/Fbt/FbtCalibrator.cs` — dump on reject, role logging
- `src/CustomAvatars/ModConfig.cs` — `FbtStaleSeconds`
- `src/CustomAvatars/Core.cs`, `CustomAvatars.csproj` — version to 0.42.3
- `CHANGELOG.md`
