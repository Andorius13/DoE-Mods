using System;
using Descent.Recon;
using Descent.Run;
using Il2Cpp;
using Realm = Il2CppOthergate.Biome.Realm;

namespace Descent.Dungeon
{
    /// <summary>
    /// <c>DungeonBuilder.InitBuilder</c> picks the floor's <c>DungeonLayoutDef</c>, and with it the
    /// main path (how long the floor is) and the one set of gen settings (how branchy). Since the
    /// 2026-09-27 game update there is no mission length and no per-difficulty gen settings: every
    /// layout carries one main path and a difficulty-tier range, and <c>InitBuilder(refs, mode,
    /// realm, _difficultyTier, hazards, seed)</c> keeps only the layouts whose range contains the
    /// tier (a tier below zero, or no layout in range, skips the filter). So the tier is the
    /// length knob now, and this prefix pins it to the floor's tier. The live generation already
    /// passes <c>GameManager.DifficultyTier</c>, which the launcher sets; the lobby validation pass
    /// (<c>GenerateLayout</c>, tier -1 by default) would otherwise check a layout drawn from every
    /// tier. Only the client that generates (the host) runs it for a live floor; the validation
    /// pass runs it on whoever validates.
    /// </summary>
    public static class InitBuilderHook
    {
        /// <summary>The floor the next InitBuilder call belongs to, set around a validation pass.</summary>
        public static FloorSpec Pending;

        public static int Applied { get; private set; }

        public static void Install()
        {
            Hooks.Patch(typeof(DungeonBuilder), "InitBuilder",
                Hooks.Of(typeof(InitBuilderHook), nameof(InitBuilder_Prefix)), null, paramCount: 6);
        }

        private static FloorSpec Current() => Pending ?? RunSync.Floor;

        private static void InitBuilder_Prefix(DungeonBuilder __instance, GameMode _gameMode, Realm _realm,
                                               ref int _difficultyTier, int _randomSeed)
        {
            try
            {
                if (!ModConfig.Enabled.Value) return;
                var spec = Current();
                if (spec == null) return;
                if (_gameMode != GameMode.DungeonRaid || _randomSeed != spec.Seed)
                {
                    ReconLog.Line($"InitBuilder for another dungeon (mode {_gameMode}, seed {_randomSeed}); floor spec seed {spec.Seed} not applied.");
                    return;
                }
                var was = _difficultyTier;
                _difficultyTier = spec.Tier;
                Applied++;
                ReconLog.Line($"InitBuilder: floor {spec.Number} realm {_realm} seed {_randomSeed}: layout tier {was} -> {_difficultyTier}");
            }
            catch (Exception e) { Core.Log.Warning($"InitBuilder prefix threw: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
