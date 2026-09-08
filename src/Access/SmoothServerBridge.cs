using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The **Network panel**: a handful of SmoothServer switches, reachable from this mod's
    /// settings tab, so a group can fix a bad network afternoon without anyone opening a shell.
    ///
    /// SmoothServer is a **separate mod** and there is deliberately no compile-time reference to
    /// it: this file finds it at runtime through <c>BepInEx.Bootstrap.Chainloader.PluginInfos</c>
    /// by GUID and reads its <see cref="ConfigEntryBase"/>s off the live
    /// <c>BaseUnityPlugin.Config</c>. NoVikingLeftBehind therefore builds, loads and behaves
    /// identically whether or not SmoothServer is installed - the panel simply does not exist when
    /// it is absent, and the tweak door refuses with <see cref="NotInstalled"/>.
    ///
    /// **The allowlist is the whole security model.** SmoothServer has ~60 settings; five of them
    /// are here, each with its section, key, expected CLR type, permitted values and tier written
    /// out by hand below. The server validates an incoming change against this table before
    /// anything else, so a crafted RPC cannot reach the other fifty-five knobs even from an admin
    /// - they stay a cfg-file / <c>cfg.py</c> job, which is the right home for them.
    ///
    /// Writes go through the same machinery as this mod's own settings (see
    /// <see cref="TweakDoor"/>): permission, validation, rate limit, a timestamped backup, a
    /// line-level edit of <c>Nosferatu.SmoothServer.cfg</c>, undo and the audit line. The entry is
    /// then set in memory, which is what makes the change instant: SmoothServer's own
    /// <c>SettingChanged</c> handlers run (a profile flip re-applies its whole preset table and
    /// logs it) and its own ServerSync pushes the new value to every client. Nothing in
    /// SmoothServer is modified, patched or reimplemented.
    /// </summary>
    internal static class SmoothServerBridge
    {
        public const string Guid = "Nosferatu.SmoothServer";

        /// <summary>Marks a setting id / wire section as belonging to SmoothServer, not to us.</summary>
        public const string Tag = "SS";
        public const string Prefix = Tag + ":";

        public const string NotInstalled = "SmoothServer is not installed on this server.";
        public const string NotAllowed = "That SmoothServer setting cannot be changed from this menu.";

        public const string PanelLabel = "Network (SmoothServer)";
        public const string PanelTheme = "Network";
        public const string PanelHint = "A few network switches, for when the server feels bad";

        /// <summary>SmoothServer's descriptions are long; the hover panel is not infinite.</summary>
        private const int DescriptionCap = 900;

        // ---- the allowlist ------------------------------------------------------------------

        private sealed class Allowed
        {
            public string Section;
            public string Key;
            public string Label;
            public string Hint;

            /// <summary>"bool" or "enum". Checked against the live entry's real CLR type.</summary>
            public string TypeName;

            /// <summary>The only values the server will accept. Null for a plain bool.</summary>
            public string[] Choices;

            /// <summary>Expected enum type name, so a rename in SmoothServer drops the row.</summary>
            public string EnumTypeName;

            public SettingTier Tier;

            /// <summary>True = this player's own machine; never sent to the server.</summary>
            public bool Local;

            /// <summary>Extra sentence appended to the hover text (ours, not SmoothServer's).</summary>
            public string Extra;

            /// <summary>
            /// True for a key that [Profiles] Profile drives. Changing it away from the value the
            /// preset dictates would be silently reverted by SmoothServer's own reload, so the
            /// door refuses it with a sentence that says what to do instead.
            /// </summary>
            public bool ProfileDriven;
        }

        /// <summary>
        /// Five switches, in the order they are shown. Matt's scope, verbatim: the handful that
        /// actually fix things, not the sixty knobs.
        /// </summary>
        private static readonly Allowed[] List =
        {
            new Allowed
            {
                Section = "Profiles", Key = "Profile", Label = "Network preset",
                TypeName = "enum", EnumTypeName = "NetProfile",
                Choices = new[] { "Default", "FastLink", "Custom" },
                Tier = SettingTier.Everyone,
                Hint = "Default is the safe choice if people rubber-band; FastLink is for strong " +
                       "PCs on good connections",
                Extra = "Everyone on the server runs the preset the server picks."
            },
            new Allowed
            {
                Section = "Compression", Key = "Enabled", Label = "Compression",
                TypeName = "bool", Tier = SettingTier.Everyone, ProfileDriven = true,
                Hint = "Squeeze the traffic between the server and everyone on it",
                Extra = "The network preset turns this on: to switch it off, set the preset to Custom first."
            },
            new Allowed
            {
                Section = "Map", Key = "Enabled", Label = "Shared map",
                TypeName = "bool", Tier = SettingTier.Everyone,
                Hint = "Everyone explores the same map, and shares pins",
                Extra = null
            },
            new Allowed
            {
                Section = "SmoothMotion", Key = "Enabled", Label = "Smooth motion (this PC)",
                TypeName = "bool", Tier = SettingTier.Everyone, Local = true,
                Hint = "How other players and mobs are drawn between updates, on your screen",
                Extra = "Written to your own SmoothServer config and to nobody else's. It changes " +
                        "nothing that is sent, stored or simulated - only what your machine draws."
            },
            new Allowed
            {
                Section = "General", Key = "EnforceClientMod", Label = "Require the client mod",
                TypeName = "bool", Tier = SettingTier.Admin,
                Hint = "Only players who have SmoothServer installed may join",
                Extra = "Also locks SmoothServer's shared settings to the server and its admins."
            }
        };

        // ---- finding SmoothServer, without referencing it -----------------------------------

        private static ConfigFile _cfg;
        private static object _plugin;
        private static string _version;
        private static FieldInfo _processingServerUpdate;
        private static bool _searched;
        private static List<SettingInfo> _rows;

        private static readonly SettingInfo[] None = new SettingInfo[0];

        /// <summary>Is SmoothServer loaded in THIS process, with a config we can read?</summary>
        public static bool Available { get { return Resolve() != null; } }

        public static string Version { get { Resolve(); return _version ?? "?"; } }

        /// <summary>SmoothServer's own cfg file. Null when it is not installed here.</summary>
        public static string CfgPath
        {
            get { var cfg = Resolve(); return cfg != null ? cfg.ConfigFilePath : null; }
        }

        public static ConfigFile Cfg { get { return Resolve(); } }

        /// <summary>The one header the settings tab prints above the five rows.</summary>
        public static string GroupHeader { get { return "SmoothServer " + Version; } }

        private static ConfigFile Resolve()
        {
            if (_cfg != null) return _cfg;
            try
            {
                PluginInfo info;
                if (!Chainloader.PluginInfos.TryGetValue(Guid, out info) || info == null) return null;

                var plugin = info.Instance as BaseUnityPlugin;
                if (plugin == null) return null;          // loaded but not (yet) instantiated

                var cfg = plugin.Config;
                if (cfg == null) return null;

                _plugin = plugin;
                _cfg = cfg;
                _version = info.Metadata != null && info.Metadata.Version != null
                    ? info.Metadata.Version.ToString()
                    : "?";

                if (!_searched)
                {
                    _searched = true;
                    NoVikingLeftBehindPlugin.Log.LogInfo("[Network] SmoothServer " + _version +
                        " is loaded here; its config is " + cfg.ConfigFilePath);

                    // The door writes that path directly. It should be the same BepInEx config
                    // directory as ours - say so out loud if it ever is not.
                    try
                    {
                        var ours = Path.GetDirectoryName(NoVikingLeftBehindPlugin.Cfg.ConfigFilePath);
                        var theirs = Path.GetDirectoryName(cfg.ConfigFilePath);
                        if (!string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase))
                            NoVikingLeftBehindPlugin.Log.LogWarning("[Network] SmoothServer's config " +
                                "is not in the same directory as ours (" + theirs + " vs " + ours + ")");
                    }
                    catch { /* diagnostics only */ }
                }
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Network] could not look SmoothServer up: " + e.Message);
                return null;
            }
            return _cfg;
        }

        private static ConfigEntryBase Entry(string section, string key)
        {
            var cfg = Resolve();
            if (cfg == null) return null;
            foreach (var kv in cfg)
                if (kv.Key.Section == section && kv.Key.Key == key) return kv.Value;
            return null;
        }

        // ---- the five rows -------------------------------------------------------------------

        /// <summary>
        /// The panel's rows, built once. Each is an ordinary <see cref="SettingInfo"/> carrying a
        /// foreign <c>ConfigEntryBase</c>, so every row builder, tooltip and refresh path in the
        /// settings tab works on it unchanged.
        /// </summary>
        public static IList<SettingInfo> Rows()
        {
            if (_rows != null) return _rows;
            if (Resolve() == null) return None;

            var built = new List<SettingInfo>();
            foreach (var a in List)
            {
                var info = Build(a);
                if (info != null) built.Add(info);
            }
            _rows = built;

            NoVikingLeftBehindPlugin.Log.LogInfo("[Network] panel: " + built.Count + " of " +
                List.Length + " allowlisted SmoothServer settings resolved" +
                (built.Count == List.Length ? "" : " - the rest are not in this build of SmoothServer"));
            return _rows;
        }

        private static SettingInfo Build(Allowed a)
        {
            var entry = Entry(a.Section, a.Key);
            if (entry == null)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Network] SmoothServer has no [" + a.Section +
                    "] " + a.Key + " - that row is dropped");
                return null;
            }

            var t = entry.SettingType;
            if (a.TypeName == "bool" && t != typeof(bool))
            {
                Mismatch(a, t); return null;
            }
            if (a.TypeName == "enum")
            {
                if (!t.IsEnum || t.Name != a.EnumTypeName || !SameNames(a.Choices, Enum.GetNames(t)))
                {
                    Mismatch(a, t); return null;
                }
            }

            return new SettingInfo
            {
                Section = a.Section,
                Key = a.Key,
                Label = a.Label,
                Hint = a.Hint,
                Description = Describe(a, entry),
                Entry = entry,
                ValueType = t,
                TypeName = a.TypeName,
                Choices = a.Choices,
                Tier = a.Tier,
                Live = true,
                IsLocal = a.Local,
                FreeText = false,
                Owner = null,
                ModuleName = "SmoothServer",
                Theme = PanelTheme,
                ModuleHint = PanelHint,
                IsEnabledToggle = false,
                ForeignTag = Tag
            };
        }

        private static void Mismatch(Allowed a, Type actual)
        {
            NoVikingLeftBehindPlugin.Log.LogWarning("[Network] SmoothServer's [" + a.Section + "] " +
                a.Key + " is a " + actual.Name + ", not the " + a.TypeName +
                " this panel expects - that row is dropped rather than guessed at");
        }

        private static bool SameNames(string[] want, string[] got)
        {
            if (want == null || got == null || want.Length != got.Length) return false;
            foreach (var w in want)
            {
                bool hit = false;
                foreach (var g in got) if (string.Equals(w, g, StringComparison.Ordinal)) { hit = true; break; }
                if (!hit) return false;
            }
            return true;
        }

        /// <summary>SmoothServer's own ConfigDescription, trimmed to fit a hover panel.</summary>
        private static string Describe(Allowed a, ConfigEntryBase entry)
        {
            string d = null;
            try { d = entry.Description != null ? entry.Description.Description : null; }
            catch { /* best effort */ }

            if (!string.IsNullOrEmpty(d) && d.Length > DescriptionCap)
            {
                int cut = d.LastIndexOf(". ", DescriptionCap, StringComparison.Ordinal);
                d = (cut > DescriptionCap / 2 ? d.Substring(0, cut + 1) : d.Substring(0, DescriptionCap).TrimEnd()) + " ...";
            }

            string mine = "This is a SmoothServer setting, shown here so it can be changed in game." +
                          (string.IsNullOrEmpty(a.Extra) ? "" : " " + a.Extra);
            return string.IsNullOrEmpty(d) ? mine : d + "\n\n" + mine;
        }

        // ---- lookups -------------------------------------------------------------------------

        private static Allowed Lookup(string section, string key)
        {
            foreach (var a in List)
                if (string.Equals(a.Section, section, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        /// <summary>The row for a SmoothServer section/key, or null when it is not allowlisted.</summary>
        public static SettingInfo Find(string section, string key)
        {
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) return null;
            foreach (var info in Rows())
                if (string.Equals(info.Section, section, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(info.Key, key, StringComparison.OrdinalIgnoreCase)) return info;
            return null;
        }

        /// <summary>"SS:Section.Key" -> the row, or null.</summary>
        public static SettingInfo FindById(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            string rest = id.Substring(Prefix.Length);
            int dot = rest.IndexOf('.');
            if (dot <= 0 || dot == rest.Length - 1) return null;
            return Find(rest.Substring(0, dot), rest.Substring(dot + 1));
        }

        public static bool IsForeignSection(string section)
        {
            return section != null && section.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static string StripPrefix(string section)
        {
            return IsForeignSection(section) ? section.Substring(Prefix.Length) : section;
        }

        // ---- validation, on the server, before anything is written ---------------------------

        /// <summary>
        /// The allowlist's own check, run before <see cref="SettingValue.TryParse"/>: the pair must
        /// be on the list at all, and an enum value must be one of the strings written out above -
        /// not merely something that happens to parse into SmoothServer's enum today.
        /// Null when the value is acceptable, otherwise the sentence to show the player.
        /// </summary>
        public static string Validate(SettingInfo info, string raw)
        {
            if (info == null) return NotAllowed;
            var a = Lookup(info.Section, info.Key);
            if (a == null) return NotAllowed;
            if (a.Choices == null) return null;

            string v = (raw ?? "").Trim();
            foreach (var c in a.Choices)
                if (string.Equals(c, v, StringComparison.OrdinalIgnoreCase)) return null;

            return info.Label + ": expected one of " + string.Join(", ", a.Choices) + ".";
        }

        /// <summary>
        /// SmoothServer's preset re-asserts its table over the cfg file after every reload, so a
        /// change the preset owns would be silently undone a second later. Refuse it instead, and
        /// say what to do. Null when there is nothing in the way.
        /// </summary>
        public static string ProfileRefusal(SettingInfo info, string newValue)
        {
            if (info == null) return null;
            var a = Lookup(info.Section, info.Key);
            if (a == null || !a.ProfileDriven) return null;

            string profile = CurrentProfile();
            if (profile == null || string.Equals(profile, "Custom", StringComparison.OrdinalIgnoreCase))
                return null;

            // Both Default and FastLink force this key to true (see SmoothServer's Profiles table),
            // so a request for true is exactly what the preset wants and is allowed through.
            bool want;
            if (SettingValue.TryParseBool(newValue ?? "", out want) && want) return null;

            return "The network preset (" + profile + ") sets " + info.Label +
                   ". Change the preset to Custom first.";
        }

        /// <summary>
        /// A SmoothServer module that booted with Enabled=false never installed its Harmony
        /// patches, exactly like ours: turning it back on needs a restart. Read straight off
        /// SmoothServer's own module list so the answer is its truth, not a guess.
        /// Null when the change will actually take effect.
        /// </summary>
        public static string RestartRefusal(SettingInfo info, string newValue)
        {
            string status = StatusForOn(info, newValue);
            if (status == null) return null;
            if (status.StartsWith("FAILED", StringComparison.Ordinal))
                return "SmoothServer could not start that feature on this server.";
            if (string.Equals(status, "disabled", StringComparison.Ordinal))
                return "Needs a server restart.";
            return null;
        }

        /// <summary>The same question for a per-player row, worded for a player's own game.</summary>
        public static string LocalRestartNote(SettingInfo info, string newValue)
        {
            string status = StatusForOn(info, newValue);
            if (status == null) return null;
            if (status.StartsWith("FAILED", StringComparison.Ordinal))
                return "SmoothServer could not start that feature on this machine.";
            if (string.Equals(status, "disabled", StringComparison.Ordinal))
                return "Saved - restart Valheim for it to take effect.";
            return null;
        }

        private static string StatusForOn(SettingInfo info, string newValue)
        {
            if (info == null || !string.Equals(info.Key, "Enabled", StringComparison.Ordinal)) return null;
            bool want;
            if (!SettingValue.TryParseBool(newValue ?? "", out want) || !want) return null;
            return ModuleStatus(info.Section);
        }

        /// <summary>
        /// SmoothServer's <c>FeatureModule.Status</c> for the module owning a section
        /// ("applied" / "disabled" / "disabled(side)" / "FAILED(...)"), by reflection over its
        /// plugin's own module list. Null whenever the shape is not what we expect - never a
        /// reason to block a change.
        /// </summary>
        private static string ModuleStatus(string section)
        {
            try
            {
                if (Resolve() == null || _plugin == null) return null;
                var field = _plugin.GetType().GetField("Modules",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var modules = field != null ? field.GetValue(null) as System.Collections.IEnumerable : null;
                if (modules == null) return null;

                foreach (var m in modules)
                {
                    if (m == null) continue;
                    var mt = m.GetType();
                    var sectionProp = mt.GetProperty("Section", BindingFlags.Instance | BindingFlags.Public);
                    var sec = sectionProp != null ? sectionProp.GetValue(m, null) as string : null;
                    if (!string.Equals(sec, section, StringComparison.Ordinal)) continue;

                    var statusField = mt.GetField("Status", BindingFlags.Instance | BindingFlags.Public);
                    return statusField != null ? statusField.GetValue(m) as string : null;
                }
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Network] could not read SmoothServer's " +
                    "module status for [" + section + "]: " + e.Message);
            }
            return null;
        }

        /// <summary>[Profiles] Profile as it stands right now, or null if it cannot be read.</summary>
        public static string CurrentProfile()
        {
            var info = Find("Profiles", "Profile");
            return info != null ? info.CurrentString : null;
        }

        // ---- writing -------------------------------------------------------------------------

        /// <summary>
        /// Write one allowlisted key to SmoothServer's cfg file (timestamped backup first, exactly
        /// as <c>cfg.py</c> and this mod's own door do), then set the live entry so the change is
        /// instant.
        ///
        /// Setting the entry is what makes SmoothServer do the work: its own SettingChanged
        /// handlers run (a profile flip re-applies its whole preset table and logs the changes) and
        /// its own ServerSync pushes the value to every client. Its <c>SaveOnConfigSet</c> is
        /// deliberately left alone here - a profile flip writes nine other keys, and letting
        /// SmoothServer save its own file is what keeps the file and its memory in step.
        /// </summary>
        /// <param name="perPlayer">
        /// True for the one machine-local row. It suppresses SmoothServer's ServerSync broadcast
        /// for the duration of the assignment, so "this PC" really means this PC.
        /// </param>
        public static void Write(SettingInfo info, object parsed, string canonical, bool perPlayer)
        {
            var cfg = Resolve();
            if (cfg == null) throw new Exception(NotInstalled);

            string backup = CfgFile.SetValue(cfg.ConfigFilePath, info.Section, info.Key, canonical);

            bool suppressed = perPlayer && SuppressBroadcast(true);
            try { info.Entry.BoxedValue = parsed; }
            finally { if (suppressed) SuppressBroadcast(false); }

            NoVikingLeftBehindPlugin.Log.LogInfo("[Network] wrote SmoothServer [" + info.Section + "] " +
                info.Key + " = " + canonical + "  (backup " + backup + ")");
        }

        /// <summary>
        /// Flip SmoothServer's own <c>ServerSync.ConfigSync.ProcessingServerUpdate</c>, which its
        /// AddConfigEntry hook checks before broadcasting a locally-changed synced entry. It is a
        /// different type from the ServerSync this mod vendors, so it can only be reached through
        /// SmoothServer's own assembly. Best effort: a failure just means the value is offered to
        /// the server, which is free to ignore it.
        /// </summary>
        private static bool SuppressBroadcast(bool on)
        {
            try
            {
                if (_processingServerUpdate == null)
                {
                    if (_plugin == null) return false;
                    var t = _plugin.GetType().Assembly.GetType("ServerSync.ConfigSync");
                    if (t == null) return false;
                    _processingServerUpdate = t.GetField("ProcessingServerUpdate",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_processingServerUpdate == null || _processingServerUpdate.FieldType != typeof(bool))
                    { _processingServerUpdate = null; return false; }
                }
                _processingServerUpdate.SetValue(null, on);
                return true;
            }
            catch { return false; }
        }

        // ---- the panel's one button ------------------------------------------------------------

        /// <summary>
        /// "Reset network to Default": three ordinary door calls, in the order that makes them all
        /// legal (the preset first, so the compression row is no longer preset-driven away from
        /// what we are about to ask for). Everything else - permission, audit, undo - is the same
        /// as clicking the three rows by hand.
        /// </summary>
        public static void RequestResetToDefault()
        {
            Ask("Profiles", "Profile", "Default");
            Ask("Compression", "Enabled", "true");
            Ask("Map", "Enabled", "true");
        }

        private static void Ask(string section, string key, string value)
        {
            var info = Find(section, key);
            if (info != null) TweakDoor.Request(info, value);
        }

        /// <summary>One line for the settings tab's tooltip and the self-test's log.</summary>
        public static string AllowlistLine()
        {
            var parts = new List<string>();
            foreach (var a in List)
                parts.Add(a.Section + "." + a.Key + "(" + a.TypeName +
                          (a.Choices != null ? " " + string.Join("|", a.Choices) : "") +
                          (a.Local ? " per-player" : " " + a.Tier) + ")");
            return string.Join(", ", parts.ToArray());
        }
    }
}
