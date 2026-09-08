using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// A small control that lets a player set a key-type setting by pressing the key itself,
    /// instead of typing a Unity <c>KeyCode</c> name into a text field by hand.
    ///
    /// Every key/modifier setting in this mod is a plain free-text string that some module's own
    /// <c>ParseKey</c> turns into a <c>KeyCode</c> with a bare
    /// <c>Enum.Parse(typeof(KeyCode), s, true)</c> - see <c>LoadoutsModule.ParseKey</c>
    /// (Loadout1Key, Loadout2Key, SaveModifier), <c>DualPowersModule.ParseKey</c> (SecondSlotKey,
    /// ThirdSlotKey, SecondSlotModifier, ThirdSlotModifier, Slot1Modifier),
    /// <c>CorpseRunPlusModule.ParseKey</c> (ClearGraveKey) and the inline parse in
    /// <c>RepairAllModule.ParseSettings</c> (Hotkey). The one exception is
    /// <c>CraftFromChestsModule.ParseKey</c> (ToggleKey), whose own bind description documents a
    /// second, richer form: "optional modifiers then the key, joined by '+'" (its default is
    /// "LeftAlt+O"). A '+' fed into any of the single-KeyCode parsers is not a valid enum name,
    /// Enum.Parse throws, and every one of those modules catches that by silently turning the
    /// hotkey off rather than surfacing an error - so this recorder must never emit a combination
    /// for a setting that cannot parse one back.
    ///
    /// <see cref="Build"/> is not handed the owning <see cref="SettingInfo"/> - its public surface
    /// takes only the current string value and a callback - so there is no id or description to
    /// key a lookup table off at build time. Instead each <see cref="Handle"/> infers, from the
    /// values it actually sees for that row, the two things that change how a press should be
    /// encoded:
    ///   * whether a bare modifier (no other key) is itself a complete binding. True for a
    ///     setting like SaveModifier, whose value is always one of the six modifier KeyCodes -
    ///     LoadoutsModule binds SaveModifier's default as "LeftAlt", and DualPowersModule binds
    ///     SecondSlotModifier/ThirdSlotModifier/Slot1Modifier as "LeftShift"/"LeftControl"/
    ///     "LeftShift". Detected the first time a value is seen that parses as one of those six
    ///     KeyCodes and nothing else.
    ///   * whether this setting's own parser accepts the "Mods+Key" combination form at all -
    ///     true only for ToggleKey today. Detected the first time a value is seen containing '+',
    ///     which can only happen for a setting whose own parser already split on '+' successfully
    ///     (a plain KeyCode name never contains one).
    /// Both flags latch on once true and are never cleared by a later "None"/cleared value, so a
    /// setting does not lose its classification the moment the player clears it.
    ///
    /// The "clear to none" form written back is the literal word "None": every parser above
    /// either treats an empty string as KeyCode.None, or accepts "None" through the very same
    /// Enum.Parse call that accepts every other KeyCode name (KeyCode.None is a real enum
    /// member) - and DualPowersModule already ships "None" as ThirdSlotKey's own default value,
    /// so it is not an invented token, it is one already live in this mod's config.
    /// </summary>
    internal static class KeyRecorder
    {
        // ---- what counts as a key-type setting ---------------------------------------------------

        /// <summary>
        /// True when this setting should be edited with a key recorder rather than a plain text
        /// field. Every key/modifier setting bound in this mod (Loadout1Key, Loadout2Key,
        /// SaveModifier, SecondSlotKey, ThirdSlotKey, SecondSlotModifier, ThirdSlotModifier,
        /// Slot1Modifier, ClearGraveKey, Hotkey, ToggleKey) is MACHINE-LOCAL free text, and every
        /// one of those keys ends in "Key" - including "Hotkey" - or contains "Modifier". No
        /// other free-text local setting in the mod matches that shape (colours end in "Color",
        /// prefab/item lists end in a plural or "Prefabs", CompassArrow does not end in "Key"),
        /// so the name is a safe, generic signal rather than a per-id allowlist that would need
        /// updating for every future hotkey.
        /// </summary>
        internal static bool IsKeySetting(SettingInfo info)
        {
            if (info == null) return false;
            if (info.TypeName != "string" || !info.FreeText || !info.IsLocal) return false;
            string key = info.Key;
            if (string.IsNullOrEmpty(key)) return false;
            return key.EndsWith("Key", StringComparison.OrdinalIgnoreCase) ||
                   key.IndexOf("Modifier", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---- candidate keys, cached once -----------------------------------------------------------

        private static KeyCode[] _candidates;

        /// <summary>Every KeyCode worth polling for a press: not None, not Escape (reserved for
        /// cancel), not a mouse button (this is a keyboard recorder, and a mouse click is how the
        /// player got here in the first place - capturing it would end the recording on its own
        /// opening click).</summary>
        private static KeyCode[] Candidates()
        {
            if (_candidates != null) return _candidates;
            var list = new List<KeyCode>(160);
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (k == KeyCode.None) continue;
                if (k == KeyCode.Escape) continue;
                if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6) continue;
                list.Add(k);
            }
            _candidates = list.ToArray();
            return _candidates;
        }

        private static readonly KeyCode[] Modifiers =
        {
            KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt,
            KeyCode.LeftShift, KeyCode.RightShift
        };

        private static bool IsModifierKey(KeyCode k)
        {
            for (int i = 0; i < Modifiers.Length; i++) if (Modifiers[i] == k) return true;
            return false;
        }

        private static bool TryParseKeyCode(string s, out KeyCode k)
        {
            k = KeyCode.None;
            if (string.IsNullOrEmpty(s)) return false;
            try { k = (KeyCode)Enum.Parse(typeof(KeyCode), s.Trim(), true); return true; }
            catch { return false; }
        }

        // ---- building ------------------------------------------------------------------------------

        /// <summary>
        /// Build the control into <paramref name="parent"/> - a rect the caller has already sized,
        /// the same shape <c>UiKit.Input</c> is normally dropped into. Two clones sit side by
        /// side: the main button (the bound key, or "Press a key..." while recording) and a
        /// narrow "x" to its right that clears the value. Returns null - like every UiKit factory
        /// - when there is nothing to clone from yet (<see cref="UiKit.Ready"/> false); callers
        /// already null-check that pattern for every other control on a row.
        /// </summary>
        internal static Handle Build(RectTransform parent, string current, Action<string> onPicked)
        {
            if (parent == null) return null;
            try
            {
                var mainButton = UiKit.Button(parent, "", UiKit.BaseFontSize * 0.9f);
                if (mainButton == null) return null;
                var mainRt = (RectTransform)mainButton.transform;
                mainRt.anchorMin = new Vector2(0f, 0f);
                mainRt.anchorMax = new Vector2(1f, 1f);
                mainRt.offsetMin = new Vector2(0f, 0f);
                mainRt.offsetMax = new Vector2(-30f, 0f);          // leaves room for the clear button

                var clearButton = UiKit.Button(parent, "x", UiKit.BaseFontSize * 0.9f);
                if (clearButton != null)
                {
                    var clearRt = (RectTransform)clearButton.transform;
                    clearRt.anchorMin = new Vector2(1f, 0f);
                    clearRt.anchorMax = new Vector2(1f, 1f);
                    clearRt.pivot = new Vector2(1f, 0.5f);
                    clearRt.offsetMin = new Vector2(-26f, 0f);
                    clearRt.offsetMax = new Vector2(0f, 0f);
                    UiKit.Tip(clearButton.gameObject, "Clear this hotkey.");
                }

                var handle = new Handle(mainButton, onPicked);
                handle.SetValue(current);

                mainButton.onClick.AddListener(delegate { handle.StartRecording(); });
                if (clearButton != null)
                    clearButton.onClick.AddListener(delegate { handle.ClearValue(); });

                return handle;
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[KeyRecorder] Build failed: " + e);
                return null;
            }
        }

        private static void SetCaption(Button button, string caption)
        {
            if (button == null) return;
            foreach (var txt in button.GetComponentsInChildren<TMP_Text>(true)) txt.text = caption;
        }

        /// <summary>"" or "None" (any case) displays as "None"; a stored "Mods+Key" string is
        /// already its own label - the format this class writes is the format it shows.</summary>
        private static string Pretty(string value)
        {
            if (string.IsNullOrEmpty(value)) return "None";
            string v = value.Trim();
            return v.Length == 0 || string.Equals(v, "None", StringComparison.OrdinalIgnoreCase) ? "None" : v;
        }

        // ---- the handle ------------------------------------------------------------------------------

        internal sealed class Handle
        {
            private readonly Button _main;
            private readonly Action<string> _onPicked;

            private string _current = "None";
            private bool _recording;

            // Latched the first time the evidence for either is seen; see the class remarks above
            // for exactly what that evidence is. Never un-set by a later "None"/cleared value.
            private bool _bareModifierOk;
            private bool _allowCombo;

            internal Handle(Button main, Action<string> onPicked)
            {
                _main = main;
                _onPicked = onPicked;
            }

            /// <summary>True while waiting for a key.</summary>
            internal bool Recording { get { return _recording; } }

            /// <summary>Show this value without raising onPicked - the caller uses this to refresh
            /// from config (a live push from the server, an Undo, another client's change).</summary>
            internal void SetValue(string value)
            {
                try
                {
                    _current = string.IsNullOrEmpty(value) ? "None" : value;
                    Classify(_current);
                    if (!_recording) SetCaption(_main, Pretty(_current));
                }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogError("[KeyRecorder] SetValue failed: " + e);
                }
            }

            /// <summary>Learn the two format flags from a value actually seen for this setting.</summary>
            private void Classify(string value)
            {
                if (string.IsNullOrEmpty(value)) return;
                if (!_allowCombo && value.IndexOf('+') >= 0) _allowCombo = true;
                if (!_bareModifierOk && value.IndexOf('+') < 0)
                {
                    KeyCode k;
                    if (TryParseKeyCode(value, out k) && IsModifierKey(k)) _bareModifierOk = true;
                }
            }

            internal void StartRecording()
            {
                try
                {
                    if (_main == null) return;
                    _recording = true;
                    SetCaption(_main, "Press a key...");
                }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogError("[KeyRecorder] StartRecording failed: " + e);
                }
            }

            /// <summary>The "x" button: writes back the same "None" every parser above accepts.</summary>
            internal void ClearValue()
            {
                try
                {
                    _recording = false;
                    Commit("None");
                }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogError("[KeyRecorder] ClearValue failed: " + e);
                }
            }

            /// <summary>Called every frame by the page's Update while a recording is in progress.
            /// A no-op, safely, whenever it is not - the caller does not need to guard the call.</summary>
            internal void Tick()
            {
                if (!_recording) return;
                try
                {
                    if (_main == null) { _recording = false; return; }   // the row was torn down under us

                    if (Input.GetKeyDown(KeyCode.Escape))
                    {
                        _recording = false;
                        SetCaption(_main, Pretty(_current));              // cancel: leaves the old value
                        return;
                    }

                    var candidates = Candidates();
                    for (int i = 0; i < candidates.Length; i++)
                    {
                        var k = candidates[i];
                        if (!Input.GetKeyDown(k)) continue;

                        if (IsModifierKey(k))
                        {
                            // Not a complete binding on its own, unless this IS a modifier-type
                            // setting - keep waiting for a non-modifier key otherwise.
                            if (!_bareModifierOk) continue;
                            Commit(k.ToString());
                            return;
                        }

                        Commit(_allowCombo ? ComboWith(k) : k.ToString());
                        return;
                    }
                }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogError("[KeyRecorder] Tick failed: " + e);
                    _recording = false;
                }
            }

            /// <summary>"Mods+Key", modifiers held at the moment <paramref name="main"/> went down,
            /// in the same "modifiers then the key" order CraftFromChestsModule.ParseKey expects
            /// (it does not care about the order of the modifiers themselves, only that the LAST
            /// '+'-separated part is the main key).</summary>
            private static string ComboWith(KeyCode main)
            {
                string mods = "";
                for (int i = 0; i < Modifiers.Length; i++)
                {
                    if (!Input.GetKey(Modifiers[i])) continue;
                    mods += (mods.Length > 0 ? "+" : "") + Modifiers[i];
                }
                return mods.Length > 0 ? mods + "+" + main : main.ToString();
            }

            private void Commit(string value)
            {
                _recording = false;
                _current = value;
                Classify(value);
                SetCaption(_main, Pretty(value));
                if (_onPicked != null) _onPicked(value);
            }
        }
    }
}
