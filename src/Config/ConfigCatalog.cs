using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;

namespace NoVikingLeftBehind
{
    /// <summary>One setting, fully described. Built once at bind time, read by everything else.</summary>
    internal sealed class SettingInfo
    {
        public string Section;
        public string Key;
        public string Label;
        public string Hint;
        public string Description;

        public ConfigEntryBase Entry;
        public Type ValueType;

        /// <summary>bool | int | float | string | enum</summary>
        public string TypeName;

        public bool HasRange;
        public double Min, Max, Step;
        public string[] Choices;

        public SettingTier Tier;
        public bool Live;
        public bool IsLocal;
        public bool FreeText;

        /// <summary>Which view shows this row. See <see cref="SettingLevel"/>.</summary>
        public SettingLevel Level;

        /// <summary>Plain-language heading on the Simple view. Only set for Essential rows.</summary>
        public string SimpleGroup;

        /// <summary>Order within <see cref="SimpleGroup"/>, low first; ties keep bind order.</summary>
        public int SimpleOrder;

        /// <summary>Never list this row, in either view. Obsolete settings only.</summary>
        public bool HiddenAlways;

        /// <summary>When set, this list can be ticked off a list of what is in the world.</summary>
        public PickerSpec Picker;

        /// <summary>Null for the handful of plugin-level entries ([Frontier], [Tiers], [General]).</summary>
        public FeatureModule Owner;
        public string ModuleName;
        public string Theme;
        public string ModuleHint;

        /// <summary>True for the per-module "Enabled" toggle, which has its own restart rule.</summary>
        public bool IsEnabledToggle;

        /// <summary>
        /// Null for this mod's own settings. Otherwise the short tag of the plugin that really
        /// owns the entry - today only <see cref="SmoothServerBridge.Tag"/>. A foreign setting is
        /// never in the <see cref="ConfigCatalog"/>: it is built on demand from an allowlist and
        /// its <see cref="Entry"/> belongs to another plugin's <c>ConfigFile</c>.
        /// </summary>
        public string ForeignTag;

        /// <summary>True when this setting belongs to another mod (see <see cref="ForeignTag"/>).</summary>
        public bool Foreign { get { return ForeignTag != null; } }

        /// <summary>"Section.Key" - the id used everywhere (RPC, catalog lookups, cfg.py).
        /// A foreign setting carries its owner's tag, so ids can never collide with ours
        /// ([General] EnforceClientMod exists in both mods).</summary>
        public string Id
        {
            get { return ForeignTag == null ? Section + "." + Key : ForeignTag + ":" + Section + "." + Key; }
        }

        /// <summary>The section as it crosses the wire: prefixed for a foreign setting.</summary>
        public string WireSection
        {
            get { return ForeignTag == null ? Section : ForeignTag + ":" + Section; }
        }

        /// <summary>False when the owning module is switched off, so the UI can grey the rows.</summary>
        public bool IsModuleEnabled
        {
            get { return Owner == null || Owner.Enabled; }
        }

        /// <summary>Did the module install its patches at boot? Governs the Enabled-ON rule.</summary>
        public bool IsModuleApplied
        {
            get { return Owner == null || Owner.Applied; }
        }

        public string CurrentString
        {
            get
            {
                try
                {
                    // ALWAYS the boxed value, never GetSerializedValue(). ServerSync patches that
                    // getter (PreventSavingServerInfo) to hand back LocalBaseValue - this machine's
                    // *own* pre-sync value out of its own cfg file - for every synchronised entry
                    // while the config is locked, which is exactly how a client runs. That is right
                    // for saving the file and wrong for showing a value: it made the settings tab
                    // show a number nobody is running (0.9.0: [Chests] Range saved as 50 on the
                    // server, redrawn as 20 on the client, for ever). This was known for another
                    // mod's entries since the SmoothServer panel went in; it is just as true for
                    // ours. BoxedValue is always what is actually in force.
                    return ConfigCatalog.Serialize(Entry.BoxedValue, ValueType);
                }
                catch { return "?"; }
            }
        }

        /// <summary>
        /// Diagnostic only: what BepInEx would write to the cfg file for this entry. On a client
        /// with the config locked ServerSync substitutes this machine's own pre-sync value here,
        /// so it can disagree with <see cref="CurrentString"/> - which is the point of logging it.
        /// </summary>
        public string SerializedString
        {
            get { try { return Entry.GetSerializedValue(); } catch { return "?"; } }
        }

        public string DefaultString
        {
            get { return ConfigCatalog.Serialize(Entry.DefaultValue, ValueType); }
        }
    }

    /// <summary>
    /// Every NoVikingLeftBehind setting, with the metadata each one declared at bind time.
    ///
    /// Filled by <see cref="NoVikingLeftBehindPlugin.BindSynced"/> / <c>BindLocal</c> - the two
    /// helpers every module already goes through - so a setting cannot exist without appearing
    /// here. Three consumers read it and therefore always agree:
    ///   * the in-game settings tab (labels, hints, tooltips, control type, ranges, permissions),
    ///   * <c>nvlb.catalog</c> on a client and the catalog dump written at boot on a server,
    ///   * <see cref="TweakDoor"/>, which validates every incoming change against it.
    /// </summary>
    internal static class ConfigCatalog
    {
        private static readonly List<SettingInfo> _all = new List<SettingInfo>();
        private static readonly Dictionary<string, SettingInfo> _byId =
            new Dictionary<string, SettingInfo>(StringComparer.OrdinalIgnoreCase);

        public static IList<SettingInfo> All { get { return _all; } }

        // ---- registration -------------------------------------------------------------------

        internal static void Register(ConfigEntryBase entry, Opt opt, FeatureModule owner, bool isLocal)
        {
            if (entry == null) return;
            try
            {
                var info = Build(entry, opt, owner, isLocal);
                if (_byId.ContainsKey(info.Id))
                {
                    // Two modules sharing a section is legitimate (e.g. [Debug]); a duplicate key
                    // is not, and would make the door ambiguous.
                    NoVikingLeftBehindPlugin.Log.LogWarning(
                        "catalog: duplicate setting " + info.Id + " - the first one wins");
                    return;
                }
                _byId[info.Id] = info;
                _all.Add(info);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("catalog: could not describe " +
                    entry.Definition + ": " + e.Message);
            }
        }

        private static SettingInfo Build(ConfigEntryBase entry, Opt opt, FeatureModule owner, bool isLocal)
        {
            if (opt == null) opt = new Opt();

            var t = entry.SettingType;
            var info = new SettingInfo
            {
                Section = entry.Definition.Section,
                Key = entry.Definition.Key,
                Description = entry.Description != null ? entry.Description.Description : null,
                Entry = entry,
                ValueType = t,
                Tier = opt.Tier,
                Live = opt.Live,
                IsLocal = isLocal,
                FreeText = opt.FreeText,
                Picker = opt.Picker,
                Owner = owner,
                ModuleName = owner != null ? owner.Name : "General",
                Theme = owner != null ? owner.Theme : "Server",
                ModuleHint = owner != null ? owner.Hint : null,
                Hint = opt.Hint,
                Choices = opt.Choices,
                Level = opt.Level,
                SimpleGroup = opt.SimpleGroup,
                SimpleOrder = opt.SimpleOrder,
                HiddenAlways = opt.HiddenAlways
            };

            info.IsEnabledToggle = string.Equals(info.Key, "Enabled", StringComparison.Ordinal) && owner != null;
            info.Label = !string.IsNullOrEmpty(opt.Label) ? opt.Label : Humanise(info.Key);

            if (t == typeof(bool)) info.TypeName = "bool";
            else if (t == typeof(string)) info.TypeName = "string";
            else if (t.IsEnum)
            {
                info.TypeName = "enum";
                if (info.Choices == null) info.Choices = Enum.GetNames(t);
            }
            else if (t == typeof(int) || t == typeof(long) || t == typeof(short)) info.TypeName = "int";
            else if (t == typeof(float) || t == typeof(double)) info.TypeName = "float";
            else info.TypeName = t.Name.ToLowerInvariant();

            if (opt.HasRange && (info.TypeName == "int" || info.TypeName == "float"))
            {
                info.HasRange = true;
                info.Min = opt.Min;
                info.Max = opt.Max;
                info.Step = opt.Step > 0
                    ? opt.Step
                    : (info.TypeName == "int" ? 1 : NiceStep(opt.Min, opt.Max));
            }

            if (string.IsNullOrEmpty(info.Hint))
                info.Hint = FirstSentence(info.Description);

            return info;
        }

        /// <summary>A step that gives a slider ~100 usable notches without silly precision.</summary>
        private static double NiceStep(double min, double max)
        {
            double span = Math.Abs(max - min);
            if (span <= 0) return 1;
            if (span <= 2) return 0.01;
            if (span <= 20) return 0.1;
            if (span <= 200) return 1;
            return Math.Round(span / 100.0);
        }

        /// <summary>"RegrowDays" -> "Regrow days"; leaves ALLCAPS and existing spaces alone.</summary>
        internal static string Humanise(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            var sb = new StringBuilder(key.Length + 8);
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(key[i - 1]) && key[i - 1] != ' ')
                    sb.Append(' ').Append(char.ToLowerInvariant(c));
                else if (i > 0 && char.IsUpper(c) && i + 1 < key.Length &&
                         char.IsUpper(key[i - 1]) && char.IsLower(key[i + 1]))
                    sb.Append(' ').Append(char.ToLowerInvariant(c));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string FirstSentence(string description)
        {
            if (string.IsNullOrEmpty(description)) return null;
            int dot = description.IndexOf(". ", StringComparison.Ordinal);
            string s = dot > 0 ? description.Substring(0, dot + 1) : description;
            if (s.Length > 90) s = s.Substring(0, 87).TrimEnd() + "...";
            return s;
        }

        /// <summary>
        /// The exact text BepInEx would put in the cfg file for this value. It has to be BepInEx's
        /// own converter and not a hand-rolled ToString: every comparison in the door and the UI
        /// ("is it already this?", "is it back at its default?") compares this against
        /// ConfigEntryBase.GetSerializedValue(), and a float formatted two different ways would
        /// make those silently wrong.
        /// </summary>
        internal static string Serialize(object value, Type type)
        {
            if (value == null) return "";

            // Strings go through RAW, deliberately. BepInEx's own converter escapes them for a
            // TOML-ish quoted form, and because the door reads a value back, re-serialises it and
            // writes it again, every save escaped what the last save had already escaped:
            // 1,2 typed with quotes became "1,2", then \"1,2\", then \\"1,2\\", and the module's
            // parser gave up on it. A cfg value is a raw line to the end of the line - a quote in
            // one is nothing special - so there is nothing here to escape in the first place.
            var s = value as string;
            if (s != null) return Unquote(s);

            try { return TomlTypeConverter.ConvertToString(value, type ?? value.GetType()); }
            catch
            {
                if (value is float) return ((float)value).ToString(CultureInfo.InvariantCulture);
                if (value is double) return ((double)value).ToString(CultureInfo.InvariantCulture);
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Peel the escaping off a value that an earlier build wrote. It strips one surrounding
        /// pair of quotes - plain or backslash-escaped - and the backslashes in front of any that
        /// are left, repeatedly, because the old write path could nest them several deep. A value
        /// that never had quotes comes back untouched, so this is safe to run on every read and
        /// every write: it is a migration that costs nothing once the file is clean.
        /// </summary>
        internal static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            for (int guard = 0; guard < 8; guard++)
            {
                string before = s;
                s = s.Trim();

                if (s.Length >= 4 && s.StartsWith("\\\"") && s.EndsWith("\\\""))
                    s = s.Substring(2, s.Length - 4);
                else if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                    s = s.Substring(1, s.Length - 2);
                else if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'')
                    s = s.Substring(1, s.Length - 2);

                if (s.IndexOf("\\\"", StringComparison.Ordinal) >= 0)
                    s = s.Replace("\\\"", "\"");

                if (s == before) break;
            }
            return s;
        }

        // ---- lookups ------------------------------------------------------------------------

        public static SettingInfo Find(string section, string key)
        {
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) return null;
            SettingInfo info;
            return _byId.TryGetValue(section + "." + key, out info) ? info : null;
        }

        public static SettingInfo Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            SettingInfo info;
            if (_byId.TryGetValue(id, out info)) return info;
            // tolerate "Key" alone when it is unambiguous, the way cfg.py does
            SettingInfo hit = null;
            foreach (var s in _all)
            {
                if (!string.Equals(s.Key, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (hit != null) return null;
                hit = s;
            }
            return hit;
        }

        /// <summary>Distinct modules in display order: theme, then module name.</summary>
        public static List<FeatureModule> ModulesForUi()
        {
            var seen = new HashSet<string>();
            var list = new List<FeatureModule>();
            foreach (var s in _all)
            {
                if (s.Owner == null) continue;
                if (!seen.Add(s.Owner.Name)) continue;
                list.Add(s.Owner);
            }
            list.Sort(delegate (FeatureModule a, FeatureModule b)
            {
                int t = string.CompareOrdinal(ThemeOrder(a.Theme) + a.Theme, ThemeOrder(b.Theme) + b.Theme);
                return t != 0 ? t : string.CompareOrdinal(a.Name, b.Name);
            });
            return list;
        }

        private static string ThemeOrder(string theme)
        {
            switch (theme)
            {
                case "Catching up": return "1";
                case "Inventory": return "2";
                case "Building & gathering": return "3";
                case "Food & survival": return "4";
                case "On the water": return "5";
                case "Death": return "6";
                case "Combat & powers": return "7";
                case "World": return "8";
                default: return "9";
            }
        }

        public static List<SettingInfo> ForModule(FeatureModule module)
        {
            var list = new List<SettingInfo>();
            foreach (var s in _all)
                if (s.Owner == module) list.Add(s);
            return list;
        }

        /// <summary>Plugin-level settings with no module behind them ([General], [Frontier], [Tiers]).</summary>
        public static List<SettingInfo> Orphans()
        {
            var list = new List<SettingInfo>();
            foreach (var s in _all)
                if (s.Owner == null) list.Add(s);
            return list;
        }

        // ---- the Simple view ------------------------------------------------------------------

        /// <summary>One Simple-view heading and the rows under it, ready to draw.</summary>
        internal sealed class SimpleGroup
        {
            public string Name;
            public List<SettingInfo> Rows;
        }

        /// <summary>
        /// The whole Simple view: every <see cref="SettingLevel.Essential"/> row, grouped under its
        /// <see cref="SettingInfo.SimpleGroup"/> heading, groups in <see cref="SimpleGroups.Order"/>
        /// and rows by <see cref="SettingInfo.SimpleOrder"/> then bind order. A group with no rows
        /// is left out entirely, so the page is honest while the tagging lands module by module.
        ///
        /// Ignores module selection by design - the Simple view is one page, not a per-module one -
        /// and skips <see cref="SettingInfo.HiddenAlways"/>. An Essential row whose group is
        /// misspelt simply does not appear; the self-test is what shouts about it.
        /// </summary>
        public static List<SimpleGroup> EssentialGroupsForUi()
        {
            var groups = new List<SimpleGroup>();
            foreach (var name in SimpleGroups.Order)
            {
                var rows = new List<SettingInfo>();
                int bind = 0;
                var order = new List<int>();
                foreach (var s in _all)
                {
                    bind++;
                    if (s.Level != SettingLevel.Essential || s.HiddenAlways) continue;
                    if (!string.Equals(s.SimpleGroup, name, StringComparison.Ordinal)) continue;
                    rows.Add(s);
                    order.Add(bind);
                }
                if (rows.Count == 0) continue;

                // Stable: List.Sort is not, so the bind index is the tie-breaker, not luck.
                var idx = new List<int>();
                for (int i = 0; i < rows.Count; i++) idx.Add(i);
                var rowsCopy = rows;
                var orderCopy = order;
                idx.Sort(delegate (int a, int b)
                {
                    int c = rowsCopy[a].SimpleOrder.CompareTo(rowsCopy[b].SimpleOrder);
                    return c != 0 ? c : orderCopy[a].CompareTo(orderCopy[b]);
                });
                var sorted = new List<SettingInfo>(rows.Count);
                foreach (var i in idx) sorted.Add(rows[i]);

                groups.Add(new SimpleGroup { Name = name, Rows = sorted });
            }
            return groups;
        }

        /// <summary>
        /// The Simple view as plain text, for <c>nvlb.catalog simple</c> and the boot log: a group
        /// heading, then one line per row. The point is that the classification can be reviewed
        /// from a log file, with no game and no screenshot.
        /// </summary>
        public static List<string> SimpleLines()
        {
            var lines = new List<string>();
            var groups = EssentialGroupsForUi();

            int rows = 0;
            foreach (var g in groups) rows += g.Rows.Count;
            lines.Add("Simple view: " + rows + " essential setting(s) in " + groups.Count +
                      " of " + SimpleGroups.Order.Length + " group(s)");

            foreach (var g in groups)
            {
                lines.Add("  " + g.Name.ToUpperInvariant());
                foreach (var s in g.Rows)
                    lines.Add("    " + (s.Label ?? s.Key) + "  -  [" + s.Section + "] " + s.Key +
                              " = " + s.CurrentString);
            }

            foreach (var name in SimpleGroups.Order)
            {
                bool present = false;
                foreach (var g in groups) if (g.Name == name) { present = true; break; }
                if (!present) lines.Add("  " + name.ToUpperInvariant() + "  (empty - nothing tagged yet)");
            }
            return lines;
        }

        // ---- the headless self-test -------------------------------------------------------------

        /// <summary>
        /// Proves the Simple/Advanced metadata is coherent, with no game and no UI: it reads the
        /// catalog only and changes nothing. Driven on a dedicated server by
        /// <c>[PickerSelfTest] SelfTest</c> (which already runs a headless config check at world
        /// load) and on demand by <c>nvlb.catalog selftest</c>.
        ///
        /// A malformed group is a FAIL - it silently loses a row from the page. An empty group and
        /// a restart-only Essential are WARN: both are true for a while by design as the tagging
        /// lands module by module, and a foundation build must boot clean.
        /// </summary>
        public static List<string> SelfTestLines()
        {
            const string P = "[SettingsSelfTest] ";
            var lines = new List<string> { P + "--- begin ---" };
            int pass = 0, warn = 0, fail = 0;

            int essential = 0, advanced = 0, diagnostic = 0, hidden = 0;
            foreach (var s in _all)
            {
                if (s.HiddenAlways) hidden++;
                switch (s.Level)
                {
                    case SettingLevel.Essential: essential++; break;
                    case SettingLevel.Diagnostic: diagnostic++; break;
                    default: advanced++; break;
                }
            }

            // 1. Every setting carries a level. True by construction (the field has a default), so
            //    this is a count, not a question - but the count is the thing worth logging.
            lines.Add(P + "levels: " + essential + " essential, " + advanced + " advanced, " +
                      diagnostic + " diagnostic, " + hidden + " hidden-always, of " + _all.Count +
                      " setting(s)");
            pass++;

            // 2. Every Essential row names a group that exists.
            int badGroup = 0;
            foreach (var s in _all)
            {
                if (s.Level != SettingLevel.Essential) continue;
                if (SimpleGroups.IsKnown(s.SimpleGroup)) continue;
                badGroup++;
                lines.Add(P + "FAIL  " + s.Id + " is Essential but its group is " +
                          (string.IsNullOrEmpty(s.SimpleGroup) ? "<none>" : "'" + s.SimpleGroup + "'") +
                          " - it would not appear on the Simple page at all");
            }
            if (badGroup == 0) { pass++; lines.Add(P + "PASS  every Essential row names a known group"); }
            else fail += badGroup;

            // 3. Every group has something in it.
            var groups = EssentialGroupsForUi();
            var filled = new HashSet<string>();
            foreach (var g in groups) filled.Add(g.Name);
            int empty = 0;
            foreach (var name in SimpleGroups.Order)
            {
                if (filled.Contains(name)) continue;
                empty++;
                lines.Add(P + "WARN  group '" + name + "' has no Essential rows yet");
            }
            if (empty == 0) { pass++; lines.Add(P + "PASS  all " + SimpleGroups.Order.Length + " groups have rows"); }
            else warn += empty;

            // 4. The Essential budget. A page that grows back into 294 rows helps nobody.
            if (essential <= EssentialBudget)
            {
                pass++;
                lines.Add(P + "PASS  " + essential + " Essential row(s), budget " + EssentialBudget);
            }
            else
            {
                fail++;
                lines.Add(P + "FAIL  " + essential + " Essential row(s) exceeds the budget of " +
                          EssentialBudget + " - trim the list rather than raising the budget");
            }

            // 5. A Simple row that cannot be changed without a restart is a poor first impression.
            //    A module's own Enabled toggle is exempt: its restart rule is the door's, not
            //    Opt.Restart's, and it is greyed with its own reason.
            int restartOnly = 0;
            foreach (var s in _all)
            {
                if (s.Level != SettingLevel.Essential || s.Live || s.IsEnabledToggle) continue;
                restartOnly++;
                lines.Add(P + "WARN  " + s.Id + " is Essential but restart-only");
            }
            if (restartOnly == 0) { pass++; lines.Add(P + "PASS  no Essential row is restart-only"); }
            else warn += restartOnly;

            lines.Add(P + "--- end --- " + (pass + warn + fail) + " checks, " + pass + " PASS, " +
                      warn + " WARN, " + fail + " FAIL");
            return lines;
        }

        /// <summary>How many rows the Simple page may hold. Enforced by <see cref="SelfTestLines"/>.</summary>
        public const int EssentialBudget = 45;

        // ---- reporting ------------------------------------------------------------------------

        /// <summary>The one-line proof, logged at boot on both halves.</summary>
        public static string SummaryLine()
        {
            int synced = 0, local = 0, everyone = 0, admin = 0, live = 0, restart = 0, ranged = 0, hinted = 0;
            int essential = 0, diagnostic = 0;
            var types = new Dictionary<string, int>();
            foreach (var s in _all)
            {
                if (s.Level == SettingLevel.Essential) essential++;
                else if (s.Level == SettingLevel.Diagnostic) diagnostic++;
                if (s.IsLocal) local++; else synced++;
                if (s.Tier == SettingTier.Admin) admin++; else everyone++;
                if (s.Live) live++; else restart++;
                if (s.HasRange) ranged++;
                if (!string.IsNullOrEmpty(s.Hint)) hinted++;
                int n; types.TryGetValue(s.TypeName, out n); types[s.TypeName] = n + 1;
            }
            var order = new[] { "bool", "int", "float", "string", "enum" };
            var sb = new StringBuilder();
            foreach (var t in order)
            {
                int n;
                if (types.TryGetValue(t, out n)) { sb.Append(sb.Length > 0 ? " " : "").Append(t).Append('=').Append(n); types.Remove(t); }
            }
            foreach (var kv in types) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);

            return "ConfigCatalog: " + _all.Count + " settings (" + synced + " synced / " + local +
                   " local), tiers: Everyone=" + everyone + " Admin=" + admin +
                   ", live=" + live + " restart=" + restart +
                   ", types: " + sb + ", ranged=" + ranged + ", hints=" + hinted + "/" + _all.Count +
                   ", " + essential + " essential / " + diagnostic + " diagnostic";
        }

        /// <summary>Human-readable dump, one block per module. Used by <c>nvlb.catalog</c>.</summary>
        public static List<string> DumpLines(string filter)
        {
            var lines = new List<string> { SummaryLine() };
            string f = string.IsNullOrEmpty(filter) ? null : filter.ToLowerInvariant();

            var modules = ModulesForUi();
            var groups = new List<KeyValuePair<string, List<SettingInfo>>>();
            var orphans = Orphans();
            if (orphans.Count > 0) groups.Add(new KeyValuePair<string, List<SettingInfo>>("(plugin)", orphans));
            foreach (var m in modules)
                groups.Add(new KeyValuePair<string, List<SettingInfo>>(
                    m.Name + "  [" + m.Theme + "] " + (m.Hint ?? ""), ForModule(m)));

            foreach (var g in groups)
            {
                var rows = new List<string>();
                foreach (var s in g.Value)
                {
                    if (f != null && !Matches(s, f)) continue;
                    rows.Add("    " + Row(s));
                }
                if (rows.Count == 0) continue;
                lines.Add("  " + g.Key);
                lines.AddRange(rows);
            }
            return lines;
        }

        private static string Row(SettingInfo s)
        {
            var sb = new StringBuilder();
            sb.Append(s.Id).Append(" = ").Append(s.CurrentString);
            sb.Append("  (").Append(s.TypeName);
            if (s.HasRange) sb.Append(' ').Append(Num(s.Min)).Append("..").Append(Num(s.Max)).Append("/").Append(Num(s.Step));
            if (s.Choices != null) sb.Append(" one of: ").Append(string.Join("|", s.Choices));
            sb.Append(s.IsLocal ? " local" : " synced");
            sb.Append(' ').Append(s.Tier == SettingTier.Admin ? "admin" : "everyone");
            if (!s.Live) sb.Append(" restart");
            if (s.Level == SettingLevel.Essential) sb.Append(" essential:").Append(s.SimpleGroup);
            else if (s.Level == SettingLevel.Diagnostic) sb.Append(" diagnostic");
            if (s.HiddenAlways) sb.Append(" hidden");
            sb.Append(")");
            if (!string.IsNullOrEmpty(s.Hint)) sb.Append("  - ").Append(s.Hint);
            return sb.ToString();
        }

        private static string Num(double d)
        {
            return d == Math.Floor(d) && Math.Abs(d) < 1e15
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static bool Matches(SettingInfo s, string lowerNeedle)
        {
            if (string.IsNullOrEmpty(lowerNeedle)) return true;
            return Has(s.Key, lowerNeedle) || Has(s.Section, lowerNeedle) || Has(s.Label, lowerNeedle) ||
                   Has(s.Hint, lowerNeedle) || Has(s.Description, lowerNeedle) || Has(s.ModuleName, lowerNeedle);
        }

        private static bool Has(string haystack, string lowerNeedle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(lowerNeedle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Tab-separated dump for tooling (written next to the cfg on a server).</summary>
        public static string Tsv()
        {
            var sb = new StringBuilder();
            // Columns are APPEND-ONLY: anything that already reads this file by column index must
            // keep working, so a new field goes on the end and never in the middle.
            sb.Append("section\tkey\tlabel\thint\ttype\tmin\tmax\tstep\tchoices\ttier\tlive\tscope\tmodule\ttheme\tdefault\tvalue\tlevel\tsimplegroup\n");
            foreach (var s in _all)
            {
                sb.Append(s.Section).Append('\t').Append(s.Key).Append('\t').Append(Clean(s.Label)).Append('\t')
                  .Append(Clean(s.Hint)).Append('\t').Append(s.TypeName).Append('\t')
                  .Append(s.HasRange ? Num(s.Min) : "").Append('\t')
                  .Append(s.HasRange ? Num(s.Max) : "").Append('\t')
                  .Append(s.HasRange ? Num(s.Step) : "").Append('\t')
                  .Append(s.Choices != null ? string.Join("|", s.Choices) : "").Append('\t')
                  .Append(s.Tier).Append('\t').Append(s.Live ? "live" : "restart").Append('\t')
                  .Append(s.IsLocal ? "local" : "synced").Append('\t')
                  .Append(s.ModuleName).Append('\t').Append(s.Theme).Append('\t')
                  .Append(Clean(s.DefaultString)).Append('\t').Append(Clean(s.CurrentString)).Append('\t')
                  .Append(s.HiddenAlways ? "Hidden" : s.Level.ToString()).Append('\t')
                  .Append(Clean(s.SimpleGroup)).Append('\n');
            }
            return sb.ToString();
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}
