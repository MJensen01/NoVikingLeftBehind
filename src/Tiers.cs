using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Material -> tier map, and the "is this behind the frontier?" test every catch-up module
    /// keys off. Tiers match <see cref="Frontier"/>: 1 = unlocked by Eikthyr, 2 by the Elder, ...
    /// Anything not in the map is tier 0 (starter material, never discounted).
    /// </summary>
    internal static class Tiers
    {
        public const string Section = "Tiers";

        public const string DefaultMap =
            "Copper:1,Tin:1,Bronze:1,TrollHide:1,Chitin:1,BronzeNails:1," +
            "Iron:2,ElderBark:2,Guck:2,IronNails:2,Chain:2," +
            "Silver:3,WolfPelt:3,WolfHairBundle:3,FreezeGland:3,Obsidian:3," +
            "BlackMetal:4,LinenThread:4,LoxPelt:4,Needle:4,Tar:4,JuteRed:4," +
            "BlackMarble:5,Eitr:5,Carapace:5,Sap:5,ScaleHide:5,YggdrasilWood:5,Mandible:5," +
            "Softtissue:5,JuteBlue:5," +
            "FlametalNew:6,Grausten:6,ProustitePowder:6,CharredCogwheel:6,MorgenSinew:6," +
            "AskHide:6,CelestialFeather:6,MoltenCore:6";

        public static ConfigEntry<string> MaterialTiers;

        private static Dictionary<string, int> _map =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _unknown = new List<string>();
        private static bool _validated;

        public static int Count => _map.Count;
        public static int UnknownCount => _unknown.Count;

        public static void BindConfig()
        {
            MaterialTiers = NoVikingLeftBehindPlugin.BindSynced(Section, "MaterialTiers", DefaultMap,
                "Comma-separated PrefabName:tier pairs. Tier 1 unlocks with Eikthyr, 2 with the " +
                "Elder, 3 Bonemass, 4 Moder, 5 Yagluth, 6 The Queen, 7 Fader. Anything not " +
                "listed is tier 0 and is never treated as behind the frontier. Prefab names are " +
                "checked against ObjectDB once the game has loaded; unknown names are logged as " +
                "a warning and otherwise ignored.", null,
                Opt.T("Which materials are behind the frontier, and by how much"));

            NoVikingLeftBehindPlugin.ConfigChanged += OnConfigChanged;
            Parse();
        }

        private static void OnConfigChanged(ConfigEntryBase entry)
        {
            if (entry != MaterialTiers) return;
            Parse();
            _validated = false;
            ValidateOnce();
            NoVikingLeftBehindPlugin.Log.LogInfo("[Tiers] material map reloaded: " + _map.Count + " entries");
        }

        private static void Parse()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var raw = MaterialTiers != null ? MaterialTiers.Value : DefaultMap;

            foreach (var chunk in raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = chunk.Trim();
                if (piece.Length == 0) continue;

                var colon = piece.LastIndexOf(':');
                if (colon <= 0)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Tiers] ignoring malformed entry '" + piece + "' (want Name:tier)");
                    continue;
                }

                var name = piece.Substring(0, colon).Trim();
                int tier;
                if (!int.TryParse(piece.Substring(colon + 1).Trim(), out tier) || tier < 0 || tier > Frontier.MaxTier)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Tiers] ignoring '" + piece + "': tier must be 0.." + Frontier.MaxTier);
                    continue;
                }

                map[name] = tier;
            }

            _map = map;
        }

        /// <summary>
        /// Check every configured prefab name against ObjectDB once it exists. Unknown names are
        /// warned about and kept (a name may belong to another mod that loads later).
        /// </summary>
        public static void ValidateOnce()
        {
            if (_validated) return;
            var odb = ObjectDB.instance;
            if (odb == null || odb.m_items == null || odb.m_items.Count == 0) return;
            _validated = true;

            _unknown.Clear();
            foreach (var kv in _map)
            {
                if (kv.Value <= 0) continue;
                if (odb.GetItemPrefab(kv.Key) == null) _unknown.Add(kv.Key);
            }

            if (_unknown.Count > 0)
                NoVikingLeftBehindPlugin.Log.LogWarning("[Tiers] " + _unknown.Count + " configured material(s) not in ObjectDB: " +
                                              string.Join(", ", _unknown.ToArray()));
            else
                NoVikingLeftBehindPlugin.Log.LogInfo("[Tiers] " + _map.Count + " materials, all present in ObjectDB");
        }

        // ---- lookups ------------------------------------------------------------------------

        /// <summary>Strip Unity's "(Clone)" suffix so instance names resolve too.</summary>
        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int i = name.IndexOf("(Clone)", StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i) : name;
        }

        public static int OfItem(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return 0;
            int tier;
            return _map.TryGetValue(CleanName(prefabName), out tier) ? tier : 0;
        }

        public static int OfItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_dropPrefab == null) return 0;
            return OfItem(item.m_dropPrefab.name);
        }

        public static int OfItem(ItemDrop drop)
        {
            return drop == null ? 0 : OfItem(drop.name);
        }

        /// <summary>Tier of a recipe = the highest tier among its requirements.</summary>
        public static int OfRecipe(Recipe recipe)
        {
            return recipe == null ? 0 : OfRequirements(recipe.m_resources);
        }

        /// <summary>Tier of a build piece = the highest tier among its requirements.</summary>
        public static int OfPiece(Piece piece)
        {
            return piece == null ? 0 : OfRequirements(piece.m_resources);
        }

        public static int OfRequirements(Piece.Requirement[] reqs)
        {
            if (reqs == null) return 0;
            int max = 0;
            for (int i = 0; i < reqs.Length; i++)
            {
                var r = reqs[i];
                if (r == null || r.m_resItem == null) continue;
                int t = OfItem(r.m_resItem.name);
                if (t > max) max = t;
            }
            return max;
        }

        /// <summary>
        /// The one test every catch-up module uses: is this tier far enough behind the world's
        /// progress to be softened? Tier 0 (starter material) is never behind.
        /// </summary>
        public static bool IsBehind(int tier)
        {
            if (tier < 1) return false;
            int cutoff = Frontier.WorldTier - (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1);
            return tier <= cutoff;
        }

        public static bool IsRecipeBehind(Recipe recipe) { return IsBehind(OfRecipe(recipe)); }
        public static bool IsPieceBehind(Piece piece) { return IsBehind(OfPiece(piece)); }
        public static bool IsItemBehind(string prefabName) { return IsBehind(OfItem(prefabName)); }

        public static string Dump()
        {
            var sb = new StringBuilder();
            var byTier = new SortedDictionary<int, List<string>>();
            foreach (var kv in _map)
            {
                List<string> list;
                if (!byTier.TryGetValue(kv.Value, out list)) byTier[kv.Value] = list = new List<string>();
                list.Add(kv.Key);
            }
            foreach (var kv in byTier)
            {
                kv.Value.Sort(StringComparer.OrdinalIgnoreCase);
                sb.Append("t").Append(kv.Key).Append(": ").Append(string.Join(" ", kv.Value.ToArray())).Append("\n");
            }
            return sb.ToString();
        }
    }
}
