using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Valheim.SettingsGui;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **NvlbKeys** - gives this mod's hotkeys the same life as a vanilla one: a real
    /// <c>ZInput</c> button, a row on Valheim's own **Keyboard &amp; Mouse** settings page, and a
    /// rebind that survives a restart. Nothing here is a feature of its own - modules
    /// <see cref="Declare"/> their keys and read them with <see cref="Down"/> / <see cref="Held"/>;
    /// the module that owns the wiring calls <see cref="InstallPatches"/> once.
    ///
    /// Why this shape, and not "read a KeyCode from the config"
    /// -------------------------------------------------------
    /// A config-file KeyCode is invisible to the game. It cannot be rebound in game, it does not
    /// show up when the player looks at the key list, and it silently fights whatever vanilla
    /// already put on that key. A registered ZInput button fixes all three at once, because
    /// everything downstream (the settings page, the bind dialog, persistence, the "key already
    /// used" checks) is driven off ZInput's own <c>m_buttons</c> dictionary. So we do not build a
    /// key system - we join the one that is already there.
    ///
    /// Registration: why a postfix on ZInput.Reset()
    /// ---------------------------------------------
    /// <c>ZInput.Reset()</c> (ZInput.cs:1473) is <c>ClearButtons(); ResetKBMButtons();
    /// UpdateGamepadInputLayout(...)</c> - it **empties** <c>m_buttons</c> and rebuilds it from
    /// vanilla's own list. Anything we added is gone. Rather than chase every caller, we postfix
    /// <c>Reset()</c> itself and re-add our buttons at the end of every rebuild, which makes the
    /// registration self-healing.
    ///
    /// That one seam also buys persistence for free. <c>ZInput.Load()</c> (1491) calls
    /// <c>Reset()</c> first and *then* loops over every <c>Rebindable</c> button that has a
    /// <c>kbmBinding_&lt;Name&gt;</c> PlatformPrefs key and re-applies it (1514). Our buttons are
    /// registered as <c>Rebindable</c>, and our postfix has already put them back by the time that
    /// loop runs, so a player's rebind is restored exactly like a vanilla one. The write side is
    /// the mirror image: <c>ZInput.Save()</c> (1480) writes <c>kbmBinding_&lt;Name&gt;</c> for
    /// every <c>Rebindable</c> button, ours included, with no help from us.
    ///
    /// The settings page: a prefix on KeyboardMouseSettings.Initialize()
    /// -----------------------------------------------------------------
    /// The page draws its rows from a serialized <c>List&lt;KeySetting&gt; m_keys</c>;
    /// <c>Initialize()</c> calls <c>SetupKeys()</c>, which wires every entry's button to
    /// <c>OpenBindDialog(key)</c> and then writes each key label from
    /// <c>Localization.GetBoundKeyString(key.m_keyName)</c>. Appending our entries in a **prefix**
    /// on <c>Initialize()</c> therefore hands them to vanilla before it has looked at the list, and
    /// vanilla adopts them as its own - bind dialog, blocked-key check, label refresh and all.
    ///
    /// Appending (rather than inserting) is deliberate. <c>SetupKeys</c>'s gamepad-navigation
    /// arithmetic walks a <c>m_keyRows</c> x <c>m_keyCols</c> grid (13 x 2 for vanilla's 26 rows);
    /// its down/right branches are guarded by <c>num3 &lt; m_keys.Count</c> and its up/left
    /// branches can only reach indices that already exist, so extra entries on the end cannot make
    /// it index out of range. They land at <c>num2 &gt;= m_keyCols</c>, which simply means they get
    /// no right-navigation - acceptable for a gamepad user who is, by definition, not using these.
    ///
    /// <c>OnDestroy</c> clears <c>m_keys</c> and the whole Settings object is rebuilt on every
    /// open, so the injection re-runs each time and is guarded per instance by looking for an
    /// <c>NVLB_</c> row that is already there.
    ///
    /// Failure is never fatal
    /// ----------------------
    /// The page injection is one try/catch. If anything about the page has moved, we log the
    /// reason once, leave <c>m_keys</c> exactly as vanilla left it, and <see cref="Status"/> says
    /// <c>page=not-available</c>. The keys still work - <see cref="Down"/> and <see cref="Held"/>
    /// fall back to the raw configured <c>KeyCode</c> whenever the ZInput button is not there.
    /// Everything is inert on a dedicated server, which has neither a <c>ZInput.instance</c> nor a
    /// <c>KeyboardMouseSettings</c>.
    /// </summary>
    internal static class NvlbKeys
    {
        /// <summary>Prefix on every ZInput button name we own. Also how we recognise our own rows.</summary>
        public const string Prefix = "NVLB_";

        /// <summary>One declared hotkey. <see cref="DefaultKey"/> is a delegate, not a value, so a
        /// config edit changes the default the next time ZInput rebuilds its buttons.</summary>
        private sealed class Decl
        {
            public string Id;
            public string Name;          // "NVLB_" + Id - the ZInput button name
            public string Label;         // what the settings page row says this action is
            public Func<KeyCode> DefaultKey;
        }

        private static readonly List<Decl> Decls = new List<Decl>();
        private static readonly Dictionary<string, Decl> ById = new Dictionary<string, Decl>(StringComparer.Ordinal);

        /// <summary>Clash warnings already printed, as "id|VanillaButton", so a rebuild does not spam.</summary>
        private static readonly HashSet<string> WarnedClashes = new HashSet<string>(StringComparer.Ordinal);

        private static bool _patched;
        private static int _registerErrors;

        // Page state, for Status(). "" reason means "we have not seen the page yet".
        private static bool _pageRowsAdded;
        private static string _pageFailure = "";
        private static bool _pageFailureLogged;

        // ---- declaration --------------------------------------------------------------------

        /// <summary>
        /// Declare a rebindable hotkey. Safe to call more than once for the same
        /// <paramref name="id"/> - the latest label and default win and nothing is duplicated.
        /// The ZInput button name is <c>"NVLB_" + id</c>.
        ///
        /// <paramref name="defaultKey"/> is re-read on every re-registration (i.e. after every
        /// <c>ZInput.Reset()</c>), which is what makes a config edit change the DEFAULT while a
        /// player's rebind on the vanilla page still wins - the rebind is re-applied by
        /// <c>ZInput.Load()</c> after our postfix has put the default back.
        ///
        /// A default of <see cref="KeyCode.None"/> means "unbound": the button is still registered,
        /// so the player can give it a key on the settings page, but it never fires until they do.
        /// </summary>
        public static void Declare(string id, string label, Func<KeyCode> defaultKey)
        {
            if (string.IsNullOrEmpty(id) || defaultKey == null) return;
            try
            {
                Decl d;
                if (!ById.TryGetValue(id, out d))
                {
                    d = new Decl();
                    d.Id = id;
                    d.Name = Prefix + id;
                    Decls.Add(d);
                    ById[id] = d;
                }
                d.Label = string.IsNullOrEmpty(label) ? id : label;
                d.DefaultKey = defaultKey;

                // A module that declares late (config reload, a feature switched on at runtime)
                // still gets a live button; the normal path is ZInput not existing yet and the
                // Reset postfix picking this up on the first rebuild.
                if (ZInput.instance != null) RegisterOne(ZInput.instance, d);
            }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] Declare(" + id + ") failed: " + e.Message);
            }
        }

        // ---- reading (called every frame - must never throw) ----------------------------------

        /// <summary>
        /// True on the frame the bound key went down. Reads the ZInput button when it is
        /// registered, so a rebind on the vanilla page takes effect immediately; falls back to the
        /// raw configured KeyCode when it is not, so a module never loses its hotkey.
        /// </summary>
        public static bool Down(string id)
        {
            try
            {
                Decl d;
                if (!ById.TryGetValue(id, out d)) return false;
                if (IsRegistered(d)) return ZInput.GetButtonDown(d.Name);
                KeyCode k = d.DefaultKey();
                return k != KeyCode.None && ZInput.GetKeyDown(k, false);
            }
            catch { return false; }
        }

        /// <summary>Is it held right now? Same button-first, KeyCode-fallback rule as <see cref="Down"/>.</summary>
        public static bool Held(string id)
        {
            try
            {
                Decl d;
                if (!ById.TryGetValue(id, out d)) return false;
                if (IsRegistered(d)) return ZInput.GetButton(d.Name);
                KeyCode k = d.DefaultKey();
                return k != KeyCode.None && ZInput.GetKey(k, false);
            }
            catch { return false; }
        }

        /// <summary>
        /// What the key is bound to now, for HUD hints and <c>nvlb.status</c>: "Z", "LeftAlt", ""
        /// when unbound. Taken from the button's effective binding path rather than
        /// <c>GetBoundKeyString</c>, which returns localization tokens ("$button_lctrl") that are
        /// right for a TMP label but wrong for a log line.
        /// </summary>
        public static string Label(string id)
        {
            try
            {
                Decl d;
                if (!ById.TryGetValue(id, out d)) return "";
                var zi = ZInput.instance;
                if (zi != null)
                {
                    var def = zi.GetButtonDef(d.Name);
                    if (def != null) return PrettyPath(def.GetActionPath(true));
                }
                KeyCode k = d.DefaultKey();
                return k == KeyCode.None ? "" : k.ToString();
            }
            catch { return ""; }
        }

        /// <summary>
        /// One line for <c>nvlb.status</c> and the boot log: how many buttons are actually
        /// registered, whether the vanilla Keyboard &amp; Mouse page took our rows, and what
        /// everything is bound to right now.
        /// </summary>
        public static string Status()
        {
            try
            {
                if (Decls.Count == 0) return "[Keys] no hotkeys declared";

                int live = 0;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < Decls.Count; i++)
                {
                    var d = Decls[i];
                    if (IsRegistered(d)) live++;
                    if (sb.Length > 0) sb.Append(" ");
                    string bound = Label(d.Id);
                    sb.Append(d.Id).Append("=").Append(string.IsNullOrEmpty(bound) ? "(unbound)" : bound);
                }

                string page;
                if (_pageRowsAdded) page = "page=rows-added";
                else if (!string.IsNullOrEmpty(_pageFailure)) page = "page=not-available (" + _pageFailure + ")";
                else page = "page=not-seen-yet";

                return "[Keys] " + live + "/" + Decls.Count + " registered, " + page + "; " + sb;
            }
            catch (Exception e)
            {
                return "[Keys] status failed: " + e.Message;
            }
        }

        // ---- patching -------------------------------------------------------------------------

        /// <summary>
        /// Install the two hooks. Idempotent - the first module to call it wins. Throws loudly,
        /// naming the missing member, if a game update moved either target: a keybinding system
        /// that silently does nothing is worse than one that refuses to load.
        /// </summary>
        public static void InstallPatches(Harmony harmony)
        {
            if (harmony == null) throw new ArgumentNullException("harmony");
            if (_patched) return;

            var reset = AccessTools.Method(typeof(ZInput), "Reset", new Type[0]);
            Require(reset, "ZInput.Reset()");

            var init = AccessTools.Method(typeof(KeyboardMouseSettings), "Initialize", new Type[0]);
            Require(init, "Valheim.SettingsGui.KeyboardMouseSettings.Initialize()");

            // Compile-time bound, but the game we run against is not always the game we built
            // against - so check the field is still there rather than let SetupKeys NRE.
            if (AccessTools.Field(typeof(KeyboardMouseSettings), "m_keys") == null)
                throw new Exception("Valheim.SettingsGui.KeyboardMouseSettings.m_keys not found");

            harmony.Patch(reset, postfix: new HarmonyMethod(typeof(NvlbKeys), nameof(ZInputResetPostfix)));
            harmony.Patch(init, prefix: new HarmonyMethod(typeof(NvlbKeys), nameof(KbmInitializePrefix)));
            _patched = true;

            // ZInput.Awake may already have run (BepInEx load order, a config reload, a module
            // enabled at runtime): the postfix above only fires on the NEXT rebuild, so seed now.
            RegisterNow();
        }

        /// <summary>
        /// Register every declared button into a ZInput that already exists. No-op when there is
        /// none - a dedicated server never has one.
        /// </summary>
        public static void RegisterNow()
        {
            try
            {
                var zi = ZInput.instance;
                if (zi == null) return;
                for (int i = 0; i < Decls.Count; i++) RegisterOne(zi, Decls[i]);
            }
            catch (Exception e)
            {
                if (_registerErrors++ < 3)
                    Log.LogWarning("[Keys] RegisterNow failed (" + _registerErrors + "/3): " + e.Message);
            }
        }

        private static void Require(MethodBase m, string what)
        {
            if (m == null) throw new Exception(what + " not found - Valheim's input or settings code changed shape");
        }

        // ---- ZInput registration ---------------------------------------------------------------

        /// <summary>
        /// Runs at the end of every <c>ZInput.Reset()</c>, which has just wiped <c>m_buttons</c>.
        /// Re-adding here is what makes registration survive <c>Load()</c>, a controller layout
        /// change, and anything else that rebuilds the button table. Never throws: an exception out
        /// of a postfix on Reset would take the game's whole input system with it.
        /// </summary>
        private static void ZInputResetPostfix(ZInput __instance)
        {
            try
            {
                if (__instance == null) return;
                for (int i = 0; i < Decls.Count; i++) RegisterOne(__instance, Decls[i]);
            }
            catch (Exception e)
            {
                if (_registerErrors++ < 3)
                    Log.LogWarning("[Keys] re-register after ZInput.Reset failed (" + _registerErrors + "/3): " + e.Message);
            }
        }

        /// <summary>
        /// Add one button, idempotently. <c>AddButton</c> is <c>m_buttons.Add</c> underneath
        /// (ZInput.cs:2582), which throws on a duplicate key, so the ContainsKey guard is load
        /// bearing, not defensive.
        ///
        /// <c>rebindable: true</c> is what puts the button in <c>Save()</c>'s and <c>Load()</c>'s
        /// loops and lets <c>StartBindKey</c> accept it. <c>showHints: false</c> keeps it out of
        /// <c>GetBoundActionString</c>, which would otherwise try to render our action as the
        /// localization token "$settings_nvlb_..." in vanilla's key-hint strings.
        /// </summary>
        private static void RegisterOne(ZInput zi, Decl d)
        {
            if (zi == null || d == null) return;
            if (zi.m_buttons == null || zi.m_buttons.ContainsKey(d.Name)) return;

            KeyCode key = KeyCode.None;
            try { key = d.DefaultKey(); }
            catch (Exception e) { Log.LogWarning("[Keys] default for " + d.Id + " threw: " + e.Message); }

            // KeyCodeToPath is vanilla's own KeyCode -> input-system path conversion (ZInput.cs:2521);
            // it goes through TryKeyCodeToMouseButton / TryKeyCodeToKey, so we never carry a
            // translation table of our own that could drift from the game's.
            string path = ZInput.KeyCodeToPath(key, false);
            zi.AddButton(d.Name, path, false, false, true, 0f, 0f);

            WarnOnVanillaClash(zi, d, key, path);

            // A button added AFTER ZInput.Load() has run (Declare at runtime, RegisterNow at patch
            // time) has missed Load()'s rebind loop, so apply the saved binding ourselves. When we
            // are inside Reset() this is harmless - Load() is about to do exactly the same thing.
            try
            {
                string prefKey = "kbmBinding_" + d.Name;
                if (PlatformPrefs.HasKey(prefKey))
                {
                    string saved = PlatformPrefs.GetString(prefKey);
                    var def = zi.GetButtonDef(d.Name);
                    if (def != null && !string.IsNullOrEmpty(saved)) def.Rebind(saved);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] could not restore the saved binding for " + d.Id + ": " + e.Message);
            }
        }

        /// <summary>
        /// Warn - once per pair, per session - when our default sits on a key vanilla already owns.
        /// Compared against each vanilla button's ORIGINAL path (<c>GetActionPath(effective: false)</c>),
        /// not its effective one: the question is "does vanilla ship this key on that action", and
        /// that answer must not change because the player has rebound something.
        ///
        /// Non-rebindable buttons are checked too. They never appear on the settings page, so a
        /// clash with one of them (Chat on Enter, Hotbar1-8 on the digits, Escape) is the kind that
        /// is hardest to work out from in game - exactly the kind worth a log line.
        /// </summary>
        private static void WarnOnVanillaClash(ZInput zi, Decl d, KeyCode key, string path)
        {
            if (key == KeyCode.None || string.IsNullOrEmpty(path)) return;
            try
            {
                foreach (var def in zi.m_buttons.Values)
                {
                    if (def == null || def.Name == null) continue;
                    if (def.Name.StartsWith(Prefix, StringComparison.Ordinal)) continue;   // ours
                    if (def.Source == ZInput.InputSource.Gamepad) continue;

                    if (!string.Equals(def.GetActionPath(false), path, StringComparison.OrdinalIgnoreCase)) continue;

                    string mark = d.Id + "|" + def.Name;
                    if (!WarnedClashes.Add(mark)) continue;
                    Log.LogWarning("[Keys] default " + key + " for '" + d.Id + "' is also Valheim's own '" +
                                   def.Name + "' - both will fire. Change the default in the config, or " +
                                   "rebind one of them on the game's Keyboard & Mouse settings page.");
                }
            }
            catch { /* a clash warning is never worth breaking registration for */ }
        }

        private static bool IsRegistered(Decl d)
        {
            var zi = ZInput.instance;
            return zi != null && zi.m_buttons != null && zi.m_buttons.ContainsKey(d.Name);
        }

        /// <summary>
        /// "&lt;Keyboard&gt;/leftAlt" -&gt; "LeftAlt", "&lt;Keyboard&gt;/Z" -&gt; "Z",
        /// "&lt;Keyboard&gt;/None" -&gt; "". Only the first letter is touched: every input-system
        /// control name is already camelCase, so capitalising it lands on the KeyCode spelling.
        /// </summary>
        private static string PrettyPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            string s = slash >= 0 ? path.Substring(slash + 1) : path;
            if (s.Length == 0 || s == "None") return "";
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // ---- the vanilla Keyboard & Mouse page --------------------------------------------------

        /// <summary>
        /// Append our rows before <c>Initialize</c> reaches <c>SetupKeys()</c>. One try/catch: on
        /// any failure we log the reason once and leave <c>m_keys</c> untouched, because a settings
        /// page that throws is a settings page the player cannot close.
        /// </summary>
        private static void KbmInitializePrefix(KeyboardMouseSettings __instance)
        {
            try
            {
                InjectRows(__instance);
            }
            catch (Exception e)
            {
                PageFailed("injection threw: " + e.Message);
            }
        }

        private static void InjectRows(KeyboardMouseSettings page)
        {
            if (Decls.Count == 0) { PageFailed("no hotkeys declared"); return; }
            if (page == null) { PageFailed("no KeyboardMouseSettings"); return; }

            var keys = page.m_keys;
            if (keys == null || keys.Count < 2) { PageFailed("m_keys is empty - nothing to clone or measure"); return; }

            // Idempotent per instance: OnDestroy clears m_keys and every open builds a fresh page,
            // but Initialize could still be reached twice on one instance.
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] != null && keys[i].m_keyName != null &&
                    keys[i].m_keyName.StartsWith(Prefix, StringComparison.Ordinal)) return;
            }

            var donor = keys[keys.Count - 1];                 // bottom row of the last column
            if (donor == null || donor.m_keyTransform == null) { PageFailed("the last key row has no transform"); return; }

            RectTransform donorRt = donor.m_keyTransform;
            Transform parent = donorRt.parent;
            if (parent == null) { PageFailed("the key rows have no parent"); return; }

            // If the rows are laid out by Unity, parenting is the whole job. If they are placed by
            // hand (which is what vanilla's fixed 13x2 grid looks like), we have to measure.
            bool laidOut = parent.GetComponent<LayoutGroup>() != null;
            float pitch = laidOut ? 0f : MeasureRowPitch(keys, donorRt);
            Vector2 anchor = donorRt.anchoredPosition;

            // Row 0 is a heading, not a binding: it carries no KeySetting and its key button is
            // switched off, so vanilla never touches it and the player can see at a glance whose
            // rows these are.
            int placed = 0;
            var heading = CloneRow(donorRt, parent, "NVLB_KeyHeading", laidOut, anchor, pitch, placed++);
            if (heading == null) { PageFailed("could not clone a key row"); return; }
            DressRow(heading, "NoVikingLeftBehind", true);

            for (int i = 0; i < Decls.Count; i++)
            {
                var d = Decls[i];
                var row = CloneRow(donorRt, parent, "NVLB_KeyRow_" + d.Id, laidOut, anchor, pitch, placed++);
                if (row == null) continue;
                DressRow(row, d.Label, false);

                var ks = new KeySetting();
                ks.m_keyName = d.Name;
                ks.m_keyTransform = row;
                // InvalidKeyBind() iterates m_blockedButtons with no null check, so this must be a
                // real array; copying the donor's keeps our rows under the same rules as vanilla's.
                ks.m_blockedButtons = donor.m_blockedButtons == null
                    ? new KeyCode[0]
                    : (KeyCode[])donor.m_blockedButtons.Clone();
                keys.Add(ks);
            }

            if (!laidOut) GrowScrollContent(parent, Mathf.Abs(pitch) * placed);

            _pageRowsAdded = true;
            _pageFailure = "";
            Log.LogInfo("[Keys] added " + Decls.Count + " row(s) to the Keyboard & Mouse page" +
                        (laidOut ? " (layout group)" : " (measured pitch " + pitch.ToString("0.#") + ")"));
        }

        /// <summary>
        /// Clone the donor row and put it <paramref name="index"/> pitches below it. Only rows in
        /// the donor's own column are measured, so a two-column page cannot hand us the horizontal
        /// gap by mistake.
        /// </summary>
        private static RectTransform CloneRow(RectTransform donor, Transform parent, string name,
                                              bool laidOut, Vector2 anchor, float pitch, int index)
        {
            var go = UnityEngine.Object.Instantiate(donor.gameObject, parent, false);
            if (go == null) return null;
            go.name = name;
            go.SetActive(true);

            var rt = go.transform as RectTransform;
            if (rt == null) { UnityEngine.Object.Destroy(go); return null; }
            if (!laidOut) rt.anchoredPosition = new Vector2(anchor.x, anchor.y + pitch * (index + 1));
            return rt;
        }

        /// <summary>
        /// Set the row's action-name label, and clear the key label that <c>UpdateBindings</c> is
        /// about to write. The two TMP_Texts are told apart the way vanilla tells them apart: the
        /// key label is the one the Button owns (<c>GetComponentInChildren&lt;Button&gt;()
        /// .GetComponentInChildren&lt;TMP_Text&gt;()</c> in <c>UpdateBindings</c>), so the action
        /// name is any other one.
        /// </summary>
        private static void DressRow(RectTransform row, string label, bool heading)
        {
            var button = row.GetComponentInChildren<Button>(true);

            // Instantiate carries prefab-authored (persistent) onClick calls across, and their
            // targets are components outside the clone - on the real page. Assigning a fresh event
            // object is the only way to drop those; SetupKeys then adds the listener we do want.
            if (button != null) button.onClick = new Button.ButtonClickedEvent();

            var texts = row.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                var t = texts[i];
                if (t == null) continue;
                bool inButton = button != null && t.transform.IsChildOf(button.transform);
                if (inButton) t.text = "";                      // UpdateBindings fills this in
                else t.text = label;                            // the action name
            }

            // The heading is decoration: no KeySetting points at it, so hiding its button leaves a
            // plain label. (Localization.Localize only rewrites text containing '$', so a plain
            // label is never touched by the page's Localize component.)
            if (heading && button != null) button.gameObject.SetActive(false);
        }

        /// <summary>
        /// Vertical gap between two adjacent rows of the donor's column, as a negative number
        /// (downwards). Falls back to the row's own height when the column has fewer than two
        /// measurable rows.
        /// </summary>
        private static float MeasureRowPitch(List<KeySetting> keys, RectTransform donor)
        {
            var ys = new List<float>();
            float x = donor.anchoredPosition.x;
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                if (k == null || k.m_keyTransform == null) continue;
                if (k.m_keyTransform.parent != donor.parent) continue;
                if (Mathf.Abs(k.m_keyTransform.anchoredPosition.x - x) > 1f) continue;   // other column
                ys.Add(k.m_keyTransform.anchoredPosition.y);
            }

            ys.Sort();
            float best = 0f;
            for (int i = 1; i < ys.Count; i++)
            {
                float gap = ys[i] - ys[i - 1];
                if (gap < 0.5f) continue;                       // duplicate / coincident rows
                if (best == 0f || gap < best) best = gap;
            }
            if (best == 0f) best = Mathf.Max(donor.rect.height, 20f);
            return -best;
        }

        /// <summary>
        /// Make room in the scroll view for the rows we appended. Only needed when the rows are
        /// hand-placed; a layout group brings its own ContentSizeFitter.
        /// </summary>
        private static void GrowScrollContent(Transform parent, float added)
        {
            if (added <= 0f) return;
            var scroll = parent.GetComponentInParent<ScrollRect>();
            if (scroll == null || scroll.content == null) return;
            Vector2 size = scroll.content.sizeDelta;
            scroll.content.sizeDelta = new Vector2(size.x, size.y + added);
        }

        private static void PageFailed(string reason)
        {
            _pageRowsAdded = false;
            _pageFailure = reason;
            if (_pageFailureLogged) return;
            _pageFailureLogged = true;
            Log.LogWarning("[Keys] the Keyboard & Mouse page did not take our rows (" + reason +
                           "). The hotkeys still work from the config; they just cannot be rebound in game.");
        }

        private static BepInEx.Logging.ManualLogSource Log
        {
            get { return NoVikingLeftBehindPlugin.Log; }
        }
    }
}
