using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>One saved weapon pair: what goes in the right hand and what goes in the left.</summary>
    internal sealed class LoadoutSpec
    {
        public string RightPrefab = "";
        public int RightQuality = 1;
        public int RightVariant;
        public string LeftPrefab = "";
        public int LeftQuality = 1;
        public int LeftVariant;

        public bool IsEmpty
        {
            get { return string.IsNullOrEmpty(RightPrefab) && string.IsNullOrEmpty(LeftPrefab); }
        }

        public override string ToString()
        {
            return (string.IsNullOrEmpty(RightPrefab) ? "-" : RightPrefab) + " / " +
                   (string.IsNullOrEmpty(LeftPrefab) ? "-" : LeftPrefab);
        }
    }

    /// <summary>
    /// Weapon loadouts: one key swaps to a saved weapon + off-hand pair.
    ///
    /// Identity. There is no per-item unique id in 0.221.12 - ItemDrop.ItemData has no m_uid, and
    /// m_crafterID / m_crafterName identify the smith, not the item (several items share them).
    /// The stable identity of an item is therefore the triple (drop-prefab name, quality, variant),
    /// which is exactly what vanilla itself persists in Inventory.Save. A loadout stores that
    /// triple and, when applied, picks the best candidate still in the inventory: exact quality and
    /// variant first, then the same prefab at the highest quality. So upgrading a sword at the
    /// forge does not break the loadout, and a copy taken from a chest works too.
    ///
    /// Storage is Player.m_customData["nvlb.loadout1"/"2"] - the same channel the Powers module and
    /// ExtraSlots' save blob use, which vanilla persists verbatim for save version >= 26.
    ///
    /// TWO MODES, since 0.8.1.
    ///   * **Saved pair** (the original, and still the default): hold SaveModifier and press the
    ///     key to store the (prefab, quality, variant) of whatever is in your hands; press it alone
    ///     to put that pair back.
    ///   * **Hotbar slots**: set `Loadout1Slots = "1,2"` and the key equips whatever is sitting in
    ///     hotbar slots 1 and 2 *right now* - main hand first, then off-hand. Nothing is stored, so
    ///     there is nothing to keep in step: rearranging your hotbar rearranges the loadout. The
    ///     save modifier is ignored for a loadout in this mode, because there is nothing to save.
    /// A hotbar slot is simply column n-1 of row 0 of the player's own inventory - exactly what
    /// vanilla's `Player.UseHotbarItem(int index)` reads (`m_inventory.GetItemAt(index - 1, 0)`).
    ///
    /// KEYS, since 0.8.1. The keys are registered as real ZInput buttons and appear on Valheim's
    /// own **Keyboard &amp; Mouse** settings page, so they can be rebound there like any vanilla
    /// action - see <see cref="NvlbKeys"/>. The config entries below are the DEFAULT for each
    /// button; a rebind on that page wins over them. `Loadout1Key` also moved from **V to Z** in
    /// 0.8.1: V is vanilla's own AutoPickup toggle (`ZInput.AddButton("AutoPickup", KeyToPath(Key.V)
    /// ...)`, ZInput.cs:2651), so the old default fought the game.
    /// </summary>
    internal sealed class LoadoutsModule : FeatureModule
    {
        public override string Name => "Loadouts";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Loadouts";
        public override string Theme => "Inventory";
        public override string Hint => "Save and equip weapon and shield loadouts with a key press";

        public const string KeyPrefix = "nvlb.loadout";
        public const int MaxSlots = 4;

        private static LoadoutsModule _inst;

        private ConfigEntry<int> _slots;
        private ConfigEntry<string> _key1;
        private ConfigEntry<string> _key2;
        private ConfigEntry<string> _saveModifier;
        private ConfigEntry<string> _slots1;
        private ConfigEntry<string> _slots2;

        /// <summary>The ZInput button id (without the NVLB_ prefix) for loadout <c>n</c>.</summary>
        internal static string KeyId(int slot) { return "Loadout" + slot; }

        /// <summary>The ZInput button id for the save modifier.</summary>
        internal const string SaveModifierId = "LoadoutSave";

        /// <summary>Loadout1Key's default up to 0.8.0 - the one value that gets migrated to Z.</summary>
        private const string OldLoadout1Default = "V";

        private static KeyCode[] _keys = new KeyCode[0];
        private static KeyCode _modifier = KeyCode.LeftControl;

        /// <summary>Per loadout: the hotbar slot numbers it equips, or null for saved-pair mode.</summary>
        private static int[][] _hotbar = new int[MaxSlots][];

        private static int _tickErrors;
        private static bool _migrationLogged;

        // ---- config ---------------------------------------------------------------------------

        protected override void Bind()
        {
            _slots = BindSynced("Slots", 2, "Server: how many weapon loadouts each player gets (0-" + MaxSlots + ").",
                Opt.N("How many weapon loadouts each player gets", 0, MaxSlots, 1));
            _key1 = BindLocal("Loadout1Key", "Z",
                "Local: DEFAULT key that equips loadout 1. Since 0.8.1 this is a real Valheim " +
                "keybinding, so it can be rebound on the game's own Keyboard & Mouse settings page - " +
                "and a rebind there wins over this value. Changed from V to Z in 0.8.1 because V is " +
                "vanilla's own auto-pickup toggle. Unity KeyCode name, or None.",
                Opt.T("Default key that equips loadout 1"));
            _key2 = BindLocal("Loadout2Key", "B",
                "Local: DEFAULT key that equips loadout 2, rebindable on Valheim's own Keyboard & " +
                "Mouse page. Unity KeyCode name, or None.",
                Opt.T("Default key that equips loadout 2"));
            _saveModifier = BindLocal("SaveModifier", "LeftControl",
                "Local: hold this and press a loadout key to SAVE what you are currently holding " +
                "into that loadout instead of equipping it. Ignored for a loadout that is set to " +
                "hotbar-slot mode (Loadout1Slots / Loadout2Slots), which stores nothing. Also " +
                "rebindable on Valheim's own Keyboard & Mouse page.",
                Opt.T("Default key held to save instead of equip a loadout"));

            _slots1 = BindLocal("Loadout1Slots", "",
                "Local: make loadout 1 a pair of HOTBAR SLOTS instead of a saved weapon pair. " +
                "\"1,2\" means 'equip whatever is in hotbar slot 1 (main hand) and slot 2 " +
                "(off-hand)' at the moment you press the key - so rearranging your hotbar " +
                "rearranges the loadout and there is nothing to save. \"1\" is main hand only. " +
                "Slot numbers are 1-8, left to right. Empty = the original saved-pair mode.",
                Opt.T("Hotbar slots loadout 1 equips (e.g. \"1,2\"), empty for the saved pair"));
            _slots2 = BindLocal("Loadout2Slots", "",
                "Local: the same for loadout 2. Empty = the original saved-pair mode.",
                Opt.T("Hotbar slots loadout 2 equips (e.g. \"3,4\"), empty for the saved pair"));

            MigrateLoadout1Key();
            ParseKeys();

            // The hotkeys become real, rebindable Valheim keybindings. The lambdas are re-read on
            // every registration, so a cfg edit moves the DEFAULT and a player's own rebind on the
            // Keyboard & Mouse page still wins - see NvlbKeys.
            NvlbKeys.Declare(KeyId(1), "Loadout 1", delegate { return _keys.Length > 0 ? _keys[0] : KeyCode.None; });
            NvlbKeys.Declare(KeyId(2), "Loadout 2", delegate { return _keys.Length > 1 ? _keys[1] : KeyCode.None; });
            NvlbKeys.Declare(SaveModifierId, "Save loadout (hold)", delegate { return _modifier; });
        }

        /// <summary>
        /// 0.8.0 and earlier defaulted Loadout1Key to V, which is vanilla's AutoPickup toggle. A cfg
        /// still holding exactly that old default is moved to the new one; anything a player chose
        /// themselves - including a deliberate "V" typed after this release - is left alone, because
        /// the only thing we can tell apart is "identical to the old default".
        /// </summary>
        private void MigrateLoadout1Key()
        {
            if (_key1 == null || _key1.Value != OldLoadout1Default) return;
            _key1.Value = (string)_key1.DefaultValue;
            if (_migrationLogged) return;
            _migrationLogged = true;
            Log.LogWarning("[Loadouts] Loadout1Key was still the old default '" + OldLoadout1Default +
                           "', which is vanilla's own auto-pickup toggle - moved to '" + _key1.Value +
                           "'. Rebind it on Valheim's Keyboard & Mouse settings page if you want " +
                           "something else.");
        }

        public override void OnConfigChanged(ConfigEntryBase entry) { ParseKeys(); }

        private void ParseKeys()
        {
            var names = new List<string> { _key1.Value, _key2.Value, "None", "None" };
            var keys = new KeyCode[MaxSlots];
            for (int i = 0; i < MaxSlots; i++) keys[i] = ParseKey(names[i]);
            _keys = keys;
            _modifier = ParseKey(_saveModifier.Value);

            var pairs = new int[MaxSlots][];
            pairs[0] = ParseSlots(_slots1.Value, "Loadout1Slots");
            pairs[1] = ParseSlots(_slots2.Value, "Loadout2Slots");
            _hotbar = pairs;
        }

        /// <summary>
        /// "1,2" -> {1,2}. Null when empty (saved-pair mode) or unusable, which is the safe answer:
        /// a typo leaves the loadout in the mode it has always had rather than doing nothing.
        /// </summary>
        internal static int[] ParseSlots(string raw, string what)
        {
            raw = (raw ?? "").Trim();
            if (raw.Length == 0) return null;

            var parts = raw.Split(',');
            var outp = new List<int>();
            for (int i = 0; i < parts.Length && outp.Count < 2; i++)
            {
                var s = parts[i].Trim();
                if (s.Length == 0) continue;
                int n;
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ||
                    n < 1 || n > HotbarSlots)
                {
                    Log.LogWarning("[Loadouts] " + what + ": '" + s + "' is not a hotbar slot number " +
                                   "(1-" + HotbarSlots + ") - falling back to the saved weapon pair");
                    return null;
                }
                outp.Add(n);
            }
            return outp.Count == 0 ? null : outp.ToArray();
        }

        /// <summary>The hotbar is row 0 of the player's own inventory, eight cells wide.</summary>
        internal const int HotbarSlots = 8;

        /// <summary>The hotbar slots loadout <paramref name="slot"/> uses, or null for saved-pair mode.</summary>
        private static int[] HotbarFor(int slot)
        {
            return slot >= 1 && slot <= _hotbar.Length ? _hotbar[slot - 1] : null;
        }

        private static KeyCode ParseKey(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return KeyCode.None;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); }
            catch
            {
                Log.LogWarning("[Loadouts] '" + s + "' is not a Unity KeyCode - that binding is off");
                return KeyCode.None;
            }
        }

        private int SlotCount { get { return Mathf.Clamp(_slots.Value, 0, MaxSlots); } }

        // ---- patches ---------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            _inst = this;
            var update = AccessTools.Method(typeof(Player), "Update");
            if (update == null) throw new Exception("Player.Update() not found");
            if (AccessTools.Method(typeof(Humanoid), "EquipItem", new[] { typeof(ItemDrop.ItemData), typeof(bool) }) == null)
                throw new Exception("Humanoid.EquipItem(ItemData,bool) not found");
            if (AccessTools.Method(typeof(Humanoid), "UnequipItem", new[] { typeof(ItemDrop.ItemData), typeof(bool) }) == null)
                throw new Exception("Humanoid.UnequipItem(ItemData,bool) not found");

            Harmony.Patch(update, postfix: new HarmonyMethod(typeof(LoadoutsModule), nameof(PlayerUpdatePostfix)));
        }

        private static void PlayerUpdatePostfix(Player __instance)
        {
            if (_inst == null || !_inst.Active || !ClientActive()) return;
            if (__instance == null || __instance != Player.m_localPlayer) return;
            try
            {
                if (!InputAllowed(__instance)) return;

                // Read through NvlbKeys, not the raw KeyCode: these are real ZInput buttons since
                // 0.8.1, so a rebind on Valheim's own Keyboard & Mouse page takes effect at once.
                // NvlbKeys falls back to the configured KeyCode if registration never happened.
                bool save = NvlbKeys.Held(SaveModifierId);
                for (int i = 0; i < _inst.SlotCount && i < _keys.Length; i++)
                {
                    int slot = i + 1;
                    if (!NvlbKeys.Down(KeyId(slot))) continue;

                    var pair = HotbarFor(slot);
                    if (pair != null) ApplyHotbarPair(__instance, slot, pair);   // nothing to save
                    else if (save) SaveLoadout(__instance, slot);
                    else ApplyLoadout(__instance, slot);
                }
            }
            catch (Exception e)
            {
                if (_tickErrors++ < 3) Log.LogError("[Loadouts] input tick failed (" + _tickErrors + "/3): " + e);
            }
        }

        private static bool InputAllowed(Player me)
        {
            if (!me.TakeInput()) return false;
            if (Hud.InRadial() || Hud.IsPieceSelectionVisible()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            if (Console.IsVisible() || TextInput.IsVisible()) return false;
            if (InventoryGui.IsVisible() || StoreGui.IsVisible() || Menu.IsVisible() || Minimap.IsOpen()) return false;
            return true;
        }

        // ---- save / apply -------------------------------------------------------------------------

        internal static void SaveLoadout(Player p, int slot)
        {
            var spec = new LoadoutSpec();
            Fill(p.RightItem, ref spec.RightPrefab, ref spec.RightQuality, ref spec.RightVariant);
            Fill(p.LeftItem, ref spec.LeftPrefab, ref spec.LeftQuality, ref spec.LeftVariant);

            if (spec.IsEmpty)
            {
                p.m_customData.Remove(KeyPrefix + slot);
                p.Message(MessageHud.MessageType.Center, "Loadout " + slot + " cleared");
                Log.LogInfo("[Loadouts] slot " + slot + " cleared (nothing in hand)");
                return;
            }

            p.m_customData[KeyPrefix + slot] = Encode(spec);
            p.Message(MessageHud.MessageType.Center, "Loadout " + slot + " saved: " + Pretty(p, spec));
            Log.LogInfo("[Loadouts] slot " + slot + " saved: " + spec);
        }

        private static void Fill(ItemDrop.ItemData item, ref string prefab, ref int quality, ref int variant)
        {
            if (item == null) return;
            var n = SlotBlob.PrefabNameOf(item);
            if (n == null) return;
            prefab = n;
            quality = item.m_quality;
            variant = item.m_variant;
        }

        internal static void ApplyLoadout(Player p, int slot)
        {
            string blob;
            if (!p.m_customData.TryGetValue(KeyPrefix + slot, out blob) || string.IsNullOrEmpty(blob))
            {
                p.Message(MessageHud.MessageType.Center,
                    "Loadout " + slot + " is empty - hold the save key and press it again to store what you hold");
                return;
            }
            var spec = Decode(blob);
            if (spec == null)
            {
                Log.LogWarning("[Loadouts] slot " + slot + " is unreadable: " + blob);
                p.Message(MessageHud.MessageType.Center, "Loadout " + slot + " is unreadable");
                return;
            }

            var inv = p.GetInventory();
            var right = Find(inv, spec.RightPrefab, spec.RightQuality, spec.RightVariant);
            var left = Find(inv, spec.LeftPrefab, spec.LeftQuality, spec.LeftVariant);

            if (right == null && left == null)
            {
                p.Message(MessageHud.MessageType.Center, "Loadout " + slot + ": nothing from it is in your inventory");
                return;
            }

            int done = 0;
            if (right != null)
            {
                if (p.IsItemEquiped(right) || p.EquipItem(right, false)) done++;
            }
            bool twoHanded = right != null && right.IsTwoHanded();
            if (left != null && !twoHanded && !ReferenceEquals(left, right))
            {
                if (p.IsItemEquiped(left) || p.EquipItem(left, false)) done++;
            }
            else if (twoHanded && p.LeftItem != null && !ReferenceEquals(p.LeftItem, right))
            {
                // A two-handed weapon owns both hands; vanilla EquipItem already cleared the left
                // hand, this is only belt and braces for a shield that survived it.
                p.UnequipItem(p.LeftItem, false);
            }

            p.Message(MessageHud.MessageType.Center, "Loadout " + slot + ": " + Pretty(p, spec));
            Log.LogInfo("[Loadouts] slot " + slot + " applied, " + done + " item(s) equipped: " + spec);
        }

        // ---- hotbar-slot mode -----------------------------------------------------------------------

        /// <summary>
        /// Equip whatever is sitting in the given hotbar slots right now: the first is the main
        /// hand, the second the off-hand. An empty slot is skipped rather than unequipping anything
        /// - "slot 2 is empty" means "leave my off-hand alone", not "drop my shield". A two-handed
        /// item in the first slot owns both hands, so the second is not even looked at.
        ///
        /// Nothing is stored and nothing is read back: the loadout IS the hotbar, which is why the
        /// save modifier does not apply to a loadout in this mode.
        /// </summary>
        internal static void ApplyHotbarPair(Player p, int slot, int[] pair)
        {
            var inv = p.GetInventory();
            if (inv == null || pair == null || pair.Length == 0) return;

            var main = HotbarItem(inv, pair[0]);
            var off = pair.Length > 1 ? HotbarItem(inv, pair[1]) : null;

            if (main == null && off == null)
            {
                p.Message(MessageHud.MessageType.Center,
                    "Loadout " + slot + ": hotbar " + Describe(pair) + " " +
                    (pair.Length > 1 ? "are" : "is") + " empty");
                return;
            }

            int done = 0;
            bool twoHanded = false;
            if (main != null)
            {
                if (p.IsItemEquiped(main) || p.EquipItem(main, false)) done++;
                twoHanded = main.IsTwoHanded();
            }
            if (off != null && !twoHanded && !ReferenceEquals(off, main))
            {
                if (p.IsItemEquiped(off) || p.EquipItem(off, false)) done++;
            }

            p.Message(MessageHud.MessageType.Center,
                "Loadout " + slot + ": slots " + Describe(pair) + " - " + Named(main, off));
            Log.LogInfo("[Loadouts] slot " + slot + " applied from hotbar " + Describe(pair) + ", " +
                        done + " item(s) equipped: " + (SlotBlob.PrefabNameOf(main) ?? "-") + " / " +
                        (twoHanded ? "(two-handed)" : (SlotBlob.PrefabNameOf(off) ?? "-")));
        }

        private static ItemDrop.ItemData HotbarItem(Inventory inv, int oneBased)
        {
            // Exactly what Player.UseHotbarItem(int) reads: column index-1 of row 0.
            int x = oneBased - 1;
            if (x < 0 || x >= inv.GetWidth()) return null;
            return inv.GetItemAt(x, 0);
        }

        /// <summary>"1+2", or just "1" for a main-hand-only pair.</summary>
        private static string Describe(int[] pair)
        {
            if (pair == null || pair.Length == 0) return "-";
            if (pair.Length == 1) return pair[0].ToString(CultureInfo.InvariantCulture);
            return pair[0].ToString(CultureInfo.InvariantCulture) + "+" +
                   pair[1].ToString(CultureInfo.InvariantCulture);
        }

        private static string Named(ItemDrop.ItemData main, ItemDrop.ItemData off)
        {
            string m = main != null && main.m_shared != null ? main.m_shared.m_name : "-";
            if (main != null && main.IsTwoHanded()) return m;
            string o = off != null && off.m_shared != null ? off.m_shared.m_name : null;
            return o == null ? m : m + " + " + o;
        }

        /// <summary>Best candidate in the inventory: exact quality+variant, else highest quality.</summary>
        internal static ItemDrop.ItemData Find(Inventory inv, string prefab, int quality, int variant)
        {
            if (inv == null || string.IsNullOrEmpty(prefab)) return null;
            ItemDrop.ItemData best = null;
            var all = inv.GetAllItems();
            for (int i = 0; i < all.Count; i++)
            {
                var it = all[i];
                if (SlotBlob.PrefabNameOf(it) != prefab) continue;
                if (it.m_quality == quality && it.m_variant == variant) return it;
                if (best == null || it.m_quality > best.m_quality) best = it;
            }
            return best;
        }

        private static string Pretty(Player p, LoadoutSpec spec)
        {
            var inv = p.GetInventory();
            var r = Find(inv, spec.RightPrefab, spec.RightQuality, spec.RightVariant);
            var l = Find(inv, spec.LeftPrefab, spec.LeftQuality, spec.LeftVariant);
            string rn = r != null && r.m_shared != null ? r.m_shared.m_name : spec.RightPrefab;
            string ln = l != null && l.m_shared != null ? l.m_shared.m_name : spec.LeftPrefab;
            if (string.IsNullOrEmpty(ln) || ln == "-") return rn;
            return rn + " + " + ln;
        }

        // ---- encoding (pure, exercised by the self test) ------------------------------------------

        /// <summary>"1|name:quality:variant|name:quality:variant", each hand "-" when empty.</summary>
        internal static string Encode(LoadoutSpec spec)
        {
            if (spec == null) return "";
            return "1|" + Hand(spec.RightPrefab, spec.RightQuality, spec.RightVariant) +
                   "|" + Hand(spec.LeftPrefab, spec.LeftQuality, spec.LeftVariant);
        }

        private static string Hand(string prefab, int quality, int variant)
        {
            if (string.IsNullOrEmpty(prefab)) return "-";
            return prefab + ":" + quality.ToString(CultureInfo.InvariantCulture) + ":" +
                   variant.ToString(CultureInfo.InvariantCulture);
        }

        internal static LoadoutSpec Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var parts = s.Split('|');
            if (parts.Length != 3 || parts[0] != "1") return null;
            var spec = new LoadoutSpec();
            if (!Hand(parts[1], ref spec.RightPrefab, ref spec.RightQuality, ref spec.RightVariant)) return null;
            if (!Hand(parts[2], ref spec.LeftPrefab, ref spec.LeftQuality, ref spec.LeftVariant)) return null;
            return spec;
        }

        private static bool Hand(string s, ref string prefab, ref int quality, ref int variant)
        {
            if (s == "-") { prefab = ""; quality = 1; variant = 0; return true; }
            var bits = s.Split(':');
            if (bits.Length != 3) return false;
            if (!int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out quality)) return false;
            if (!int.TryParse(bits[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out variant)) return false;
            prefab = bits[0];
            return true;
        }

        /// <summary>What loadout n's key is actually bound to now, falling back to the cfg value.</summary>
        private string Bound(int slot)
        {
            return Bound(KeyId(slot), slot == 1 ? _key1.Value : _key2.Value);
        }

        private static string Bound(string id, string fallback)
        {
            var s = NvlbKeys.Label(id);
            return string.IsNullOrEmpty(s) ? (fallback ?? "None") : s;
        }

        public override string StatusDetail()
        {
            // The bound key, not the configured one: they differ the moment a player rebinds on
            // Valheim's own Keyboard & Mouse page, and the bound one is the one that fires.
            var s = "slots=" + SlotCount +
                    " keys=" + Bound(1) + "," + Bound(2) +
                    " saveModifier=" + Bound(SaveModifierId, _saveModifier.Value);
            var p = Player.m_localPlayer;
            if (p != null)
                for (int i = 1; i <= SlotCount; i++)
                {
                    var pair = HotbarFor(i);
                    if (pair != null) { s += "  L" + i + "=hotbar " + Describe(pair); continue; }
                    string blob;
                    p.m_customData.TryGetValue(KeyPrefix + i, out blob);
                    var spec = Decode(blob);
                    s += "  L" + i + "=" + (spec == null ? "-" : spec.ToString());
                }
            return s;
        }
    }
}
