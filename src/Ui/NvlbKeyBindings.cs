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
    /// One source of truth, and the trap that made two
    /// -----------------------------------------------
    /// That free persistence has a sharp edge, and it drew blood. <c>ZInput.Save()</c> runs on
    /// **every** press of OK in the settings window, whatever tab the player was on, and it writes
    /// the *effective* path of every rebindable button. So the first time a player ever closes
    /// Settings with OK, whatever our config said at that moment is frozen into
    /// <c>HKCU\Software\IronGate\Valheim</c> - not because they rebound anything, but because
    /// vanilla serialises the whole table. From then on <c>Load()</c> re-applies that frozen value
    /// *after* our postfix has put the config default back, so the config is dead: editing
    /// <c>Loadout1Key</c> changes a number nobody reads. Worse, a default that later moves in a
    /// release (LoadoutSave, LeftCtrl to LeftAlt) leaves the old key still in force while every
    /// message in game names the new one.
    ///
    /// The rule this file now enforces is therefore: **the ZInput binding is the only thing
    /// consulted at runtime, and the config supplies that binding's default only until a binding
    /// exists.** <see cref="Current"/> answers "what is actually in force"; <see cref="SetBinding"/>
    /// and <see cref="ClearBinding"/> are how anything changes it; <see cref="Down"/> and
    /// <see cref="Held"/> read the button and nothing else. The config can still win a fight it
    /// ought to win, and only that one: we remember, in <c>nvlbKeyDefault_&lt;Name&gt;</c>, which
    /// default we last reconciled against, so a saved binding that is merely a frozen copy of a
    /// default we have since changed is recognised for what it is and moved on. A saved binding
    /// that differs from that remembered default is a real rebind and is never touched.
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
    /// <c>page=not-available</c>. The keys still work; they just cannot be rebound from vanilla's
    /// page.
    ///
    /// If the button itself has gone missing from <c>m_buttons</c> - something else cleared the
    /// table without going through <c>Reset()</c> - <see cref="Down"/> re-registers it on the spot
    /// and reads it again, so registration heals rather than degrades. Only when that fails do we
    /// read a raw key, and even then we read <see cref="Current"/>, never the config value, so the
    /// answer cannot disagree with the binding. Everything is inert on a dedicated server, which
    /// has neither a <c>ZInput.instance</c> nor a <c>KeyboardMouseSettings</c>.
    /// </summary>
    internal static class NvlbKeys
    {
        /// <summary>Prefix on every ZInput button name we own. Also how we recognise our own rows.</summary>
        public const string Prefix = "NVLB_";

        /// <summary>Vanilla's own PlatformPrefs key for a saved binding - <c>ZInput.Save()</c>
        /// (ZInput.cs:1486) and <c>ZInput.Load()</c> (1514) both spell it this way.</summary>
        private const string BindingPref = "kbmBinding_";

        /// <summary>Ours, and ours alone: the config default we last reconciled this button
        /// against, stored as a <c>KeyCode</c> name. See the "one source of truth" note above -
        /// this is what tells a real rebind apart from a frozen copy of an old default.</summary>
        private const string DefaultPref = "nvlbKeyDefault_";

        /// <summary>One declared hotkey. <see cref="DefaultKey"/> is a delegate, not a value, so a
        /// config edit changes the default the next time ZInput rebuilds its buttons.</summary>
        private sealed class Decl
        {
            public string Id;
            public string Name;          // "NVLB_" + Id - the ZInput button name
            public string Label;         // what the settings page row says this action is
            public Func<KeyCode> DefaultKey;

            // Down() and Held() run every frame. On the rare path where the ZInput button is
            // missing they need the binding in force, and working that out reads PlatformPrefs -
            // so it is worked out once and then held until something actually changes it.
            public bool FallbackResolved;
            public KeyCode Fallback;

            // The config default this button's saved binding was last reconciled against, so a
            // config edit mid-session is noticed rather than waiting for the next launch.
            public bool ReconciledDefaultKnown;
            public KeyCode ReconciledDefault;
        }

        private static readonly List<Decl> Decls = new List<Decl>();
        private static readonly Dictionary<string, Decl> ById = new Dictionary<string, Decl>(StringComparer.Ordinal);

        /// <summary>Clash warnings already printed, as "id|VanillaButton", so a rebuild does not spam.</summary>
        private static readonly HashSet<string> WarnedClashes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Ids whose saved binding has already been reconciled against the remembered
        /// default this session. The check is a once-per-session decision, not a per-rebuild one:
        /// a rebind made after we have reconciled must never be second-guessed by the next
        /// <c>ZInput.Reset()</c>.</summary>
        private static readonly HashSet<string> Reconciled = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Ids whose first <see cref="Held"/> has been logged. <see cref="Down"/> logs
        /// every press; <see cref="Held"/> is true on every frame of a hold, so it logs once.</summary>
        private static readonly HashSet<string> HeldLogged = new HashSet<string>(StringComparer.Ordinal);

        private static bool _patched;
        private static int _registerErrors;
        private static int _healFailures;

        /// <summary>Vanilla's KeyCode -&gt; path conversion, run backwards and cached. Built from
        /// <c>ZInput.KeyCodeToPath</c> itself so it cannot drift from the game's own table.</summary>
        private static Dictionary<string, KeyCode> _pathToKey;
        private static string _nonePath;

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
        /// <c>ZInput.Reset()</c>). It is a DEFAULT and only a default: it decides the binding on
        /// the first run, and after that only when the saved binding turns out to be a frozen copy
        /// of a default we have since changed. A binding the player chose always wins. Read
        /// <see cref="Current"/> to find out what is actually in force; do not read the config
        /// value and assume.
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
                Invalidate(d);

                // A module that declares late (config reload, a feature switched on at runtime)
                // still gets a live button; the normal path is ZInput not existing yet and the
                // Reset postfix picking this up on the first rebuild.
                var zi = ZInput.instance;
                if (zi == null) return;

                if (!IsRegistered(d)) { RegisterOne(zi, d); return; }

                // Already live, and this call is a config reload telling us the default moved.
                // Reconcile against the new one, so editing the config is not something you have
                // to restart the game to see.
                KeyCode fresh = SafeDefault(d);
                if (d.ReconciledDefaultKnown && d.ReconciledDefault != fresh)
                {
                    Reconciled.Remove(d.Id);
                    ApplySavedBinding(zi, d, fresh);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] Declare(" + id + ") failed: " + e.Message);
            }
        }

        // ---- reading (called every frame - must never throw) ----------------------------------

        /// <summary>
        /// True on the frame the bound key went down. The ZInput button is the only thing asked:
        /// a rebind takes effect on the next frame, with no relaunch. If the button has gone
        /// missing from <c>m_buttons</c> we put it back and ask again; only if that fails do we
        /// read a raw key, and the key we read is <see cref="Current"/> - the binding in force -
        /// so the two answers can never diverge.
        ///
        /// Logs one line per press while we are chasing dead keys, naming which of the two paths
        /// answered. That is deliberately loud: the next log a player sends settles what fired.
        /// </summary>
        public static bool Down(string id)
        {
            try
            {
                Decl d;
                if (!ById.TryGetValue(id, out d)) return false;

                if (!IsRegistered(d)) Heal(d);

                if (IsRegistered(d))
                {
                    if (!ZInput.GetButtonDown(d.Name)) return false;
                    LogFired(d, "the ZInput binding");
                    return true;
                }

                KeyCode k = FallbackKey(d);
                if (k == KeyCode.None || !ZInput.GetKeyDown(k, false)) return false;
                LogFired(d, "the raw-key fallback (the ZInput button is not registered)");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Is it held right now? Same button-only, self-healing rule as <see cref="Down"/>. This is
        /// true on every frame of a hold, so unlike <see cref="Down"/> it logs once per key per
        /// session rather than once per frame.
        /// </summary>
        public static bool Held(string id)
        {
            try
            {
                Decl d;
                if (!ById.TryGetValue(id, out d)) return false;

                if (!IsRegistered(d)) Heal(d);

                if (IsRegistered(d))
                {
                    if (!ZInput.GetButton(d.Name)) return false;
                    if (HeldLogged.Add(d.Id)) LogHeld(d, "the ZInput binding");
                    return true;
                }

                KeyCode k = FallbackKey(d);
                if (k == KeyCode.None || !ZInput.GetKey(k, false)) return false;
                if (HeldLogged.Add(d.Id)) LogHeld(d, "the raw-key fallback (the ZInput button is not registered)");
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// The button has gone from <c>m_buttons</c> without a <c>Reset()</c> we could postfix.
        /// Put it back rather than quietly degrade to a raw key. Rate limited by failures, not by
        /// attempts, so a dedicated server (no <c>ZInput.instance</c>, ever) costs a handful of
        /// null checks and then nothing at all, while a genuine re-registration keeps its budget.
        /// </summary>
        private static void Heal(Decl d)
        {
            if (_healFailures >= 32) return;
            try
            {
                var zi = ZInput.instance;

                // No ZInput at all is not a failure, it is a dedicated server or a frame before
                // Awake. Costing it a null check forever is cheaper than spending the budget on it.
                if (zi == null || zi.m_buttons == null) return;

                RegisterOne(zi, d);
                if (IsRegistered(d))
                {
                    Log.LogWarning("[Keys] " + d.Name + " had vanished from ZInput's button table and " +
                                   "has been re-registered. Something rebuilt the buttons without going " +
                                   "through ZInput.Reset().");
                    return;
                }
                _healFailures++;
            }
            catch { _healFailures++; }
        }

        /// <summary>
        /// The binding in force, cached, for the per-frame path that has no ZInput button to ask.
        /// Anything that can change the answer calls <see cref="Invalidate"/>.
        /// </summary>
        private static KeyCode FallbackKey(Decl d)
        {
            if (d.FallbackResolved) return d.Fallback;
            d.Fallback = Current(d.Id);
            d.FallbackResolved = true;
            return d.Fallback;
        }

        private static void Invalidate(Decl d)
        {
            if (d != null) d.FallbackResolved = false;
        }

        private static void LogFired(Decl d, string via)
        {
            try { Log.LogInfo("[Keys] " + d.Name + " down, answered by " + via + " (" + Label(d.Id) + ")"); }
            catch { /* a diagnostic is never worth losing a keypress for */ }
        }

        private static void LogHeld(Decl d, string via)
        {
            try
            {
                Log.LogInfo("[Keys] " + d.Name + " held, answered by " + via + " (" + Label(d.Id) +
                            "). Logged once per key per session, since a hold is true every frame.");
            }
            catch { }
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
                KeyCode k = SafeDefault(d);
                return k == KeyCode.None ? "" : k.ToString();
            }
            catch { return ""; }
        }

        // ---- the binding, as a value anyone may read or write --------------------------------

        /// <summary>
        /// The key actually in force for <paramref name="id"/>: the live ZInput binding when the
        /// button exists (which is the saved binding, because <c>Load()</c> has already applied
        /// it), the saved binding when it does not, and the configured default only when neither
        /// says anything. <see cref="KeyCode.None"/> means unbound - or, rarely, bound to a control
        /// no <c>KeyCode</c> names, in which case <see cref="Label"/> still shows it correctly.
        /// </summary>
        public static KeyCode Current(string id)
        {
            try
            {
                Decl d;
                if (!ById.TryGetValue(id, out d)) return KeyCode.None;

                var zi = ZInput.instance;
                if (zi != null)
                {
                    var def = zi.GetButtonDef(d.Name);
                    if (def != null)
                    {
                        string p = def.GetActionPath(true);
                        if (!string.IsNullOrEmpty(p)) return KeyFromPath(p);
                    }
                }

                KeyCode saved;
                if (TryReadSaved(d.Name, out saved)) return saved;
                return SafeDefault(d);
            }
            catch { return KeyCode.None; }
        }

        /// <summary>
        /// Bind <paramref name="id"/> to <paramref name="key"/> and make it stick, the way vanilla
        /// does: rebind the live <c>InputAction</c> (<c>ButtonDef.Rebind</c>, ZInput.cs:188 - the
        /// same call the interactive rebind ends at), then write
        /// <c>kbmBinding_&lt;Name&gt;</c> from the button's effective path exactly as
        /// <c>ZInput.Save()</c>'s loop does (ZInput.cs:1486), and flush with
        /// <c>PlatformPrefs.Save()</c> the way <c>Settings.ApplyAndClose()</c> does
        /// (Settings.cs:133). We write our one key rather than calling <c>ZInput.Save()</c>
        /// wholesale, because that also deletes <c>gamepad_enabled</c>, rewrites
        /// <c>input_switching_mode</c> and nukes the legacy prefs - a lot of collateral for one
        /// hotkey, and it would commit a settings window the player has not pressed OK on yet.
        ///
        /// <see cref="KeyCode.None"/> is a legal argument and means "unbound"; it lands on
        /// <c>&lt;Keyboard&gt;/None</c>, which is vanilla's own spelling for an unbound button
        /// (<c>AddButton("CamZoomIn", KeyToPath(Key.None))</c>, ZInput.cs:2637).
        ///
        /// Returns false, with a log line, if the id is not declared or anything throws. Writing
        /// the pref is the part that must succeed; a missing <c>ZInput.instance</c> (a dedicated
        /// server, or before <c>Awake</c>) is not a failure - the binding is picked up on the next
        /// registration.
        /// </summary>
        public static bool SetBinding(string id, KeyCode key)
        {
            Decl d;
            if (string.IsNullOrEmpty(id) || !ById.TryGetValue(id, out d))
            {
                Log.LogWarning("[Keys] SetBinding(" + id + ") - no hotkey by that name is declared.");
                return false;
            }

            try
            {
                string path = SafePath(key);

                var zi = ZInput.instance;
                if (zi != null)
                {
                    if (!IsRegistered(d)) RegisterOne(zi, d);
                    var def = zi.GetButtonDef(d.Name);
                    if (def != null) def.Rebind(path);
                }

                // From here on this binding is a deliberate choice, so the reconcile in
                // ApplySavedBinding must leave it alone for the rest of the session, and the
                // remembered default moves to whatever the config says now.
                KeyCode cfg = SafeDefault(d);
                Reconciled.Add(d.Id);
                d.ReconciledDefault = cfg;
                d.ReconciledDefaultKnown = true;
                Invalidate(d);
                PlatformPrefs.SetString(BindingPref + d.Name, path);
                PlatformPrefs.SetString(DefaultPref + d.Name, cfg.ToString());
                PlatformPrefs.Save();

                Log.LogInfo("[Keys] " + d.Name + " is now bound to " +
                            (key == KeyCode.None ? "nothing" : key.ToString()) + ".");
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] SetBinding(" + id + ", " + key + ") failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Forget the saved binding for <paramref name="id"/> so the configured default applies
        /// again, and put that default in force straight away. We rebind to the default rather
        /// than call <c>ButtonDef.ResetBinding()</c>, because "reset" means the path baked in when
        /// the button was added, which is the default as it stood *then* - the very staleness this
        /// file exists to remove.
        /// </summary>
        public static bool ClearBinding(string id)
        {
            Decl d;
            if (string.IsNullOrEmpty(id) || !ById.TryGetValue(id, out d))
            {
                Log.LogWarning("[Keys] ClearBinding(" + id + ") - no hotkey by that name is declared.");
                return false;
            }

            try
            {
                KeyCode cfg = SafeDefault(d);

                Reconciled.Add(d.Id);
                d.ReconciledDefault = cfg;
                d.ReconciledDefaultKnown = true;
                Invalidate(d);
                PlatformPrefs.DeleteKey(BindingPref + d.Name);
                PlatformPrefs.SetString(DefaultPref + d.Name, cfg.ToString());
                PlatformPrefs.Save();

                var zi = ZInput.instance;
                if (zi != null)
                {
                    if (!IsRegistered(d)) RegisterOne(zi, d);
                    var def = zi.GetButtonDef(d.Name);
                    if (def != null) def.Rebind(SafePath(cfg));
                }

                Log.LogInfo("[Keys] " + d.Name + " is back on its configured default (" +
                            (cfg == KeyCode.None ? "nothing" : cfg.ToString()) + ").");
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] ClearBinding(" + id + ") failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// A release moved a config default. If the saved binding is still exactly
        /// <paramref name="oldDefault"/> then it was never a choice, only a copy of the default
        /// vanilla happened to serialise, so move it to <paramref name="newDefault"/>. A saved
        /// binding that is anything else is a real rebind and is left alone; no saved binding at
        /// all needs nothing doing, because the new default is already what applies.
        ///
        /// Safe before the module has declared the key and before ZInput exists: the pref is the
        /// durable part and is written either way.
        /// </summary>
        public static bool MigrateSavedBinding(string id, KeyCode oldDefault, KeyCode newDefault)
        {
            if (string.IsNullOrEmpty(id) || oldDefault == newDefault) return false;

            try
            {
                string name = Prefix + id;

                KeyCode saved;
                if (!TryReadSaved(name, out saved)) return false;
                if (saved != oldDefault) return false;

                string path = SafePath(newDefault);
                PlatformPrefs.SetString(BindingPref + name, path);
                PlatformPrefs.SetString(DefaultPref + name, newDefault.ToString());
                PlatformPrefs.Save();
                Reconciled.Add(id);

                Decl d;
                if (ById.TryGetValue(id, out d))
                {
                    d.ReconciledDefault = newDefault;
                    d.ReconciledDefaultKnown = true;
                    Invalidate(d);
                    var zi = ZInput.instance;
                    if (zi != null)
                    {
                        if (!IsRegistered(d)) RegisterOne(zi, d);
                        var def = zi.GetButtonDef(d.Name);
                        if (def != null) def.Rebind(path);
                    }
                }

                Log.LogInfo("[Keys] " + name + ": the saved binding was still the old default " +
                            oldDefault + ", so it has been moved to the new default " + newDefault +
                            ". Rebind it in game if you wanted the old key.");
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] MigrateSavedBinding(" + id + ") failed: " + e.Message);
                return false;
            }
        }

        // ---- the id <-> config setting table ---------------------------------------------------

        /// <summary>
        /// One row of the map between an NvlbKeys id and the config setting that supplies its
        /// default. Hand written on purpose: the default is a closure at the
        /// <see cref="Declare"/> call site, so there is nothing to read it out of, and eight rows
        /// in one place beat eight more arguments spread across six modules.
        /// </summary>
        private sealed class SettingRef
        {
            public string Id;
            public string Section;
            public string Key;
        }

        /// <summary>
        /// Derived by matching every <c>NvlbKeys.Declare(</c> call site to the <c>BindLocal</c>
        /// entry its default lambda reads. Keep it in step when a module adds a hotkey; a missing
        /// row costs a settings-tab row, not a working key.
        /// </summary>
        private static readonly SettingRef[] SettingTable =
        {
            // Chests: the setting is a chord ("LeftAlt+O"); only its last part is the declared key.
            new SettingRef { Id = "ChestToggle",  Section = "Chests",   Key = "ToggleKey" },
            new SettingRef { Id = "GraveDismiss", Section = "CorpseRun", Key = "ClearGraveKey" },
            new SettingRef { Id = "Loadout1",     Section = "Loadouts", Key = "Loadout1Key" },
            new SettingRef { Id = "Loadout2",     Section = "Loadouts", Key = "Loadout2Key" },
            new SettingRef { Id = "LoadoutSave",  Section = "Loadouts", Key = "SaveModifier" },
            new SettingRef { Id = "PowerSlot2",   Section = "Powers",   Key = "SecondSlotKey" },
            new SettingRef { Id = "PowerSlot3",   Section = "Powers",   Key = "ThirdSlotKey" },
            new SettingRef { Id = "Repair",       Section = "Repair",   Key = "Hotkey" }
        };

        /// <summary>
        /// The config setting that supplies this id's default, as "Section.Key" - the same shape
        /// <c>CfgFile</c> uses. Empty when the id has no row.
        /// </summary>
        public static string SettingFor(string id)
        {
            string section, key;
            return TrySettingFor(id, out section, out key) ? section + "." + key : "";
        }

        /// <summary>The same map, split, for callers that need the two halves separately.</summary>
        public static bool TrySettingFor(string id, out string section, out string key)
        {
            section = "";
            key = "";
            if (string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < SettingTable.Length; i++)
            {
                if (!string.Equals(SettingTable[i].Id, id, StringComparison.Ordinal)) continue;
                section = SettingTable[i].Section;
                key = SettingTable[i].Key;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The reverse: which hotkey does this config setting supply the default for? Empty when
        /// the setting is not a hotkey. Case insensitive, because a section name typed by a player
        /// into the tweak door is not guaranteed to match ours.
        /// </summary>
        public static string IdForSetting(string section, string key)
        {
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) return "";
            for (int i = 0; i < SettingTable.Length; i++)
            {
                if (!string.Equals(SettingTable[i].Section, section, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(SettingTable[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                return SettingTable[i].Id;
            }
            return "";
        }

        /// <summary>Every declared id, in declaration order. For a settings tab that wants a row per key.</summary>
        public static string[] Ids()
        {
            try
            {
                var ids = new string[Decls.Count];
                for (int i = 0; i < Decls.Count; i++) ids[i] = Decls[i].Id;
                return ids;
            }
            catch { return new string[0]; }
        }

        /// <summary>What this hotkey does, in words - the label its row carries on the settings page.</summary>
        public static string ActionLabel(string id)
        {
            Decl d;
            if (string.IsNullOrEmpty(id) || !ById.TryGetValue(id, out d)) return "";
            return d.Label ?? "";
        }

        /// <summary>The configured default, ignoring any saved binding. For "reset to default" copy.</summary>
        public static KeyCode Configured(string id)
        {
            Decl d;
            if (string.IsNullOrEmpty(id) || !ById.TryGetValue(id, out d)) return KeyCode.None;
            return SafeDefault(d);
        }

        /// <summary>Is there a saved binding at all, or is this key still running on its default?</summary>
        public static bool HasSavedBinding(string id)
        {
            KeyCode ignored;
            return !string.IsNullOrEmpty(id) && TryReadSaved(Prefix + id, out ignored);
        }

        // ---- KeyCode and path, in both directions -----------------------------------------------

        /// <summary>The default, with the module's lambda unable to take the frame down with it.</summary>
        private static KeyCode SafeDefault(Decl d)
        {
            if (d == null || d.DefaultKey == null) return KeyCode.None;
            try { return d.DefaultKey(); }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] default for " + d.Id + " threw: " + e.Message);
                return KeyCode.None;
            }
        }

        /// <summary>
        /// <c>ZInput.KeyCodeToPath</c> with a guarantee attached: never null, never empty. That
        /// matters because <c>ButtonDef</c>'s constructor does <c>path.Contains("Gamepad")</c>
        /// with no null check (ZInput.cs:120), so an empty path would take out
        /// <c>AddButton</c> and, from the Reset postfix, the game's whole input table with it.
        /// </summary>
        private static string SafePath(KeyCode key)
        {
            try
            {
                string p = ZInput.KeyCodeToPath(key, false);
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] KeyCodeToPath(" + key + ") threw: " + e.Message);
            }
            return NonePath();
        }

        /// <summary>Vanilla's spelling of "no key", cached. <c>KeyToPath(Key.None)</c>, i.e. "&lt;Keyboard&gt;/None".</summary>
        private static string NonePath()
        {
            if (!string.IsNullOrEmpty(_nonePath)) return _nonePath;
            try { _nonePath = ZInput.KeyCodeToPath(KeyCode.None, false); }
            catch { }
            if (string.IsNullOrEmpty(_nonePath)) _nonePath = "<Keyboard>/None";
            return _nonePath;
        }

        /// <summary>
        /// Path back to KeyCode. Built once by running vanilla's own <c>KeyCodeToPath</c> over
        /// every keyboard and mouse KeyCode, so there is no translation table of ours to drift.
        /// Compared case insensitively on purpose: we generate "&lt;Keyboard&gt;/LeftCtrl" while an
        /// interactive rebind writes the resolved control, "&lt;Keyboard&gt;/leftCtrl", and those
        /// are the same key.
        ///
        /// Everything vanilla cannot map falls through to "&lt;Keyboard&gt;/None", so that path is
        /// pinned to <see cref="KeyCode.None"/> and every other claimant on it is dropped.
        /// </summary>
        private static Dictionary<string, KeyCode> PathToKey()
        {
            if (_pathToKey != null) return _pathToKey;

            var map = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string none = NonePath();
                map[none] = KeyCode.None;

                Array all = Enum.GetValues(typeof(KeyCode));
                for (int i = 0; i < all.Length; i++)
                {
                    KeyCode kc = (KeyCode)all.GetValue(i);
                    if (kc == KeyCode.None) continue;
                    if (kc >= KeyCode.JoystickButton0) continue;      // gamepad paths are not ours to name

                    string p;
                    try { p = ZInput.KeyCodeToPath(kc, false); }
                    catch { continue; }

                    if (string.IsNullOrEmpty(p)) continue;
                    if (string.Equals(p, none, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!map.ContainsKey(p)) map[p] = kc;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("[Keys] could not build the path table: " + e.Message);
            }

            _pathToKey = map;
            return map;
        }

        /// <summary><see cref="KeyCode.None"/> for an unbound path, and for one no KeyCode names.</summary>
        private static KeyCode KeyFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return KeyCode.None;
            try
            {
                KeyCode k;
                return PathToKey().TryGetValue(path, out k) ? k : KeyCode.None;
            }
            catch { return KeyCode.None; }
        }

        /// <summary>The saved binding for a ZInput button name, or false when there is none.</summary>
        private static bool TryReadSaved(string name, out KeyCode key)
        {
            key = KeyCode.None;
            try
            {
                string pref = BindingPref + name;
                if (!PlatformPrefs.HasKey(pref)) return false;
                string path = PlatformPrefs.GetString(pref);
                if (string.IsNullOrEmpty(path)) return false;
                key = KeyFromPath(path);
                return true;
            }
            catch { return false; }
        }

        private static bool TryParseKeyCode(string text, out KeyCode key)
        {
            key = KeyCode.None;
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                key = (KeyCode)Enum.Parse(typeof(KeyCode), text, true);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// For <c>nvlb.status</c> and the boot log: how many buttons are actually registered,
        /// whether the vanilla Keyboard &amp; Mouse page took our rows, and then a line per key
        /// giving the three facts that a dead hotkey always turns out to be about - the configured
        /// default, the saved binding, and which of them is winning.
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
                    bool registered = IsRegistered(d);
                    if (registered) live++;

                    KeyCode cfg = SafeDefault(d);
                    KeyCode saved;
                    bool hasSaved = TryReadSaved(d.Name, out saved);
                    KeyCode now = Current(d.Id);

                    string winner;
                    if (!registered) winner = "nothing (the ZInput button is not registered)";
                    else if (hasSaved && saved == now && saved != cfg) winner = "the saved binding";
                    else if (hasSaved && saved == now) winner = "the saved binding, which agrees with the config";
                    else if (now == cfg) winner = "the config default";
                    else winner = "the live binding, which matches neither";

                    sb.Append("\n  ").Append(d.Id)
                      .Append(": in force=").Append(Describe(now))
                      .Append(", config=").Append(Describe(cfg))
                      .Append(", saved=").Append(hasSaved ? Describe(saved) : "none")
                      .Append(", setting=").Append(NameOrDash(SettingFor(d.Id)))
                      .Append(" -> ").Append(winner);
                }

                string page;
                if (_pageRowsAdded) page = "page=rows-added";
                else if (!string.IsNullOrEmpty(_pageFailure)) page = "page=not-available (" + _pageFailure + ")";
                else page = "page=not-seen-yet";

                return "[Keys] " + live + "/" + Decls.Count + " registered, " + page + ";" + sb;
            }
            catch (Exception e)
            {
                return "[Keys] status failed: " + e.Message;
            }
        }

        private static string Describe(KeyCode k)
        {
            return k == KeyCode.None ? "(unbound)" : k.ToString();
        }

        private static string NameOrDash(string s)
        {
            return string.IsNullOrEmpty(s) ? "-" : s;
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

            KeyCode key = SafeDefault(d);

            // KeyCodeToPath is vanilla's own KeyCode -> input-system path conversion (ZInput.cs:2521);
            // it goes through TryKeyCodeToMouseButton / TryKeyCodeToKey, so we never carry a
            // translation table of our own that could drift from the game's. KeyCode.None comes
            // back as "<Keyboard>/None", which is exactly what vanilla registers its own unbound
            // buttons with (CamZoomIn / CamZoomOut, ZInput.cs:2637) - a real string that resolves
            // to no control, so nothing here can throw on an unbound default.
            string path = SafePath(key);
            zi.AddButton(d.Name, path, false, false, true, 0f, 0f);
            Invalidate(d);

            WarnOnVanillaClash(zi, d, key, path);

            ApplySavedBinding(zi, d, key);
        }

        /// <summary>
        /// Put the saved binding back on a button we have just added, and - once per key per
        /// session - decide whether that saved binding deserves to win.
        ///
        /// A button added AFTER <c>ZInput.Load()</c> has run (a <see cref="Declare"/> at runtime,
        /// <see cref="RegisterNow"/> at patch time) has missed Load()'s rebind loop, so the restore
        /// has to happen here. Inside <c>Reset()</c> it is harmless - Load() is about to do the
        /// same thing.
        ///
        /// The reconcile is the interesting half. <c>ZInput.Save()</c> writes every rebindable
        /// button on every settings OK, so a saved binding is not evidence that anyone rebound
        /// anything - it is usually just the config default, frozen. We keep our own record of the
        /// default we last reconciled against; when the saved binding still equals that record and
        /// the config now says something else, the saved value was never a choice and the config
        /// wins. When it differs from the record, somebody bound that key on purpose and nothing
        /// we do here may touch it.
        /// </summary>
        private static void ApplySavedBinding(ZInput zi, Decl d, KeyCode cfgDefault)
        {
            try
            {
                var def = zi.GetButtonDef(d.Name);
                if (def == null) return;

                string bindingPref = BindingPref + d.Name;
                string savedPath = PlatformPrefs.HasKey(bindingPref) ? PlatformPrefs.GetString(bindingPref) : null;
                bool hasSaved = !string.IsNullOrEmpty(savedPath);

                if (Reconciled.Add(d.Id))
                {
                    string defaultPref = DefaultPref + d.Name;
                    KeyCode remembered;
                    bool haveRemembered = TryParseKeyCode(
                        PlatformPrefs.HasKey(defaultPref) ? PlatformPrefs.GetString(defaultPref) : null,
                        out remembered);

                    if (hasSaved && haveRemembered && remembered != cfgDefault &&
                        KeyFromPath(savedPath) == remembered)
                    {
                        savedPath = SafePath(cfgDefault);
                        PlatformPrefs.SetString(bindingPref, savedPath);
                        Log.LogInfo("[Keys] " + d.Name + ": the saved binding was still " + remembered +
                                    ", which is only the default this key used to carry, so the config's " +
                                    "new default " + (cfgDefault == KeyCode.None ? "nothing" : cfgDefault.ToString()) +
                                    " applies from now on. Rebind it in game if you wanted the old key.");
                    }

                    if (!haveRemembered || remembered != cfgDefault)
                    {
                        PlatformPrefs.SetString(defaultPref, cfgDefault.ToString());
                        PlatformPrefs.Save();
                    }

                    d.ReconciledDefault = cfgDefault;
                    d.ReconciledDefaultKnown = true;
                    Invalidate(d);
                }

                if (hasSaved) def.Rebind(savedPath);
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
