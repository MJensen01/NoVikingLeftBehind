using System;
using System.Collections.Generic;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>One decoded blob entry: an item and the slot key it belongs in.</summary>
    internal sealed class SlotEntry
    {
        public string SlotKey;
        public ItemDrop.ItemData Item;
    }

    /// <summary>
    /// The side blob: how extra-slot items are turned into a string and back.
    ///
    /// Rule 1 of QOL-ARCHITECTURE section 4 is "never persist extra-slot items inside the vanilla
    /// Inventory package". So we serialise them ourselves, into a ZPackage that is base64-encoded
    /// into Player.m_customData - a Dictionary&lt;string,string&gt; that vanilla writes verbatim in
    /// Player.Save and reads back in Player.Load for save version >= 26 (verified in the 0.221.12
    /// decompile: Player.cs:4384 and :4586, the same channel the Powers module already relies on).
    ///
    /// Item fields are exactly the ones vanilla itself persists in Inventory.Save version 106
    /// (Inventory.cs:718) minus m_gridPos, which is replaced by the slot KEY. Keying by slot name
    /// rather than by grid position is what makes the blob survive a layout change: if the server
    /// later adds a utility slot, every item still lands where it belongs.
    ///
    /// Blob string layout:  "1|&lt;slotCount&gt;|&lt;base64 ZPackage&gt;"
    /// </summary>
    internal static class SlotBlob
    {
        public const int BlobVersion = 1;
        private const int ItemVersion = 106;   // mirrors Inventory.Save's own version tag

        // Sanity caps for the two counts Decode reads straight out of the package (0.8.8). The
        // real layout tops out around 30 slots, so these are generous by an order of magnitude
        // and no honest blob can reach them; they exist so a corrupt or hand-made blob cannot
        // turn a ReadInt into a multi-gigabyte allocation on the character-load path.
        private const int MaxEntries = 512;
        private const int MaxCustomData = 256;

        // ---- encode ------------------------------------------------------------------------

        /// <summary>Serialise entries into the blob string. Never throws on an odd item; it logs and skips.</summary>
        public static string Encode(IList<SlotEntry> entries)
        {
            var pkg = new ZPackage();
            pkg.Write(ItemVersion);

            var good = new List<SlotEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null || e.Item == null || string.IsNullOrEmpty(e.SlotKey)) continue;
                if (PrefabNameOf(e.Item) == null)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] cannot save item in slot " + e.SlotKey +
                                                            " - no drop prefab; it stays in the live grid");
                    continue;
                }
                good.Add(e);
            }

            pkg.Write(good.Count);
            for (int i = 0; i < good.Count; i++)
            {
                var e = good[i];
                var it = e.Item;
                pkg.Write(e.SlotKey);
                pkg.Write(PrefabNameOf(it));
                pkg.Write(it.m_stack);
                pkg.Write(it.m_durability);
                pkg.Write(it.m_equipped);
                pkg.Write(it.m_quality);
                pkg.Write(it.m_variant);
                pkg.Write(it.m_crafterID);
                pkg.Write(it.m_crafterName ?? "");
                var cd = it.m_customData;
                pkg.Write(cd == null ? 0 : cd.Count);
                if (cd != null)
                    foreach (var kv in cd) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
                pkg.Write((int)it.m_worldLevel);
                pkg.Write(it.m_pickedUp);
            }

            return BlobVersion + "|" + good.Count + "|" + pkg.GetBase64();
        }

        // ---- decode ------------------------------------------------------------------------

        /// <summary>
        /// Parse a blob string. Returns null (never throws) when it is missing or unreadable, so a
        /// corrupt blob degrades to "no extra items" and the backups can be tried instead.
        /// </summary>
        public static List<SlotEntry> Decode(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return null;
            try
            {
                var bar = blob.IndexOf('|');
                if (bar <= 0) return null;
                int ver;
                if (!int.TryParse(blob.Substring(0, bar), out ver) || ver != BlobVersion) return null;
                var bar2 = blob.IndexOf('|', bar + 1);
                if (bar2 < 0) return null;
                var b64 = blob.Substring(bar2 + 1);

                var pkg = new ZPackage(b64);
                int itemVer = pkg.ReadInt();
                if (itemVer != ItemVersion)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] blob item version " + itemVer +
                                                            " != " + ItemVersion + " - refusing to read it");
                    return null;
                }
                int count = pkg.ReadInt();
                if (count < 0 || count > MaxEntries)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] blob claims " + count +
                        " entries (max " + MaxEntries + ") - refusing to read it; a backup will be tried");
                    return null;
                }

                // Deliberately NOT new List<SlotEntry>(count): the capacity hint is the allocation,
                // so it is only ever grown by what actually decodes.
                var list = new List<SlotEntry>();
                for (int i = 0; i < count; i++)
                {
                    string slotKey = pkg.ReadString();
                    string name = pkg.ReadString();
                    int stack = pkg.ReadInt();
                    float durability = pkg.ReadSingle();
                    bool equipped = pkg.ReadBool();
                    int quality = pkg.ReadInt();
                    int variant = pkg.ReadInt();
                    long crafterID = pkg.ReadLong();
                    string crafterName = pkg.ReadString();
                    var cd = new Dictionary<string, string>();
                    int n = pkg.ReadInt();
                    if (n < 0 || n > MaxCustomData)
                    {
                        NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] blob item '" + name + "' claims " + n +
                            " custom-data pairs (max " + MaxCustomData + ") - refusing to read this blob");
                        return null;
                    }
                    for (int j = 0; j < n; j++) { var k = pkg.ReadString(); cd[k] = pkg.ReadString(); }
                    int worldLevel = pkg.ReadInt();
                    bool pickedUp = pkg.ReadBool();

                    var item = MakeItem(name, stack, durability, equipped, quality, variant,
                                        crafterID, crafterName, cd, worldLevel, pickedUp);
                    if (item == null)
                    {
                        NoVikingLeftBehindPlugin.Log.LogWarning("[Slots] blob item '" + name +
                                                                "' not in ObjectDB - skipped (slot " + slotKey + ")");
                        continue;
                    }
                    list.Add(new SlotEntry { SlotKey = slotKey, Item = item });
                }
                return list;
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[Slots] blob decode failed: " + e.Message);
                return null;
            }
        }

        // ---- item construction ---------------------------------------------------------------

        /// <summary>
        /// Build a live ItemData from persisted fields, exactly the way the private
        /// Inventory.AddItem(string, int, float, Vector2i, ...) does it (Inventory.cs:897):
        /// instantiate the ObjectDB prefab with ZNetView.m_forceDisableInit, copy the fields off
        /// the component, clone, destroy the temporary GameObject. Also used by the migration
        /// rescue, which is why it lives here and not inside the blob decoder.
        /// </summary>
        public static ItemDrop.ItemData MakeItem(string name, int stack, float durability, bool equipped,
                                                 int quality, int variant, long crafterID, string crafterName,
                                                 Dictionary<string, string> customData, int worldLevel, bool pickedUp)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (ObjectDB.instance == null) return null;
            var prefab = ObjectDB.instance.GetItemPrefab(name);
            if (prefab == null) return null;

            GameObject go = null;
            try
            {
                ZNetView.m_forceDisableInit = true;
                go = UnityEngine.Object.Instantiate(prefab);
            }
            finally { ZNetView.m_forceDisableInit = false; }

            try
            {
                var drop = go.GetComponent<ItemDrop>();
                if (drop == null) return null;
                var d = drop.m_itemData;
                d.m_stack = Mathf.Min(Mathf.Max(1, stack), d.m_shared.m_maxStackSize);
                d.m_durability = durability;
                d.m_equipped = equipped;
                drop.SetQuality(quality);
                d.m_variant = variant;
                d.m_crafterID = crafterID;
                d.m_crafterName = crafterName ?? "";
                if (customData != null) d.m_customData = customData;
                d.m_worldLevel = (byte)worldLevel;
                d.m_pickedUp = pickedUp;
                var clone = d.Clone();
                clone.m_dropPrefab = prefab;
                return clone;
            }
            finally
            {
                UnityEngine.Object.Destroy(go);
            }
        }

        /// <summary>Prefab name as Inventory.Save writes it, or null when the item cannot be saved.</summary>
        public static string PrefabNameOf(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            if (item.m_dropPrefab != null) return item.m_dropPrefab.name;
            return null;
        }

        public static string Describe(ItemDrop.ItemData item)
        {
            if (item == null) return "null";
            var n = PrefabNameOf(item);
            return (n ?? (item.m_shared != null ? item.m_shared.m_name : "?")) + " x" + item.m_stack +
                   (item.m_quality > 1 ? " q" + item.m_quality : "");
        }
    }
}
