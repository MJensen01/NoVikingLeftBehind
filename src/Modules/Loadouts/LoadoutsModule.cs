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
    /// </summary>
    internal sealed class LoadoutsModule : FeatureModule
    {
        public override string Name => "Loadouts";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "Loadouts";

        public const string KeyPrefix = "nvlb.loadout";
        public const int MaxSlots = 4;

        private static LoadoutsModule _inst;

        private ConfigEntry<int> _slots;
        private ConfigEntry<string> _key1;
        private ConfigEntry<string> _key2;
        private ConfigEntry<string> _saveModifier;

        private static KeyCode[] _keys = new KeyCode[0];
        private static KeyCode _modifier = KeyCode.LeftControl;
        private static int _tickErrors;

        // ---- config ---------------------------------------------------------------------------

        protected override void Bind()
        {
            _slots = BindSynced("Slots", 2, "Server: how many weapon loadouts each player gets (0-" + MaxSlots + ").");
            _key1 = BindLocal("Loadout1Key", "V", "Local: key that equips loadout 1. Unity KeyCode name, or None.");
            _key2 = BindLocal("Loadout2Key", "B", "Local: key that equips loadout 2. Unity KeyCode name, or None.");
            _saveModifier = BindLocal("SaveModifier", "LeftControl",
                "Local: hold this and press a loadout key to SAVE what you are currently holding " +
                "into that loadout instead of equipping it.");
            ParseKeys();
        }

        public override void OnConfigChanged(ConfigEntryBase entry) { ParseKeys(); }

        private void ParseKeys()
        {
            var names = new List<string> { _key1.Value, _key2.Value, "None", "None" };
            var keys = new KeyCode[MaxSlots];
            for (int i = 0; i < MaxSlots; i++) keys[i] = ParseKey(names[i]);
            _keys = keys;
            _modifier = ParseKey(_saveModifier.Value);
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
                bool save = _modifier != KeyCode.None && ZInput.GetKey(_modifier, false);
                for (int i = 0; i < _inst.SlotCount && i < _keys.Length; i++)
                {
                    if (_keys[i] == KeyCode.None) continue;
                    if (!ZInput.GetKeyDown(_keys[i], false)) continue;
                    if (save) SaveLoadout(__instance, i + 1);
                    else ApplyLoadout(__instance, i + 1);
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

        public override string StatusDetail()
        {
            var s = "slots=" + SlotCount + " keys=" + _key1.Value + "," + _key2.Value +
                    " saveModifier=" + _saveModifier.Value;
            var p = Player.m_localPlayer;
            if (p != null)
                for (int i = 1; i <= SlotCount; i++)
                {
                    string blob;
                    p.m_customData.TryGetValue(KeyPrefix + i, out blob);
                    var spec = Decode(blob);
                    s += "  L" + i + "=" + (spec == null ? "-" : spec.ToString());
                }
            return s;
        }
    }
}
