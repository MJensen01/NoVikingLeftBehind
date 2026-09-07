using System;
using System.Collections.Generic;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// "How far up the tech tree is this player's kit?" - the gear tier used by VanguardShadow.
    ///
    /// Tiers.MaterialTiers maps *materials* (Bronze, Iron, ...) to tiers, not equipment prefabs,
    /// so an equipped ArmorBronzeChest is not in the map. Three resolution steps, highest wins:
    ///
    ///   1. Tiers.OfItem(prefabName)  - direct hit (a bar carried in hand, or an operator who
    ///                                  added equipment names to [Tiers] MaterialTiers).
    ///   2. the item's crafting recipe - ObjectDB.GetRecipe(item) then Tiers.OfRecipe(). This is
    ///                                  the authoritative answer: ArmorBronzeChest costs Bronze
    ///                                  -> tier 1, AxeIron costs Iron -> tier 2.
    ///   3. name substring             - last resort for uncraftable / modded gear whose recipe
    ///                                  is missing (e.g. a boss drop named "...Iron..."). Disabled
    ///                                  with [Vanguard] NameHeuristic = false.
    ///
    /// The material list for step 3 is read back out of Tiers.Dump() so this file never has to
    /// duplicate the map or edit Tiers.cs.
    /// </summary>
    internal static class VanguardGear
    {
        private static List<KeyValuePair<string, int>> _materials;
        private static readonly Dictionary<string, int> _cache =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Drop cached lookups (material map changed, or ObjectDB was rebuilt).</summary>
        public static void Invalidate()
        {
            _materials = null;
            _cache.Clear();
        }

        /// <summary>Highest tier among a humanoid's equipped items. 0 = starter kit / naked.</summary>
        public static int OfHumanoid(Humanoid h, bool includeUtility)
        {
            if (h == null) return 0;
            int max = 0;
            Bump(ref max, h.m_helmetItem);
            Bump(ref max, h.m_chestItem);
            Bump(ref max, h.m_legItem);
            Bump(ref max, h.m_shoulderItem);
            Bump(ref max, h.m_rightItem);
            Bump(ref max, h.m_leftItem);
            if (includeUtility) Bump(ref max, h.m_utilityItem);
            return max;
        }

        private static void Bump(ref int max, ItemDrop.ItemData item)
        {
            int t = OfItem(item);
            if (t > max) max = t;
        }

        public static int OfItem(ItemDrop.ItemData item)
        {
            if (item == null) return 0;

            string name = null;
            if (item.m_dropPrefab != null) name = item.m_dropPrefab.name;
            int t = OfName(name);
            if (t > 0) return t;

            // No prefab name (or nothing known about it): still try the recipe off the ItemData.
            return FromRecipe(item);
        }

        /// <summary>Gear tier of a prefab name. Used by the equipped-item path and by the self test.</summary>
        public static int OfName(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return 0;
            string name = Tiers.CleanName(prefabName);

            int cached;
            if (_cache.TryGetValue(name, out cached)) return cached;

            int t = Tiers.OfItem(name);                      // 1. direct
            if (t == 0) t = FromRecipeByName(name);          // 2. recipe
            if (t == 0 && VanguardShadowModule.NameHeuristic) t = FromNameParts(name);   // 3. substring

            _cache[name] = t;
            return t;
        }

        private static int FromRecipeByName(string name)
        {
            var odb = ObjectDB.instance;
            if (odb == null) return 0;
            var prefab = odb.GetItemPrefab(name);
            if (prefab == null) return 0;
            var drop = prefab.GetComponent<ItemDrop>();
            if (drop == null) return 0;
            return FromRecipe(drop.m_itemData);
        }

        private static int FromRecipe(ItemDrop.ItemData item)
        {
            var odb = ObjectDB.instance;
            if (odb == null || item == null) return 0;
            try
            {
                var recipe = odb.GetRecipe(item);
                return recipe == null ? 0 : Tiers.OfRecipe(recipe);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Highest-tier material name that appears as a substring of the item name.</summary>
        private static int FromNameParts(string name)
        {
            EnsureMaterials();
            int max = 0;
            for (int i = 0; i < _materials.Count; i++)
            {
                var kv = _materials[i];
                if (kv.Value <= max) continue;
                if (name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) max = kv.Value;
            }
            return max;
        }

        /// <summary>Read the material list back out of Tiers.Dump() ("t1: Copper Tin ...").</summary>
        private static void EnsureMaterials()
        {
            if (_materials != null) return;
            var list = new List<KeyValuePair<string, int>>();
            try
            {
                foreach (var line in Tiers.Dump().Split('\n'))
                {
                    var l = line.Trim();
                    if (l.Length < 4 || l[0] != 't') continue;
                    int colon = l.IndexOf(':');
                    if (colon < 2) continue;
                    int tier;
                    if (!int.TryParse(l.Substring(1, colon - 1), out tier) || tier <= 0) continue;
                    foreach (var n in l.Substring(colon + 1)
                                       .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        list.Add(new KeyValuePair<string, int>(n, tier));
                }
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[VanguardShadow] material list parse failed: " + e.Message);
            }
            _materials = list;
        }

        /// <summary>Read another player's replicated gear tier. -1 = unknown (no ZDO value yet).</summary>
        public static int FromZdo(Player p, int hash)
        {
            if (p == null) return -1;
            var nview = p.m_nview;
            if (nview == null || !nview.IsValid()) return -1;
            var zdo = nview.GetZDO();
            if (zdo == null) return -1;
            return zdo.GetInt(hash, -1);
        }
    }
}
