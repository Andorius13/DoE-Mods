using System;
using MelonLoader;

namespace LootOverhaul.Loot
{
    /// <summary>
    /// The ring settings of CustomAvatars, for the kobold's CONFIG tab. They live in
    /// CustomAvatars' own `[CustomAvatars]` preferences; this reads and writes them by name,
    /// so neither mod needs the other. CustomAvatars re-reads them twice a second, so a change
    /// shows on the hand at once. All of them are local: they change only what you see.
    /// </summary>
    public static class RingSettings
    {
        public const string Category = "CustomAvatars";

        public class Setting
        {
            public string Key, Label, Help, Format;
            public float Default, Min, Max, Step, BigStep;
        }

        public static readonly Setting[] All =
        {
            new Setting { Key = "RingSize",   Label = "SIZE",     Format = "x{0:0.00}",    Default = 1f, Min = 0.5f,  Max = 4f,   Step = 0.05f, BigStep = 0.25f, Help = "bigger or smaller" },
            new Setting { Key = "RingOffset", Label = "UP/DOWN",  Format = "{0:+0.0;-0.0;0}", Default = 0f, Min = -5f, Max = 5f, Step = 0.1f, BigStep = 0.5f, Help = "toward the back of the hand, in finger radii" },
            new Setting { Key = "RingSlide",  Label = "SLIDE",    Format = "{0:+0.00;-0.00;0}", Default = 0f, Min = -1f, Max = 1f, Step = 0.02f, BigStep = 0.1f, Help = "toward the fingertip, in finger lengths" },
            new Setting { Key = "RingTilt",   Label = "TILT",     Format = "{0:+0;-0;0}°", Default = 0f, Min = -180f, Max = 180f, Step = 5f, BigStep = 30f, Help = "toward the fingertip" },
            new Setting { Key = "RingTurn",   Label = "TURN",     Format = "{0:+0;-0;0}°", Default = 0f, Min = -180f, Max = 180f, Step = 5f, BigStep = 30f, Help = "left or right, seen from the back of the hand" },
            new Setting { Key = "RingRoll",   Label = "ROLL",     Format = "{0:+0;-0;0}°", Default = 0f, Min = -180f, Max = 180f, Step = 5f, BigStep = 30f, Help = "around the finger" },
        };

        /// <summary>True when CustomAvatars is loaded and has the ring settings (0.42.14 or later).</summary>
        public static bool Available => Entry("RingTilt") != null && VisibleEntry() != null;

        private static MelonPreferences_Entry<float> Entry(string key)
        {
            try { return MelonPreferences.GetEntry<float>(Category, key); } catch { return null; }
        }

        private static MelonPreferences_Entry<bool> VisibleEntry()
        {
            try { return MelonPreferences.GetEntry<bool>(Category, "RingsVisible"); } catch { return null; }
        }

        public static float Get(Setting s)
        {
            var e = Entry(s.Key);
            if (e == null) return s.Default;
            var v = e.Value;
            return float.IsFinite(v) ? v : s.Default;
        }

        public static void Nudge(Setting s, float by)
        {
            var e = Entry(s.Key);
            if (e == null) return;
            var v = Get(s) + by;
            // Snap to the step, so ten presses of +0.1 read 1.0 and not 0.99999.
            v = (float)Math.Round(v / s.Step) * s.Step;
            e.Value = Math.Max(s.Min, Math.Min(s.Max, v));
            Save();
        }

        public static bool Visible
        {
            get { var e = VisibleEntry(); return e == null || e.Value; }
            set { var e = VisibleEntry(); if (e == null) return; e.Value = value; Save(); }
        }

        public static void ResetAll()
        {
            foreach (var s in All) { var e = Entry(s.Key); if (e != null) e.Value = s.Default; }
            var vis = VisibleEntry(); if (vis != null) vis.Value = true;
            Save();
        }

        public static string Show(Setting s) => string.Format(s.Format, Get(s));

        private static void Save()
        {
            try { MelonPreferences.Save(); }
            catch (Exception e) { Core.Log.Warning($"Ring settings: could not save: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
