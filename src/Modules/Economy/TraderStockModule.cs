using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Haldor also sells bars of trailing-tier metals, so a latecomer can buy the bronze the group
    /// already mined out instead of asking for a hand-out.
    ///
    /// Patch point: postfix on Trader.Start(). m_items is a plain serialized List on the Trader
    /// component, deep-copied per instance by Unity's Instantiate, and Trader.GetAvailableItems()
    /// re-filters it on every interaction - so appending at Start is enough and needs no further
    /// hook. Entries are deduped by prefab name against whatever is already in the list, so a
    /// second Start (re-entering the zone, another mod re-running it) cannot stack duplicates.
    ///
    /// The gate is vanilla's own: TradeItem.m_requiredGlobalKey is set to the boss key that would
    /// put the material behind the frontier, i.e. KeyFor(materialTier + TiersBehind). Bronze is
    /// tier 1 and TiersBehind is 1, so it needs defeated_gdking - kill the Elder and bronze appears
    /// in Haldor's list on its own, with no further work from this module.
    ///
    /// Side is Client because the trader is a client-side object: the store window is built
    /// locally from the local Trader component. The server half of the DLL only carries the config.
    ///
    /// ISSUE #9 (Epic Loot). Valheim 1.0 grew Trader.TradeItem from four fields to twelve -
    /// m_name, m_tooltip, m_buyKey, m_incrementKey, m_buyPlayerEffects and friends, for Haldor's
    /// inventory-row upgrade. Unity fills those in when it deserialises a trader prefab, but a
    /// `new Trader.TradeItem { ... }` written in code leaves the strings NULL, and vanilla
    /// StoreGui.FillList() reads `tradeItem.m_tooltip.Length` with no null check (and
    /// BuySelectedItem() calls `m_buyPlayerEffects.Create(...)`). So every NVLB-injected item
    /// threw a NullReferenceException inside FillList the moment its gating boss key was set -
    /// which is why it showed up on a progressed dedicated world and not in a fresh single-player
    /// one. Epic Loot 0.14.5 and older hung its trader window off a Harmony POSTFIX on
    /// StoreGui.Show(); a postfix does not run when the original throws, so NVLB's NRE erased
    /// Epic Loot's bounty/merchant panel. (Epic Loot 0.14.6 moved that hook to a Finalizer for
    /// exactly this reason, but the NRE itself is ours to fix.)
    ///
    /// The fix is three layers: Sanitize() fills every field vanilla dereferences before the item
    /// is ever handed to the game; Usable() drops an entry whose ItemDrop has no icon (FillList
    /// indexes m_icons[0] unguarded); and a *guard-only* prefix on StoreGui.FillList re-checks
    /// NVLB's own items every time the window opens, so a config change or another mod's reshuffle
    /// cannot reintroduce a half-built entry. The prefix never returns false, never replaces
    /// vanilla, and never touches an item NVLB did not add.
    /// </summary>
    internal sealed class TraderStockModule : FeatureModule
    {
        public override string Name => "TraderStock";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Trader";

        public override string Theme => "Catching up";

        public override string Hint => "Traders sell metal bars from tiers your group has moved past once you have caught up";

        internal const string DefaultItems = "Bronze:5:60,Iron:5:80,Silver:5:120,BlackMetal:5:150";

        /// <summary>BepInEx GUID of Epic Loot (RandyKnapp), the mod issue #9 was reported against.</summary>
        internal const string EpicLootGuid = "randyknapp.mods.epicloot";

        private static ConfigEntry<string> _items;
        private static ConfigEntry<string> _traderNames;
        private static TraderStockModule _self;

        /// <summary>Items this module put in a trader's list, by reference. Weak, so a trader that
        /// despawns takes its entries with it and nothing leaks across world loads.</summary>
        private static readonly ConditionalWeakTable<Trader.TradeItem, object> Ours =
            new ConditionalWeakTable<Trader.TradeItem, object>();

        private static readonly object Marker = new object();
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static FieldInfo _traderField;
        private static readonly string GuidPrefix = NoVikingLeftBehindPlugin.PluginGuid;

        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive();
        }

        private sealed class Entry
        {
            public string Prefab;
            public int Stack;
            public int Price;
        }

        protected override void Bind()
        {
            _self = this;

            _items = BindSynced("Items", DefaultItems,
                "An entry only appears once its material is from a tier your group has moved " +
                "past. Comma-separated PrefabName:stack:price entries added to the trader's " +
                "stock. An entry only appears once its material is behind the frontier: it is " +
                "gated by vanilla's own TradeItem.m_requiredGlobalKey, set to the boss key for " +
                "(material tier + [Frontier] TiersBehind). Unknown prefab names are logged and " +
                "skipped. Materials at tier 0, or whose gating boss is past the last tier, are " +
                "skipped too.",
                Opt.T("Which items from tiers your group has moved past a trader stocks, and at what price")
                    .Pick(new PickerSpec(PickerSource.Materials,
                        new PickerField("Stack", 1, 50, 5, true),
                        new PickerField("Price", 1, 9999, 100, true))));

            _traderNames = BindSynced("TraderNames", "Haldor",
                "Comma-separated trader prefab names (or Trader.m_name values) that get the extra " +
                "stock. Default: Haldor only. Add Hildir or BogWitch to include them. '*' means " +
                "every trader.",
                Opt.T("Which traders get the extra stock of old-tier items")
                    .Pick(new PickerSpec(PickerSource.Traders)));
        }

        protected override void ApplyPatches()
        {
            var start = AccessTools.Method(typeof(Trader), "Start");
            if (start == null) throw new Exception("Trader.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(TraderStockModule), nameof(StartPost)));

            // Guard only - it never returns false and never replaces vanilla's FillList. If a
            // future build renames the method we lose the safety net, not the feature, so this
            // one does not throw.
            var fill = AccessTools.Method(typeof(StoreGui), "FillList");
            if (fill != null)
                Harmony.Patch(fill, prefix: new HarmonyMethod(typeof(TraderStockModule), nameof(FillListPre))
                {
                    priority = Priority.First
                });
            else
                Log.LogWarning("[TraderStock] StoreGui.FillList() not found - store-window guard not installed");

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private static void StartPost(Trader __instance)
        {
            if (!Live() || __instance == null) return;
            try
            {
                Stock(__instance);
            }
            catch (Exception e)
            {
                Log.LogWarning("[TraderStock] could not add stock to " + __instance.name + ": " + e.Message);
            }
        }

        private static void Stock(Trader trader)
        {
            if (!Matches(trader)) return;
            if (trader.m_items == null) trader.m_items = new List<Trader.TradeItem>();

            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in trader.m_items)
                if (it != null && it.m_prefab != null) have.Add(Tiers.CleanName(it.m_prefab.name));

            NoteOtherTraderMod();

            int added = 0;
            foreach (var e in Parse(_items != null ? _items.Value : DefaultItems, true))
            {
                if (have.Contains(e.Prefab)) continue;

                string key;
                var drop = Resolve(e.Prefab, out key, true);
                if (drop == null) continue;

                // Never hand the game (or another mod's window) a half-built entry: see issue #9.
                if (!Usable(drop))
                {
                    WarnOnce(e.Prefab, "has no usable item data or icon in ObjectDB - skipped");
                    continue;
                }

                var item = Sanitize(new Trader.TradeItem
                {
                    m_prefab = drop,
                    m_stack = Mathf.Max(1, e.Stack),
                    m_price = Mathf.Max(1, e.Price),
                    m_requiredGlobalKey = key
                });

                Ours.Add(item, Marker);
                trader.m_items.Add(item);
                have.Add(e.Prefab);
                added++;
            }

            if (added > 0)
                Log.LogInfo("[TraderStock] " + trader.name + " (" + trader.m_name + "): added " +
                            added + " item(s), total " + trader.m_items.Count);
        }

        private static bool Matches(Trader trader)
        {
            var names = _traderNames != null ? _traderNames.Value : "Haldor";
            foreach (var raw in names.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var n = raw.Trim();
                if (n.Length == 0) continue;
                if (n == "*") return true;
                if (Tiers.CleanName(trader.name).IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (!string.IsNullOrEmpty(trader.m_name) &&
                    trader.m_name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // ---- store-window guard (issue #9) ---------------------------------------------------------

        /// <summary>
        /// Fill in every TradeItem field vanilla dereferences without a null check. Unity gives a
        /// prefab-authored entry empty strings and a real EffectList; a code-built one gets nulls,
        /// and StoreGui.FillList() / BuySelectedItem() do not check. Idempotent, and it only ever
        /// replaces a null - an entry that already carries a value keeps it.
        /// </summary>
        internal static Trader.TradeItem Sanitize(Trader.TradeItem it)
        {
            if (it == null) return null;
            if (it.m_tooltip == null) it.m_tooltip = "";                    // FillList: m_tooltip.Length
            if (it.m_name == null) it.m_name = it.m_prefab != null ? it.m_prefab.name : "";
            if (it.m_buyKey == null) it.m_buyKey = "";                      // GetAvailableItems / BuySelectedItem
            if (it.m_incrementKey == null) it.m_incrementKey = "";
            if (it.m_requiredGlobalKey == null) it.m_requiredGlobalKey = "";
            if (it.m_buyPlayerEffects == null) it.m_buyPlayerEffects = new EffectList();  // BuySelectedItem: .Create()

            // Vanilla falls back to the prefab's icon when m_icon is empty, so stamping it is a
            // no-op there; it is what saves the row in a window another mod rebuilt from m_icon.
            if (it.m_icon == null) it.m_icon = IconOf(it.m_prefab);

            if (it.m_stack < 1) it.m_stack = 1;
            if (it.m_price < 0) it.m_price = 0;
            return it;
        }

        /// <summary>An ItemDrop FillList can actually draw: it indexes m_icons[0] unguarded.</summary>
        internal static bool Usable(ItemDrop drop)
        {
            if (drop == null) return false;
            var data = drop.m_itemData;
            var shared = data != null ? data.m_shared : null;
            var icons = shared != null ? shared.m_icons : null;
            return icons != null && icons.Length > 0 && icons[0] != null;
        }

        private static Sprite IconOf(ItemDrop drop)
        {
            return Usable(drop) ? drop.m_itemData.m_shared.m_icons[0] : null;
        }

        /// <summary>Mark an item as one of ours (used by the self-test as well as Stock()).</summary>
        internal static Trader.TradeItem Mine(Trader.TradeItem it)
        {
            if (it == null) return null;
            Ours.Add(it, Marker);
            return it;
        }

        private static bool IsOurs(Trader.TradeItem it)
        {
            object _;
            return it != null && Ours.TryGetValue(it, out _);
        }

        /// <summary>
        /// The testable half of the FillList guard: re-check every entry NVLB added to this list,
        /// sanitise it, and drop it if its prefab has gone away. Foreign entries and vanilla's own
        /// are never touched - if another mod ships a broken item that is between it and the game.
        /// Returns how many NVLB entries were removed. Never throws.
        /// </summary>
        internal static int GuardItems(List<Trader.TradeItem> items)
        {
            if (items == null) return 0;
            int dropped = 0;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var it = items[i];
                if (!IsOurs(it)) continue;

                if (!Usable(it.m_prefab))
                {
                    items.RemoveAt(i);
                    dropped++;
                    WarnOnce(it.m_name ?? "?", "lost its prefab - removed from the trader's list");
                    continue;
                }
                Sanitize(it);
            }
            return dropped;
        }

        /// <summary>
        /// Guard-only prefix on StoreGui.FillList(). Returns void, so vanilla always runs; bails
        /// out quietly on anything unexpected rather than throwing into another mod's call stack.
        /// Runs at Priority.First so a foreign prefix that returns false cannot skip the repair -
        /// the entries would still be sitting in the trader's list for whatever draws it instead.
        /// It looks at nothing but the trader's item list, so a mod that repoints m_listRoot or
        /// m_listElement is none of its business.
        /// </summary>
        private static void FillListPre(StoreGui __instance)
        {
            try
            {
                if (__instance == null) return;

                if (_traderField == null) _traderField = AccessTools.Field(typeof(StoreGui), "m_trader");
                var trader = _traderField != null ? _traderField.GetValue(__instance) as Trader : null;
                if (trader == null || trader.m_items == null) return;

                GuardItems(trader.m_items);
            }
            catch (Exception e)
            {
                Log.LogWarning("[TraderStock] store-window guard skipped: " + e.Message);
            }
        }

        // ---- other trader mods: diagnostics only ----------------------------------------------------

        /// <summary>
        /// Who else is shaping the trader window, for the log and the self-test. It changes
        /// nothing about what this module does - the guards above run the same either way - but
        /// when an issue like #9 comes in it is the first line worth reading. A negative answer is
        /// re-asked, because another plugin can patch StoreGui long after the first trader spawned.
        /// </summary>
        internal static string OtherTraderMod()
        {
            if (_otherName != null) return _otherName;
            try
            {
                BepInEx.PluginInfo info;
                if (Chainloader.PluginInfos != null &&
                    Chainloader.PluginInfos.TryGetValue(EpicLootGuid, out info) && info != null)
                    return _otherName = "Epic Loot";
                return _otherName = (ForeignPatcher("FillList") ?? ForeignPatcher("Show"));
            }
            catch { return null; }
        }

        /// <summary>One info line, the first time another trader mod is seen.</summary>
        private static void NoteOtherTraderMod()
        {
            if (_noted) return;
            var who = OtherTraderMod();
            if (string.IsNullOrEmpty(who)) return;
            _noted = true;
            Log.LogInfo("[TraderStock] " + who + " also shapes the trader window - " +
                        "added items are validated and the store-window guard is installed");
        }

        private static string _otherName;
        private static bool _noted;

        /// <summary>Owner id of the first non-NVLB Harmony patch on StoreGui.&lt;method&gt;, or null.</summary>
        private static string ForeignPatcher(string method)
        {
            var m = AccessTools.Method(typeof(StoreGui), method);
            if (m == null) return null;
            var info = HarmonyLib.Harmony.GetPatchInfo(m);
            if (info == null) return null;

            foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers })
            {
                if (list == null) continue;
                foreach (var p in list)
                {
                    if (p == null || string.IsNullOrEmpty(p.owner)) continue;
                    if (p.owner.StartsWith(GuidPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    return p.owner;
                }
            }
            return null;
        }

        private static void WarnOnce(string name, string why)
        {
            var k = name + "|" + why;
            if (!Warned.Add(k)) return;
            Log.LogWarning("[TraderStock] '" + name + "' " + why);
        }

        // ---- config parsing ----------------------------------------------------------------------

        private static List<Entry> Parse(string raw, bool warn)
        {
            var list = new List<Entry>();
            if (string.IsNullOrEmpty(raw)) return list;

            foreach (var chunk in raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = chunk.Trim();
                if (piece.Length == 0) continue;

                var bits = piece.Split(':');
                int stack, price;
                if (bits.Length != 3 ||
                    !int.TryParse(bits[1].Trim(), out stack) ||
                    !int.TryParse(bits[2].Trim(), out price))
                {
                    if (warn) Log.LogWarning("[TraderStock] ignoring '" + piece + "' (want Prefab:stack:price)");
                    continue;
                }
                list.Add(new Entry { Prefab = bits[0].Trim(), Stack = stack, Price = price });
            }
            return list;
        }

        /// <summary>Prefab -> ItemDrop plus the boss key that gates it. null = skip (and why).</summary>
        private static ItemDrop Resolve(string prefabName, out string requiredKey, bool warn)
        {
            requiredKey = null;

            int tier = Tiers.OfItem(prefabName);
            if (tier <= 0)
            {
                if (warn) Log.LogWarning("[TraderStock] '" + prefabName + "' is tier 0 in [Tiers] MaterialTiers - skipped");
                return null;
            }

            int gateTier = tier + (Frontier.TiersBehind != null ? Frontier.TiersBehind.Value : 1);
            if (gateTier > Frontier.MaxTier)
            {
                if (warn) Log.LogWarning("[TraderStock] '" + prefabName + "' (tier " + tier + ") can never be " +
                                         gateTier + " tiers behind - skipped");
                return null;
            }

            // With a [Frontier] TierOverride in force the real boss key would not match the
            // simulated progress, so trust the override and leave the key empty.
            requiredKey = Frontier.IsOverridden ? "" : Frontier.KeyFor(gateTier);
            if (Frontier.IsOverridden && !Tiers.IsBehind(tier)) return null;

            var odb = ObjectDB.instance;
            var go = odb != null ? odb.GetItemPrefab(prefabName) : null;
            var drop = go != null ? go.GetComponent<ItemDrop>() : null;
            if (drop == null && warn)
                Log.LogWarning("[TraderStock] '" + prefabName + "' not found in ObjectDB - skipped");
            return drop;
        }

        // ---- reporting ------------------------------------------------------------------------------

        private string Numbers()
        {
            var parsed = Parse(_items.Value, false);
            return parsed.Count + " item(s) for " + _traderNames.Value +
                   " [" + _items.Value + "], gated by boss key for (tier + " +
                   Frontier.TiersBehind.Value + ")";
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            if (!Active) return;
            Log.LogInfo("[" + Name + "] " + Numbers() +
                        " - traders already in the world keep their current list until they respawn");
        }

        public override string StatusDetail()
        {
            return Numbers();
        }

        /// <summary>Headless proof: what would be added to Haldor, and under which key.</summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][TraderStock] TraderNames=")
              .Append(_traderNames != null ? _traderNames.Value : "?")
              .Append(" Items=").Append(_items != null ? _items.Value : "?")
              .Append(" ").Append(Frontier.Describe())
              .Append(" TiersBehind=").Append(Frontier.TiersBehind.Value);

            foreach (var e in Parse(_items != null ? _items.Value : DefaultItems, false))
            {
                int tier = Tiers.OfItem(e.Prefab);
                string key;
                var drop = Resolve(e.Prefab, out key, false);
                sb.Append("\n  ").Append(e.Prefab).Append(" t").Append(tier)
                  .Append(" stack=").Append(e.Stack).Append(" price=").Append(e.Price)
                  .Append(" key=").Append(string.IsNullOrEmpty(key) ? "(none)" : key)
                  .Append(drop == null ? " -> SKIPPED" : " -> would be added")
                  .Append(Tiers.IsBehind(tier) ? " [behind the frontier now]" : " [not behind yet]");
            }

            sb.Append('\n').Append(GuardSelfTest());
            return sb.ToString();
        }

        /// <summary>
        /// Headless proof that the issue-#9 guards hold, with no Unity scene and no store window:
        /// every case calls the guard helpers directly. Case 1 is the exact field read that threw
        /// (StoreGui.FillList: `tradeItem.m_tooltip.Length`), replayed against a code-built item.
        /// </summary>
        internal static string GuardSelfTest()
        {
            var sb = new StringBuilder();
            sb.Append("[SelfTest][TraderStock] store-window guards (issue #9)");

            // 1. Every field vanilla FillList()/BuySelectedItem() dereferences without a null check.
            try
            {
                var probe = Sanitize(new Trader.TradeItem { m_stack = 5, m_price = 60 });
                int n = probe.m_tooltip.Length + probe.m_name.Length + probe.m_buyKey.Length +
                        probe.m_incrementKey.Length + probe.m_requiredGlobalKey.Length +
                        probe.m_buyPlayerEffects.m_effectPrefabs.Length;
                sb.Append("\n  Sanitize(new TradeItem) -> all FillList fields readable (" + n + ") -> PASS");
            }
            catch (Exception e)
            {
                sb.Append("\n  Sanitize(new TradeItem) -> FAIL: " + e.GetType().Name + " " + e.Message);
            }

            // 2. An item with no prefab at all.
            try
            {
                sb.Append(Usable(null) ? "\n  Usable(null) -> FAIL (said yes)" : "\n  Usable(null)=false -> PASS");
            }
            catch (Exception e) { sb.Append("\n  Usable(null) -> FAIL: " + e.Message); }

            // 3. Null list / null entries / a foreign entry / one of ours with a dead prefab.
            try
            {
                int a = GuardItems(null);
                var foreign = new Trader.TradeItem();                     // another mod's, must be left alone
                var mine = Mine(new Trader.TradeItem { m_name = "(self-test probe)" });  // ours, prefab null -> dropped
                var list = new List<Trader.TradeItem> { null, foreign, mine };
                int b = GuardItems(list);
                bool ok = a == 0 && b == 1 && list.Count == 2 &&
                          list.Contains(foreign) && !list.Contains(mine) &&
                          foreign.m_tooltip == null;                      // untouched
                sb.Append(ok
                    ? "\n  GuardItems(null)=0, dead NVLB entry dropped, foreign entry untouched -> PASS"
                    : "\n  GuardItems -> FAIL (null=" + a + " dropped=" + b + " left=" + list.Count + ")");
            }
            catch (Exception e) { sb.Append("\n  GuardItems -> FAIL: " + e.GetType().Name + " " + e.Message); }

            // 4. The whole prefix with a StoreGui that has no trader (m_trader == null).
            try
            {
                FillListPre(null);
                FillListPre(StoreGui.instance);   // null store window / null trader in a headless boot
                sb.Append("\n  FillList guard with no store window / no trader -> PASS");
            }
            catch (Exception e) { sb.Append("\n  FillList guard -> FAIL: " + e.GetType().Name + " " + e.Message); }

            // 5. Diagnostics: who else shapes the trader window. Changes nothing, just worth logging.
            sb.Append("\n  other trader mod=").Append(OtherTraderMod() ?? "(none)");
            return sb.ToString();
        }
    }
}
