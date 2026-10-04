using System;
using UnityEngine.InputSystem;

namespace LootOverhaul
{
    /// <summary>
    /// Keyboard hotkeys through the Input System package. The 2026-09-27 game update switched
    /// Unity's active input handling to the Input System, and every <c>UnityEngine.Input</c>
    /// call now throws InvalidOperationException (386k "Hotkey probe threw" lines in the first
    /// session). <c>Keyboard.current</c> is null while no keyboard is attached; that is not a
    /// failure, the keys just read as up. A failure is logged once, not per frame.
    /// </summary>
    public static class Hotkeys
    {
        private static bool _failed;

        public static bool Pressed(Key key)
        {
            if (_failed) return false;
            try
            {
                var kb = Keyboard.current;
                return kb != null && kb[key].wasPressedThisFrame;
            }
            catch (Exception e)
            {
                _failed = true;
                Core.Log.Warning($"Keyboard hotkeys unavailable ({e.GetType().Name}: {e.Message}); the bag still opens with the VR gesture.");
                return false;
            }
        }
    }
}
