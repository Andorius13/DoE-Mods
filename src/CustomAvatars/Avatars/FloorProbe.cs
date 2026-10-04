using System;
using System.Collections.Generic;
using CustomAvatars.Recon;
using UnityEngine;
using Il2Cpp;
using Interop = CustomAvatars.Recon.Interop;

namespace CustomAvatars.Avatars
{
    /// <summary>
    /// Your movement capsule against the floor: one fix and the lines that prove it.
    ///
    /// How the game keeps you off the floor (VRPlayerControl, 2026-09-27 build). Your body
    /// is a CapsuleCollider (`Body Collider`, a child of `VR Controller`) that never touches
    /// the ground: a sphere cast straight down finds the floor, and the suspension drives the
    /// rigidbody so that `VR Controller` sits exactly on the point it hit. The capsule hovers
    /// above that point (local bottom = suspension + 0.1, 0.2 m in the shipped prefab), which
    /// is what lets you walk up a step: the cast sees the step's top before the capsule's
    /// curve reaches its lip. The cast's start is built in WORLD metres —
    /// `VR Controller.y + 0.75·0.35 + suspension + 0.1`, sphere radius 0.2625 — so its
    /// bottom is at exactly the capsule's bottom. That equality is the design: if the capsule
    /// ever comes down onto the floor (a hard landing outruns the suspension, which keeps a
    /// faster fall rather than braking it), the cast still starts just above the floor, finds
    /// it, and the suspension lifts you back up.
    ///
    /// Scaling the play space (PlayerSize) scales the capsule about `VR Controller` — its
    /// bottom goes to k·0.2 m — but not the cast, which still starts 0.2 m up. At x1.15 the
    /// capsule's bottom is 3 cm above where the cast starts. Land on the capsule once and
    /// the cast now starts 2–3 cm inside the floor, where a sweep does not see the floor it
    /// starts in: the suspension never recovers, and you stay resting on the capsule, every
    /// part of you 0.23 m lower than it should be — feet in the floor, the capsule's bottom
    /// level with the ground and caught by every stair lip, until a jump lifts you clear and
    /// the cast finds the floor again. At x1 the resting capsule leaves the cast touching the
    /// floor, which is why vanilla never sticks there; below x1 the cast starts above the
    /// capsule's bottom and cannot get inside the floor either.
    ///
    /// The fix restores the one relation scaling broke: when you are bigger than normal the
    /// cast's start is raised by what the scale added to the capsule's bottom, measured from
    /// the live capsule every physics step (its bottom in `VR Controller`'s own units, times
    /// the scale minus one). Nothing else of the game's changes; at AvatarSize 1 and below it
    /// never writes anything. `SizeGroundCastFollowsCapsule` turns it off.
    ///
    /// The diagnostics are read-only and run at any size, so a session can compare x1 with
    /// a size: `Floor (...)` snapshots at spawn, scene load, size change, height
    /// calibration and the Numpad 5 key; `Floor: *** SUNK` / `Floor: back on the suspension` when
    /// you settle onto the capsule and leave it; `Floor: blocked` when you push the stick and
    /// do not move, with what is in front of you and how high it reaches.
    /// </summary>
    public sealed class FloorProbe
    {
        private static FloorProbe _instance;

        private readonly Fbt.TrackerReader _trackers;
        private VRPlayerControl _control;
        private IntPtr _controlPtr;
        private Rigidbody _rb;
        private float _nextResolveAt;

        private readonly List<(float at, string why)> _pending = new List<(float, string)>();

        // Sampled at 10 Hz for the sunk / blocked detectors.
        private float _nextSampleAt;
        private bool _sunk;
        private float _sunkCandidateSince = -1f;
        private float _sunkSince;
        private float _clearCandidateSince = -1f;
        private int _sunkLogs;
        private float _lastAirborneEnd = -1f, _airborneSince = -1f, _airborneMaxFall, _lastFallSpeed;
        private bool _wasGrounded = true;
        private float _blockedSince = -1f, _nextBlockedLogAt;
        private int _blockedLogs;
        private float _sceneAt = -1f;
        private string _sceneName = "-";

        // The fix's state, written from the physics-step postfix.
        private static float _lift;
        private static float _liftLogged;
        private float _liftSize = 1f, _liftReportAt = -1f;
        private static int _liftFailures;
        private static bool _liftFailureLogged;

        private const float SampleInterval = 0.1f;
        private const float SunkDepth = 0.05f;        // floor point this far below the ground = resting on the capsule
        private const float SunkConfirm = 0.5f;
        private const int MaxSunkLogs = 40;
        private const int MaxBlockedLogs = 40;
        private const float MaxLift = 0.6f;

        public FloorProbe(Fbt.TrackerReader trackers)
        {
            _instance = this;
            _trackers = trackers;
        }

        /// <summary>A snapshot in <paramref name="delay"/> seconds, from anywhere.</summary>
        public static void Request(string why, float delay)
        {
            var me = _instance;
            if (me == null) return;
            try
            {
                if (!ModConfig.FloorProbeEnabled.Value) return;
                me._pending.Add((Time.unscaledTime + Mathf.Max(0f, delay), why));
            }
            catch { }
        }

        public void OnScene(string sceneName)
        {
            _sceneName = sceneName;
            _sceneAt = Time.unscaledTime;
            _control = null;
            _controlPtr = IntPtr.Zero;
            _rb = null;
            _nextResolveAt = 0f;
            Request($"scene {sceneName}, 3 s in", 3f);
            Request($"scene {sceneName}, 10 s in", 10f);
        }

        // ---- the fix ------------------------------------------------------------------------

        public static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                // UpdateGroundTraceOrigin has its own body (RVA 0x4AD860 in the 2026-09-27
                // build, once in dump.cs), so this patch lands on nothing else.
                var target = HarmonyLib.AccessTools.Method(typeof(VRPlayerControl), "UpdateGroundTraceOrigin");
                if (target == null)
                {
                    Core.Log.Warning("Floor: VRPlayerControl.UpdateGroundTraceOrigin not found — a bigger-than-normal " +
                                     "player can still sink onto the body capsule after a hard landing.");
                    return;
                }
                harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(
                    HarmonyLib.AccessTools.Method(typeof(FloorProbe), nameof(UpdateGroundTraceOrigin_Postfix))));
                Core.Log.Msg("Floor: VRPlayerControl.UpdateGroundTraceOrigin hooked — when you are bigger than normal, " +
                             "the ground probe starts at your scaled capsule's bottom, as it does at normal size.");
            }
            catch (Exception e)
            {
                Core.Log.Warning($"Floor: could not hook UpdateGroundTraceOrigin ({e.GetType().Name}: {e.Message}).");
            }
        }

        private static void UpdateGroundTraceOrigin_Postfix(VRPlayerControl __instance)
        {
            // Runs every physics step. Never throw out of here.
            try
            {
                if (PlayerSize.Applied <= 1.0005f || !ModConfig.SizeGroundCastFollowsCapsule.Value) { _lift = 0f; return; }
                if (!Interop.Alive(__instance)) { _lift = 0f; return; }

                var body = __instance.bodyCollider;
                if (!Interop.Alive(body) || !CapsuleEnds(body, out var bottom, out _, out _)) { _lift = 0f; return; }

                var floorPoint = __instance.transform;
                // What the capsule's bottom would be at the rig's rest scale, in metres above the
                // floor point, against where it is now: the difference is exactly what scaling
                // added. In vanilla steady state the cast starts there; in the game's own
                // transient states (a pull shrinking the capsule, a blend after a teleport) the
                // relation between the two is the game's, and is kept as it is, only scaled.
                var localBottom = floorPoint.InverseTransformPoint(bottom).y;
                var worldBottom = bottom.y - floorPoint.position.y;
                var lift = worldBottom - localBottom;
                if (!(lift > 0.0005f)) { _lift = 0f; return; }
                if (lift > MaxLift) lift = MaxLift;

                var origin = __instance.groundTraceOrigin;
                origin.y += lift;
                __instance.groundTraceOrigin = origin;
                _lift = lift;
            }
            catch
            {
                _lift = 0f;
                _liftFailures++;
            }
        }

        // ---- per frame -----------------------------------------------------------------------

        public void Tick()
        {
            try
            {
                if (!ModConfig.FloorProbeEnabled.Value) { _pending.Clear(); return; }
                ReportLiftChanges();
                if (!Resolve()) return;

                var now = Time.unscaledTime;
                for (var i = _pending.Count - 1; i >= 0; i--)
                {
                    if (now < _pending[i].at) continue;
                    var why = _pending[i].why;
                    _pending.RemoveAt(i);
                    Snapshot(why);
                }

                if (now >= _nextSampleAt)
                {
                    _nextSampleAt = now + SampleInterval;
                    Sample(now);
                }
            }
            catch (Exception e)
            {
                // Diagnostics must never take anything else down; say it once a while.
                if (Time.unscaledTime >= _nextBlockedLogAt)
                {
                    _nextBlockedLogAt = Time.unscaledTime + 30f;
                    Core.Log.Msg($"Floor: probe threw ({e.GetType().Name}: {e.Message}).");
                }
            }
        }

        private void ReportLiftChanges()
        {
            if (_liftFailures > 0 && !_liftFailureLogged)
            {
                _liftFailureLogged = true;
                Core.Log.Warning("Floor: the ground-probe lift threw inside the game's physics step; it is doing nothing " +
                                 "while that lasts. Please report this line.");
            }

            // Once per size, a moment after it lands, not per physics step: the lift also moves
            // in the game's own transient capsule states (a pull, a blend), which are noise here.
            var size = PlayerSize.Applied;
            var now = Time.unscaledTime;
            if (Mathf.Abs(size - _liftSize) > 0.0005f) { _liftSize = size; _liftReportAt = now + 1.5f; }
            if (_liftReportAt < 0f || now < _liftReportAt) return;
            _liftReportAt = -1f;
            var lift = _lift;
            if (size <= 1.0005f && Mathf.Abs(_liftLogged) < 0.0005f) return;   // vanilla, and said so already
            _liftLogged = lift;
            Core.Log.Msg(lift > 0.0005f
                ? $"Floor: ground probe lifted {lift:0.000} m at x{size:0.00} — it starts at your capsule's bottom again, as at normal size."
                : size > 1.0005f
                    ? $"Floor: ground probe NOT lifted at x{size:0.00}" + (ModConfig.SizeGroundCastFollowsCapsule.Value
                        ? " (no capsule measured yet, or the scale added nothing to its bottom)." : " — SizeGroundCastFollowsCapsule is off.")
                    : $"Floor: ground probe back at the game's own height (x{size:0.00}).");
        }

        private bool Resolve()
        {
            if (Interop.Alive(_control)) return true;
            var now = Time.unscaledTime;
            if (now < _nextResolveAt) return false;
            _nextResolveAt = now + 2f;

            VRPlayerControl found = null;
            try { found = UnityEngine.Object.FindObjectOfType<VRPlayerControl>(); } catch { found = null; }
            if (!Interop.Alive(found)) return false;

            _control = found;
            try { _rb = found.rb; } catch { _rb = null; }
            if (!Interop.Alive(_rb)) { try { _rb = found.GetComponent<Rigidbody>(); } catch { _rb = null; } }

            if (found.Pointer != _controlPtr)
            {
                _controlPtr = found.Pointer;
                _sunk = false;
                _sunkCandidateSince = -1f;
                _blockedSince = -1f;
                try
                {
                    var body = found.bodyCollider;
                    var cmd = found.spherecastCommand;
                    Core.Log.Msg($"Floor: watching `{Interop.ScenePath(found.transform)}` — body capsule `{Interop.Name(body)}` " +
                                 $"r {body.radius:0.###} h {body.height:0.###} centre {Interop.Vec(body.center)} local, axis {body.direction}; " +
                                 $"suspension {found.suspension:0.###}, speed {found.suspensionSpeed:0.###}; ground cast sphere r {cmd.radius:0.####}, " +
                                 $"{cmd.distance:0.##} m long; rigidbody {(Interop.Alive(_rb) ? "found" : "NOT found")}.");
                }
                catch (Exception e) { Core.Log.Msg($"Floor: watching VRPlayerControl (fields not readable: {e.Message})."); }
                Request("spawn", 2f);
            }
            return true;
        }

        // ---- measurements --------------------------------------------------------------------

        private struct Reading
        {
            public bool HaveGround;
            public float GroundY;
            public string GroundName;
            public int GroundLayer;
            public Vector3 Bottom, Top;
            public float Radius;
            public float FloorPointY;
            public float LocalBottom;
            public float CastBottomY;
            public float CastRadius;
            public bool Grounded;
            public float GroundDistance;
            public float VelocityY;
            public Vector3 Wanted, Actual;
            public bool Climbing, Pulling;
        }

        private bool Read(out Reading r)
        {
            r = default;
            var c = _control;
            if (!Interop.Alive(c)) return false;
            var body = c.bodyCollider;
            if (!Interop.Alive(body) || !CapsuleEnds(body, out r.Bottom, out r.Top, out r.Radius)) return false;

            var floorPoint = c.transform;
            r.FloorPointY = floorPoint.position.y;
            r.LocalBottom = floorPoint.InverseTransformPoint(r.Bottom).y;
            try
            {
                var cmd = c.spherecastCommand;
                r.CastRadius = cmd.radius;
                r.CastBottomY = c.groundTraceOrigin.y - cmd.radius;
            }
            catch { r.CastRadius = float.NaN; r.CastBottomY = float.NaN; }
            try { r.Grounded = c.Get_IsGrounded; } catch { }
            try { r.GroundDistance = c.groundDistance; } catch { r.GroundDistance = float.NaN; }
            try { r.Climbing = c.isClimbing; } catch { }
            try { r.Pulling = c.isPulling; } catch { }
            try { var w = c.Get_SmoothVelocity; w.y = 0f; r.Wanted = w; } catch { }
            if (Interop.Alive(_rb))
            {
                try { var v = _rb.velocity; r.VelocityY = v.y; v.y = 0f; r.Actual = v; } catch { }
            }

            // The ground straight under the capsule's axis, from the capsule's middle down,
            // skipping anything that is part of us.
            var from = (r.Bottom + r.Top) * 0.5f;
            if (GroundBelow(from, 6f, out var hit))
            {
                r.HaveGround = true;
                r.GroundY = hit.point.y;
                try { r.GroundName = hit.collider.name; r.GroundLayer = hit.collider.gameObject.layer; } catch { r.GroundName = "?"; }
            }
            return true;
        }

        private void Snapshot(string why)
        {
            if (!Read(out var r))
            {
                Core.Log.Msg($"Floor ({why}): no movement capsule to measure.");
                return;
            }

            var k = SafeScaleY(_control.transform);
            var g = r.HaveGround ? r.GroundY : r.FloorPointY;
            string Rel(float y) => (y - g).ToString("+0.000;-0.000;0.000");

            float eyes = float.NaN, trackingFloor = float.NaN, calib = float.NaN, playerY = float.NaN, modelY = float.NaN;
            try
            {
                var eye = XRRig.CenterEyeAnchor;
                if (Interop.Alive(eye)) eyes = eye.position.y - g;
                var cam = XRRig.Transform;
                if (Interop.Alive(cam))
                {
                    trackingFloor = cam.position.y - g;
                    if (Interop.Alive(cam.parent)) calib = cam.parent.localPosition.y;
                }
            }
            catch { }
            try
            {
                var p = AvatarPlayer.LocalAvatar;
                if (Interop.Alive(p))
                {
                    playerY = p.transform.position.y - g;
                    var fb = p.FullBody;
                    if (Interop.Alive(fb)) modelY = fb.transform.position.y - g;
                }
            }
            catch { }
            var hmd = _trackers != null && _trackers.TryHmdHeight(out var h) ? $"{h:0.000} m" : "?";

            var bottomAbove = r.Bottom.y - g;
            var hover = r.Bottom.y - r.FloorPointY;
            var verdict = !r.HaveGround
                ? "no ground found under you"
                : r.FloorPointY - g < -SunkDepth && bottomAbove < 0.04f
                    ? $"*** SUNK: resting on the capsule, {g - r.FloorPointY:0.000} m lower than the suspension holds you"
                    : r.FloorPointY - g < -SunkDepth
                        ? "floor point below the ground (on a slope or stair edge?)"
                        : "on the suspension";

            Core.Log.Msg(
                $"Floor ({why}) at x{k:0.00}: {verdict}. Capsule bottom {Rel(r.Bottom.y)} m above the ground, " +
                $"hovering {hover:0.000} over your floor point ({r.LocalBottom:0.000} at x1), top {Rel(r.Top.y)}, radius {r.Radius:0.000}; " +
                $"floor point (`VR Controller`) {Rel(r.FloorPointY)}; ground cast starts {Rel(r.CastBottomY)} (sphere r {r.CastRadius:0.####}, " +
                $"lifted {_lift:0.000}), grounded {r.Grounded}, groundDistance {r.GroundDistance:0.000}, vy {r.VelocityY:0.00}" +
                (r.Climbing ? ", climbing" : "") + (r.Pulling ? ", pulling" : "") +
                $"; eyes {eyes:0.000} m above the ground (headset {hmd} real), tracking floor {trackingFloor:+0.000;-0.000}, " +
                $"calibration offset {calib:0.000} local; Player_ {playerY:+0.000;-0.000}, Model_ {modelY:+0.000;-0.000}; " +
                (r.HaveGround ? $"ground `{r.GroundName}` layer {r.GroundLayer} ({LayerName(r.GroundLayer)}) at y {g:0.000}" : "no ground hit") +
                $"; scene {_sceneName}" + (_sceneAt >= 0f ? $" +{Time.unscaledTime - _sceneAt:0}s" : "") + ".");
        }

        private void Sample(float now)
        {
            if (!Read(out var r)) return;

            // Airborne bookkeeping: what came just before a sink.
            if (!r.Grounded && _wasGrounded) { _airborneSince = now; _airborneMaxFall = 0f; }
            if (!r.Grounded) _airborneMaxFall = Mathf.Max(_airborneMaxFall, -r.VelocityY);
            if (r.Grounded && !_wasGrounded) { _lastAirborneEnd = now; _lastFallSpeed = _airborneMaxFall; }
            _wasGrounded = r.Grounded;

            if (!r.HaveGround) return;
            var depth = r.GroundY - r.FloorPointY;
            var resting = depth > SunkDepth && r.Bottom.y - r.GroundY < 0.04f && !r.Climbing && !r.Pulling;

            if (!_sunk)
            {
                if (resting)
                {
                    if (_sunkCandidateSince < 0f) _sunkCandidateSince = now;
                    if (now - _sunkCandidateSince >= SunkConfirm)
                    {
                        _sunk = true;
                        _sunkSince = _sunkCandidateSince;
                        _clearCandidateSince = -1f;
                        if (_sunkLogs++ < MaxSunkLogs)
                        {
                            var before = _lastAirborneEnd >= 0f
                                ? $"last landing {now - _lastAirborneEnd:0.0} s ago after falling at up to {_lastFallSpeed:0.0} m/s"
                                : "no landing seen";
                            Core.Log.Warning($"Floor: *** SUNK — you are resting on your body capsule, {depth:0.000} m below where " +
                                             $"the suspension holds you ({before}; grounded {r.Grounded}, groundDistance {r.GroundDistance:0.000}).");
                            Snapshot("sunk");
                        }
                    }
                }
                else _sunkCandidateSince = -1f;
            }
            else
            {
                if (!resting)
                {
                    if (_clearCandidateSince < 0f) _clearCandidateSince = now;
                    if (now - _clearCandidateSince >= 0.3f)
                    {
                        _sunk = false;
                        _sunkCandidateSince = -1f;
                        if (_sunkLogs <= MaxSunkLogs)
                            Core.Log.Msg($"Floor: back on the suspension after {now - _sunkSince:0.0} s sunk" +
                                         (r.Grounded ? "" : " (airborne — a jump?)") + ".");
                    }
                }
                else _clearCandidateSince = -1f;
            }

            // Pushing the stick and not going anywhere.
            var wanted = r.Wanted.magnitude;
            var actual = r.Actual.magnitude;
            var blocked = r.Grounded && !r.Climbing && wanted > 0.7f && actual < 0.3f * wanted;
            if (!blocked) { _blockedSince = -1f; return; }
            if (_blockedSince < 0f) { _blockedSince = now; return; }
            if (now - _blockedSince < 0.5f || now < _nextBlockedLogAt || _blockedLogs >= MaxBlockedLogs) return;
            _nextBlockedLogAt = now + 4f;
            _blockedLogs++;
            Core.Log.Msg($"Floor: blocked — stick asks {wanted:0.00} m/s, moving {actual:0.00} m/s for {now - _blockedSince:0.0} s; " +
                         $"{ProbeAhead(r, r.Wanted / wanted)}.");
            Snapshot("blocked");
        }

        /// <summary>What stands in front of the capsule, and how high it reaches.</summary>
        private string ProbeAhead(Reading r, Vector3 dir)
        {
            var axis = new Vector3(r.Bottom.x, 0f, r.Bottom.z);
            var reach = r.Radius + 0.35f;
            float lowest = float.NaN, highestHit = float.NaN, nearest = float.PositiveInfinity;
            var clearAbove = float.NaN;
            for (var hgt = 0.02f; hgt <= 0.80f; hgt += 0.02f)
            {
                var from = new Vector3(axis.x, r.GroundY + hgt, axis.z);
                if (Cast(from, dir, reach, out var hit))
                {
                    if (float.IsNaN(lowest)) lowest = hgt;
                    highestHit = hgt;
                    clearAbove = float.NaN;
                    nearest = Mathf.Min(nearest, hit.distance);
                }
                else if (!float.IsNaN(lowest) && float.IsNaN(clearAbove)) clearAbove = hgt;
            }
            if (float.IsNaN(lowest)) return $"nothing within {reach:0.00} m ahead below +0.80 m (stuck on something else)";

            var stepTop = "";
            var beyond = new Vector3(axis.x, 0f, axis.z) + dir * (nearest + 0.05f);
            if (GroundBelow(new Vector3(beyond.x, r.GroundY + 1.0f, beyond.z), 1.5f, out var top))
                stepTop = $", its top {top.point.y - r.GroundY:+0.000;-0.000} m";
            return $"something ahead from +{lowest:0.00} to +{highestHit:0.00} m above the ground, {nearest:0.000} m from your capsule's axis" +
                   (float.IsNaN(clearAbove) ? " and higher" : $", clear from +{clearAbove:0.00}") + stepTop +
                   $"; your capsule's bottom is at {r.Bottom.y - r.GroundY:+0.000;-0.000}, radius {r.Radius:0.000}, ground cast sphere r {r.CastRadius:0.####}";
        }

        // ---- geometry helpers ----------------------------------------------------------------

        /// <summary>World-space lowest and highest points of a capsule, and its world radius.</summary>
        private static bool CapsuleEnds(CapsuleCollider c, out Vector3 bottom, out Vector3 top, out float radius)
        {
            bottom = top = Vector3.zero;
            radius = 0f;
            var t = c.transform;
            var axis = c.direction == 0 ? Vector3.right : c.direction == 2 ? Vector3.forward : Vector3.up;
            var half = Mathf.Max(c.height * 0.5f, c.radius);
            var a = t.TransformPoint(c.center - axis * half);
            var b = t.TransformPoint(c.center + axis * half);
            if (a.y <= b.y) { bottom = a; top = b; } else { bottom = b; top = a; }
            var s = t.lossyScale;
            var across = c.direction == 0 ? Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))
                       : c.direction == 2 ? Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y))
                       : Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            radius = c.radius * across;
            // The lowest point of an upright capsule is its axis end minus nothing; of a tipped
            // one it is lower by the radius. Ours are upright; keep the honest number anyway.
            if (Mathf.Abs(Vector3.Dot((b - a).normalized, Vector3.up)) < 0.99f) bottom.y -= radius;
            return float.IsFinite(bottom.y) && float.IsFinite(top.y);
        }

        private static float SafeScaleY(Transform t)
        {
            try { return t.lossyScale.y; } catch { return float.NaN; }
        }

        private bool GroundBelow(Vector3 from, float maxDistance, out RaycastHit best) =>
            Cast(from, Vector3.down, maxDistance, out best);

        /// <summary>Nearest non-trigger hit along a ray that is not one of our own colliders.</summary>
        private bool Cast(Vector3 from, Vector3 dir, float maxDistance, out RaycastHit best)
        {
            best = default;
            var found = false;
            var bestDistance = float.PositiveInfinity;
            var hits = Physics.RaycastAll(from, dir, maxDistance, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null) return false;
            for (var i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                if (hit.distance >= bestDistance) continue;
                Collider col;
                try { col = hit.collider; } catch { continue; }
                if (!Interop.Alive(col) || IsOurs(col)) continue;
                best = hit;
                bestDistance = hit.distance;
                found = true;
            }
            return found;
        }

        private bool IsOurs(Collider col)
        {
            try
            {
                if (Interop.Alive(_rb))
                {
                    var arb = col.attachedRigidbody;
                    if (Interop.Alive(arb) && arb.Pointer == _rb.Pointer) return true;
                }
                var root = col.transform.root;
                if (Interop.Alive(_control) && root.Pointer == _control.transform.root.Pointer) return true;
                var p = AvatarPlayer.LocalAvatar;
                if (Interop.Alive(p))
                {
                    if (root.Pointer == p.transform.root.Pointer) return true;
                    var fb = p.FullBody;
                    if (Interop.Alive(fb) && root.Pointer == fb.transform.root.Pointer) return true;
                }
            }
            catch { }
            return false;
        }

        private static string LayerName(int layer)
        {
            try { var n = LayerMask.LayerToName(layer); return string.IsNullOrEmpty(n) ? "unnamed" : n; }
            catch { return "?"; }
        }
    }
}
