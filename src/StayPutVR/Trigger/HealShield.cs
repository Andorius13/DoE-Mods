namespace StayPutVR.Trigger
{
    /// <summary>What healed you, as far as the heal shield cares.</summary>
    public enum HealSource { None, MinorPotion, MajorPotion, Staff, LifeSteal }

    public static class HealSources
    {
        /// <summary><c>Prop.Type.HealthPotionSmall</c>; every other healing potion counts as major (<c>HealthPotion</c> = 12).</summary>
        public const int SmallPotionType = 18;

        public static HealSource FromPotionType(int propType) =>
            propType == SmallPotionType ? HealSource.MinorPotion : HealSource.MajorPotion;

        public static string Name(HealSource source) => source switch
        {
            HealSource.MinorPotion => "minor healing potion",
            HealSource.MajorPotion => "major healing potion",
            HealSource.Staff => "healing staff",
            HealSource.LifeSteal => "life steal",
            _ => "",
        };
    }

    /// <summary>
    /// For a while after a heal, hits do not shock. Only the timing lives here, so the tests can
    /// compile it; <see cref="HealWatch"/> decides what healed you, <see cref="ShockPolicy"/>
    /// picks the length for that source and asks <see cref="Holds"/>.
    ///
    /// A potion (<see cref="Start"/>) moves the end to whichever is later, its own or the running
    /// one's, so it never cuts a longer shield short. The staff and life steal stack
    /// (<see cref="Stack"/>): each tick or steal adds its seconds on top of whatever is left, up
    /// to a cap counted from now, so the longer you are healed the longer the shield. The hit that
    /// puts you down is never held: death always fires.
    /// </summary>
    public sealed class HealShield
    {
        private float _until = -1f;
        private float _length;

        /// <summary>What set the current end, for the log and the panel. Empty when no shield has run.</summary>
        public string Source { get; private set; } = "";

        /// <summary>
        /// A heal worth <paramref name="seconds"/>. True when that moved the end later; false, and
        /// nothing changes, when the setting is 0 or less or the running shield already lasts longer.
        /// </summary>
        public bool Start(float now, float seconds, string source)
        {
            if (seconds <= 0f) return false;
            var until = now + seconds;
            if (until <= _until) return false;
            _length = seconds;
            _until = until;
            Source = source ?? "";
            return true;
        }

        /// <summary>
        /// A stacking heal (staff tick, life steal): adds <paramref name="seconds"/> to the end, or
        /// to now if nothing is running, but never past now + <paramref name="cap"/>. A cap below
        /// <paramref name="seconds"/> counts as <paramref name="seconds"/>. True when the end moved;
        /// false at the cap, when the running shield already ends later than that, or when
        /// <paramref name="seconds"/> is 0 or less.
        /// </summary>
        public bool Stack(float now, float seconds, float cap, string source)
        {
            if (seconds <= 0f) return false;
            if (cap < seconds) cap = seconds;
            var from = _until > now ? _until : now;
            var until = from + seconds;
            if (until > now + cap) until = now + cap;
            if (until <= _until) return false;
            _length = until - now;
            _until = until;
            Source = source ?? "";
            return true;
        }

        public void Clear() => _until = -1f;

        /// <summary>Seconds left, 0 once it has run out.</summary>
        public float Left(float now) => _until > now ? _until - now : 0f;

        /// <summary>Share of the shield left, 1 just after the heal that set the end down to 0.</summary>
        public float Fraction(float now) => _length > 0f ? Left(now) / _length : 0f;

        public bool Active(float now) => Left(now) > 0f;

        /// <summary>Whether a hit now is held back. A lethal hit never is.</summary>
        public bool Holds(float now, bool lethal) => !lethal && Active(now);
    }
}
