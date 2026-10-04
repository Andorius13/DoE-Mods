using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace CustomAvatars
{
    /// <summary>
    /// Keyboard reads through Unity's Input System package.
    ///
    /// The 2026-09-27 game update switched the player's active input handling from the legacy
    /// Input Manager to the Input System, and in that mode every `UnityEngine.Input` call throws
    /// InvalidOperationException. The first session after the update logged it once and ran
    /// without a single hotkey, while the other mods that still polled legacy Input threw on
    /// every frame, 386,000 warnings each.
    ///
    /// Keys are named the way the legacy `KeyCode` enum names them (F4, PageUp, Home), so any
    /// key name already written into a settings file keeps meaning the same key. The names the
    /// two enums spell differently are translated in <see cref="Aliases"/>; everything else is
    /// the same word in both.
    ///
    /// Failures are logged once. No keyboard at all (`Keyboard.current` null) is not an error —
    /// a pad-only or headset-only session has none — and is said once, then re-checked quietly,
    /// since a keyboard plugged in later appears as a new device.
    /// </summary>
    public static class Hotkeys
    {
        private static readonly Dictionary<string, Key> Cache = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Legacy KeyCode names whose Input System `Key` is spelled differently.</summary>
        private static readonly Dictionary<string, Key> Aliases = new Dictionary<string, Key>(StringComparer.OrdinalIgnoreCase)
        {
            ["Return"] = Key.Enter,
            ["KeypadEnter"] = Key.NumpadEnter,
            ["LeftControl"] = Key.LeftCtrl,
            ["RightControl"] = Key.RightCtrl,
            ["LeftCommand"] = Key.LeftMeta,
            ["RightCommand"] = Key.RightMeta,
            ["LeftWindows"] = Key.LeftMeta,
            ["RightWindows"] = Key.RightMeta,
            ["LeftApple"] = Key.LeftMeta,
            ["RightApple"] = Key.RightMeta,
            ["BackQuote"] = Key.Backquote,
            ["Numlock"] = Key.NumLock,
            ["KeypadPeriod"] = Key.NumpadPeriod,
            ["KeypadDivide"] = Key.NumpadDivide,
            ["KeypadMultiply"] = Key.NumpadMultiply,
            ["KeypadMinus"] = Key.NumpadMinus,
            ["KeypadPlus"] = Key.NumpadPlus,
            ["KeypadEquals"] = Key.NumpadEquals,
        };

        private static Keyboard _keyboard;
        private static bool _dead;
        private static bool _saidNoKeyboard;

        /// <summary>
        /// Fetch this frame's keyboard. False when there is nothing to read: no keyboard, or the
        /// Input System itself failed, which is logged once and then left alone.
        /// </summary>
        public static bool BeginFrame()
        {
            _keyboard = null;
            if (_dead) return false;
            try
            {
                _keyboard = Keyboard.current;
            }
            catch (Exception e)
            {
                _dead = true;
                Core.Log.Warning($"Input System keyboard unavailable, hotkeys disabled: {e.GetType().Name}: {e.Message}");
                return false;
            }

            if (_keyboard == null)
            {
                if (!_saidNoKeyboard)
                {
                    _saidNoKeyboard = true;
                    Core.Log.Msg("Hotkeys: no keyboard attached yet (Keyboard.current is null) — will pick one up when it appears.");
                }
                return false;
            }

            if (_saidNoKeyboard)
            {
                _saidNoKeyboard = false;
                Core.Log.Msg("Hotkeys: keyboard found, hotkeys live.");
            }
            return true;
        }

        /// <summary>Was the named key pressed this frame? Legacy KeyCode names.</summary>
        public static bool Down(string name)
        {
            if (_keyboard == null || !TryResolve(name, out var key)) return false;
            try
            {
                var control = _keyboard[key];
                return control != null && control.wasPressedThisFrame;
            }
            catch (Exception e)
            {
                _dead = true;
                _keyboard = null;
                Core.Log.Warning($"Input System key read failed, hotkeys disabled: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        /// <summary>Is the named key held right now? Legacy KeyCode names.</summary>
        public static bool Held(string name)
        {
            if (_keyboard == null || !TryResolve(name, out var key)) return false;
            try
            {
                var control = _keyboard[key];
                return control != null && control.isPressed;
            }
            catch { return false; }
        }

        private static bool TryResolve(string name, out Key key)
        {
            key = Key.None;
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();
            if (Cache.TryGetValue(name, out key)) return true;
            if (Unknown.Contains(name)) return false;

            if (!Aliases.TryGetValue(name, out key))
            {
                // Legacy digits are Alpha0..Alpha9 and keypad keys Keypad0..; the new names
                // are Digit0.. and Numpad0...
                var mapped = name;
                if (mapped.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase)) mapped = "Digit" + mapped.Substring(5);
                else if (mapped.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase)) mapped = "Numpad" + mapped.Substring(6);
                if (!Enum.TryParse(mapped, true, out key) || key == Key.None)
                {
                    Unknown.Add(name);
                    Core.Log.Warning($"Hotkeys: `{name}` is not a key the Input System knows; that binding does nothing.");
                    return false;
                }
            }

            Cache[name] = key;
            return true;
        }
    }
}
