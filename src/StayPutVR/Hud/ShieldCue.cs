using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using StayPutVR.Trigger;
using UnityEngine;
using UnityEngine.Rendering;

namespace StayPutVR.Hud
{
    /// <summary>
    /// The one thing the mod draws in the headset: a dim arc low in your view while the heal
    /// shield is up, so a hit that does not shock reads as the shield and not as a broken link.
    /// It flashes briefly as it appears, shortens from both ends as the shield runs out and
    /// fades over its last second.
    ///
    /// A status light, not a panel — the settings and the counters stay on the desktop. Nothing
    /// the game already has fits: its shield dome (<c>ShieldArea</c>) blocks damage and pushes
    /// enemies away, and the arm glow is the game's own health readout. So it is a mesh of the
    /// mod's own, parented to the camera so it moves with the head on the same frame, drawn
    /// over everything but thin and dim enough not to hide anything. Only shown while armed:
    /// disarmed, nothing would have fired anyway.
    /// </summary>
    public static class ShieldCue
    {
        private const int Segments = 48;
        private const float Distance = 0.6f;       // metres in front of the eyes
        private const float RingDegrees = 28f;     // how far below the centre of view
        private const float Thickness = 0.007f;
        private const float HalfSpanDegrees = 55f; // at a full shield
        private const float FlashSeconds = 0.6f;   // brighter for this long when it appears
        private static readonly Color Tint = new Color(0.60f, 0.90f, 1.00f, 0.55f);

        private static GameObject _go;
        private static Mesh _mesh;
        private static Material _material;
        private static Transform _eyes;
        private static float _eyesRetryAt;
        private static float _shownAt = -1f;
        private static bool _failed, _logged;
        private static readonly Vector3[] Verts = new Vector3[(Segments + 1) * 2];
        private static readonly Color[] Colors = new Color[(Segments + 1) * 2];

        public static void Tick()
        {
            if (_failed) return;
            try
            {
                var now = Time.unscaledTime;
                var shield = ShockPolicy.Shield;
                var show = ModConfig.Enabled.Value && ShockPolicy.Armed && shield.Active(now);
                if (!show)
                {
                    _shownAt = -1f;
                    if (Interop.Alive(_go) && _go.activeSelf) _go.SetActive(false);
                    return;
                }
                if (!Ensure()) return;
                if (!_go.activeSelf) _go.SetActive(true);
                if (_shownAt < 0f) _shownAt = now;
                Shape(shield.Fraction(now), shield.Left(now), now);
            }
            catch (Exception e)
            {
                _failed = true;
                Core.Log.Warning($"Heal shield cue off for this session: {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool Ensure()
        {
            var eyes = Eyes();
            if (!Interop.Alive(eyes)) return false;
            if (Interop.Alive(_go) && _go.transform.parent != null && _go.transform.parent.Pointer == eyes.Pointer) return true;
            if (Interop.Alive(_go)) UnityEngine.Object.Destroy(_go);

            if (!Interop.Alive(_material))
            {
                Shader shader = Shader.Find("UI/Default");
                var onTop = shader != null;
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Unlit/Transparent");
                if (shader == null) { _failed = true; Core.Log.Warning("No usable shader for the heal shield cue; it will not be drawn."); return false; }
                _material = new Material(shader) { name = "StayPutVR_ShieldCue", color = Color.white };
                if (onTop)
                {
                    try { _material.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always); } catch { }
                    _material.renderQueue = 4000;
                }
                _material.hideFlags = HideFlags.DontUnloadUnusedAsset;   // the game unloads unused assets on a scene change
            }
            if (!Interop.Alive(_mesh))
            {
                _mesh = new Mesh { name = "StayPutVR_ShieldCue" };
                _mesh.MarkDynamic();
                var tris = new int[Segments * 12];
                for (var i = 0; i < Segments; i++)
                {
                    int a = i * 2, b = a + 1, c = a + 2, d = a + 3, t = i * 12;
                    // Both windings, so it never culls away whichever way it faces.
                    tris[t] = a; tris[t + 1] = c; tris[t + 2] = b;
                    tris[t + 3] = b; tris[t + 4] = c; tris[t + 5] = d;
                    tris[t + 6] = a; tris[t + 7] = b; tris[t + 8] = c;
                    tris[t + 9] = b; tris[t + 10] = d; tris[t + 11] = c;
                }
                _mesh.vertices = (Il2CppStructArray<Vector3>)Verts;
                _mesh.colors = (Il2CppStructArray<Color>)Colors;
                _mesh.triangles = (Il2CppStructArray<int>)tris;
                _mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }

            _go = new GameObject("StayPutVR_ShieldCue");
            _go.transform.SetParent(eyes, false);
            _go.transform.localPosition = Vector3.zero;
            _go.transform.localRotation = Quaternion.identity;
            _go.transform.localScale = Vector3.one;
            var mf = _go.AddComponent(Il2CppType.Of<MeshFilter>()).TryCast<MeshFilter>();
            var mr = _go.AddComponent(Il2CppType.Of<MeshRenderer>()).TryCast<MeshRenderer>();
            if (mf == null || mr == null) { _failed = true; return false; }
            mf.sharedMesh = _mesh;
            mr.sharedMaterial = _material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            if (!_logged) { _logged = true; Core.Log.Msg($"Heal shield cue drawn with {_material.shader.name}, on `{Interop.ScenePath(eyes)}`."); }
            return true;
        }

        /// <summary>An arc of a circle around the line of sight, centred straight below it.</summary>
        private static void Shape(float fraction, float left, float now)
        {
            var radius = Distance * Mathf.Tan(RingDegrees * Mathf.Deg2Rad);
            var half = HalfSpanDegrees * Mathf.Clamp01(fraction) * Mathf.Deg2Rad;
            // A short flash as it appears, so the start is noticed; then a slow, shallow pulse.
            var flash = 1f + 0.8f * Mathf.Clamp01(1f - (now - _shownAt) / FlashSeconds);
            var alpha = Mathf.Clamp01(Tint.a * flash * Mathf.Clamp01(left) * (0.85f + 0.15f * Mathf.Sin(now * 5f)));
            for (var i = 0; i <= Segments; i++)
            {
                var u = (float)i / Segments;
                var angle = -Mathf.PI / 2f - half + 2f * half * u;
                var dir = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                Verts[i * 2] = dir * (radius - Thickness / 2f) + new Vector3(0f, 0f, Distance);
                Verts[i * 2 + 1] = dir * (radius + Thickness / 2f) + new Vector3(0f, 0f, Distance);
                // Tapered ends rather than a hard cut.
                var taper = Mathf.SmoothStep(0f, 1f, Mathf.Min(u, 1f - u) / 0.2f);
                var c = new Color(Tint.r, Tint.g, Tint.b, alpha * taper);
                Colors[i * 2] = c;
                Colors[i * 2 + 1] = c;
            }
            _mesh.vertices = (Il2CppStructArray<Vector3>)Verts;
            _mesh.colors = (Il2CppStructArray<Color>)Colors;
            _mesh.RecalculateBounds();
        }

        private static Transform Eyes()
        {
            if (Interop.Alive(_eyes) && _eyes.gameObject.activeInHierarchy) return _eyes;
            if (Time.unscaledTime < _eyesRetryAt) return null;
            _eyesRetryAt = Time.unscaledTime + 1f;
            _eyes = null;
            try
            {
                var cam = Camera.main;
                if (Interop.Alive(cam) && cam.enabled) _eyes = cam.transform;
            }
            catch { }
            if (_eyes == null)
            {
                try
                {
                    var local = AvatarPlayer.LocalAvatar;
                    if (Interop.Alive(local) && Interop.Alive(local.Eye)) _eyes = local.Eye;
                }
                catch { }
            }
            return _eyes;
        }
    }
}
