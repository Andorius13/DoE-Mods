namespace StayPutVR.Trigger
{
    /// <summary>
    /// "A healing staff's beam is on you", as told by the beam's own RPC
    /// (<c>WeaponStaff.BeamPlayer</c>), kept apart from the heal it should cause. Pure timing, so
    /// the tests compile it; <see cref="HealWatch"/> feeds it and logs what <see cref="Poll"/> says.
    ///
    /// A heal within <see cref="Window"/> of a beam is the staff's, whatever else marked it. An
    /// episode is a run of beams with no gap longer than <see cref="EpisodeGap"/>; a beam that no
    /// heal follows is reported once per episode, so the log says which link broke.
    ///
    /// Each beam is one tick of the staff shield, and is claimed (<see cref="Claim"/>) by the heal
    /// it causes, so a tick seen both here and by the heal's own frame mark counts once, and a
    /// stray heal inside the window (the game's own regain) cannot take a second tick.
    /// </summary>
    public sealed class StaffBeam
    {
        public const float Window = 0.75f;
        public const float EpisodeGap = 3f;
        /// <summary>The kinetic type when it could not be read.</summary>
        public const int UnknownKinetic = -2;
        /// <summary><c>KineticsType.Heal</c>.</summary>
        public const int HealKinetic = 3;

        private float _lastBeamAt = -1000f;
        private float _waitingSince = -1f;
        private bool _missReported;
        private bool _unclaimed;

        public bool InEpisode { get; private set; }
        public int Beams { get; private set; }
        public int Heals { get; private set; }
        public int FromActor { get; private set; }
        public int Kinetic { get; private set; } = UnknownKinetic;

        /// <summary>A beam landed on you. True when it starts a new episode (log that one).</summary>
        public bool OnBeam(float now, int fromActor, int kinetic)
        {
            var fresh = !InEpisode || now - _lastBeamAt > EpisodeGap;
            if (fresh)
            {
                InEpisode = true;
                Beams = 0;
                Heals = 0;
                _missReported = false;
                _waitingSince = -1f;
            }
            Beams++;
            _lastBeamAt = now;
            FromActor = fromActor;
            Kinetic = kinetic;
            if (_waitingSince < 0f) _waitingSince = now;
            _unclaimed = true;
            return fresh;
        }

        /// <summary>Whether a heal now belongs to the beam: a heal kinetic (or one not read), at most <see cref="Window"/> after it.</summary>
        public bool Covers(float now) =>
            InEpisode && now >= _lastBeamAt && now - _lastBeamAt <= Window
            && (Kinetic == HealKinetic || Kinetic == UnknownKinetic);

        /// <summary>
        /// Takes the latest beam for a heal. True once per beam; false when there was none or the
        /// heal it caused already took it.
        /// </summary>
        public bool Claim()
        {
            if (!_unclaimed) return false;
            _unclaimed = false;
            return true;
        }

        /// <summary>A heal was put down to the staff.</summary>
        public void OnHeal()
        {
            if (!InEpisode) return;
            Heals++;
            _waitingSince = -1f;
        }

        /// <summary>
        /// Once a frame. <c>missed</c>: a beam went <see cref="Window"/> without a heal, said once
        /// per episode. <c>ended</c>: no beam for <see cref="EpisodeGap"/>; the counts are still readable.
        /// </summary>
        public (bool missed, bool ended) Poll(float now)
        {
            var missed = false;
            if (_waitingSince >= 0f && now - _waitingSince > Window)
            {
                _waitingSince = -1f;
                if (!_missReported) { _missReported = true; missed = true; }
            }
            var ended = false;
            if (InEpisode && now - _lastBeamAt > EpisodeGap)
            {
                InEpisode = false;
                ended = true;
            }
            return (missed, ended);
        }

        public void Clear()
        {
            InEpisode = false;
            _waitingSince = -1f;
            _lastBeamAt = -1000f;
            _unclaimed = false;
        }
    }
}
