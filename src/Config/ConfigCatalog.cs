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
                    // A foreign entry is read through its boxed value, not GetSerializedValue():
                    // ServerSync patches that getter to hand back the client's *own* pre-sync value
                    // whenever the config is locked, which would make the row show a number nobody
                    // is running. BoxedValue is always what is actually in force.
                    return Foreign
                        ? ConfigCatalog.Serialize(Entry.BoxedValue, ValueType)
                        : Entry.GetSerializedValue();
                }
                catch { return "?"; }
            }
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
                Owner = owner,
                ModuleName = owner != null ? owner.Name : "General",
                Theme = owner != null ? owner.Theme : "Server",
                ModuleHint = owner != null ? owner.Hint : null,
                Hint = opt.Hint,
                Choices = opt.Choices
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
            try { return TomlTypeConverter.ConvertToString(value, type ?? value.GetType()); }
            catch
            {
                if (value is float) return ((float)value).ToString(CultureInfo.InvariantCulture);
                if (value is double) return ((double)value).ToString(CultureInfo.InvariantCulture);
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
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

        // ---- reporting ------------------------------------------------------------------------

        /// <summary>The one-line proof, logged at boot on both halves.</summary>
        public static string SummaryLine()
        {
            int synced = 0, local = 0, everyone = 0, admin = 0, live = 0, restart = 0, ranged = 0, hinted = 0;
            var types = new Dictionary<string, int>();
            foreach (var s in _all)
            {
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
                   ", types: " + sb + ", ranged=" + ranged + ", hints=" + hinted + "/" + _all.Count;
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
            sb.Append("section\tkey\tlabel\thint\ttype\tmin\tmax\tstep\tchoices\ttier\tlive\tscope\tmodule\ttheme\tdefault\tvalue\n");
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
                  .Append(Clean(s.DefaultString)).Append('\t').Append(Clean(s.CurrentString)).Append('\n');
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
