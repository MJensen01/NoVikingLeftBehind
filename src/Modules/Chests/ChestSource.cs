// Adapted from AzuCraftyBoxes by Azumatt - https://github.com/AzumattDev/AzuCraftyBoxes
// Licence MIT-0 (LICENSE.txt in that repo): "Permission is hereby granted, free of charge ...
// without restriction". The container registry, the "count inventory + nearby boxes" idea and
// the container-consume loop are its design; the ownership guard, the measured-removal
// bookkeeping and the per-station gating below are ours.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// One place items may come from. <see cref="C"/> is null for a bare Inventory, which is how
    /// the headless self-test drives the counting/consuming core with no world around it.
    /// </summary>
    internal struct Box
    {
        public Container C;
        public Inventory Inv;
        public string Label;

        public static Box Of(Container c, string prefabName)
        {
            return new Box { C = c, Inv = c.GetInventory(), Label = prefabName };
        }

        public static Box Of(Inventory inv, string label)
        {
            return new Box { C = null, Inv = inv, Label = label };
        }
    }

    /// <summary>
    /// The nearby-container registry and the counting/consuming core shared by every Chests patch.
    ///
    /// Registry: Container.Awake adds, Container.OnDestroyed removes. Nothing is filtered at
    /// registration time - access, privacy, distance and exclusion are all re-checked on every
    /// query, because all of them can change while a chest sits in the list (a ward is built, the
    /// player walks away, the server pushes a new ExcludedContainers).
    ///
    /// Queries are cached for the frame: a single hammer placement or crafting-panel refresh asks
    /// several times per frame and the answer cannot change in between.
    /// </summary>
    internal static class ChestSource
    {
        // ---- settings, pushed in by CraftFromChestsModule on bind and on every config change ----

        internal static float Range = 20f;
        internal static bool LeaveOne;
        internal static bool IncludeVehicles = true;
        internal static HashSet<string> ExcludedContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static HashSet<string> ExcludedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---- registry ---------------------------------------------------------------------------

        private sealed class Reg
        {
            public string Prefab;
            public bool Vehicle;
            public bool Skip;      // belongs to a character, never a source
        }

        private static readonly Dictionary<Container, Reg> _all = new Dictionary<Container, Reg>();
        private static readonly List<Container> _dead = new List<Container>();

        internal static int Registered { get { return _all.Count; } }

        internal static void Register(Container c)
        {
            if (c == null || _all.ContainsKey(c)) return;
            var reg = new Reg();
            try
            {
                reg.Prefab = Utils.GetPrefabName(c.gameObject);
                reg.Vehicle = c.m_wagon != null || c.GetComponentInParent<Ship>() != null;
                reg.Skip = c.GetComponentInParent<Character>() != null;
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Chests] could not classify a container: " + e.Message);
                reg.Prefab = "";
                reg.Skip = true;
            }
            _all[c] = reg;
        }

        internal static void Unregister(Container c)
        {
            if (c == null) return;
            _all.Remove(c);
            _cacheFrame = -1;
        }

        internal static void Clear()
        {
            _all.Clear();
            _cached.Clear();
            _cacheFrame = -1;
        }

        // ---- query ------------------------------------------------------------------------------

        private static readonly List<Box> _cached = new List<Box>(64);
        private static readonly List<Box> _empty = new List<Box>(0);
        private static int _cacheFrame = -1;
        private static Vector3 _cachePos;

        /// <summary>Containers the local player may legitimately pull from right now.</summary>
        internal static List<Box> Nearby(Vector3 pos)
        {
            int frame = Time.frameCount;
            if (frame == _cacheFrame && (pos - _cachePos).sqrMagnitude < 0.0625f) return _cached;

            _cacheFrame = frame;
            _cachePos = pos;
            _cached.Clear();

            long playerId = 0L;
            try { playerId = Game.instance.GetPlayerProfile().GetPlayerID(); }
            catch { return _empty; }

            float r2 = Range * Range;

            foreach (var kv in _all)
            {
                var c = kv.Key;
                if (c == null) { _dead.Add(c); continue; }
                var reg = kv.Value;
                if (reg.Skip) continue;
                if (!IncludeVehicles && reg.Vehicle) continue;
                if (ExcludedContainers.Contains(reg.Prefab)) continue;

                try
                {
                    if ((c.transform.position - pos).sqrMagnitude > r2) continue;

                    var nview = c.m_nview;
                    if (nview == null || !nview.IsValid()) continue;

                    var inv = c.GetInventory();
                    if (inv == null) continue;

                    // Player-built only. A dungeon/loot chest has no creator, and quietly eating
                    // the crypt loot the moment you walk past it is not what anyone asked for.
                    if (nview.GetZDO().GetLong("creator".GetStableHashCode(), 0L) == 0L) continue;

                    // Someone has it open (or a cart is being pulled): leave it alone.
                    if (c.IsInUse() && !c.IsOwner()) continue;
                    if (c.m_wagon != null && c.m_wagon.InUse()) continue;

                    // Privacy setting on the chest itself, plus the ward it sits in.
                    if (!c.CheckAccess(playerId)) continue;
                    if (c.m_checkGuardStone &&
                        !PrivateArea.CheckAccess(c.transform.position, 0f, false)) continue;

                    _cached.Add(Box.Of(c, reg.Prefab));
                }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Chests] skipping a container: " + e.Message);
                }
            }

            if (_dead.Count > 0)
            {
                foreach (var d in _dead) _all.Remove(d);
                _dead.Clear();
            }

            return _cached;
        }

        // ---- the core: count and consume --------------------------------------------------------

        /// <summary>True if this item may never be taken out of a container.</summary>
        internal static bool ItemBlocked(string prefabName, string sharedName)
        {
            if (ExcludedItems.Count == 0) return false;
            if (!string.IsNullOrEmpty(prefabName) && ExcludedItems.Contains(prefabName)) return true;
            if (!string.IsNullOrEmpty(sharedName) && ExcludedItems.Contains(sharedName)) return true;
            return false;
        }

        /// <summary>How many of <paramref name="sharedName"/> the boxes can give up (LeaveOneItem applied).</summary>
        internal static int Count(string sharedName, List<Box> boxes, int quality = -1)
        {
            if (boxes == null) return 0;
            int total = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                var inv = boxes[i].Inv;
                if (inv == null) continue;
                int have = inv.CountItems(sharedName, quality);
                if (LeaveOne) have -= 1;
                if (have > 0) total += have;
            }
            return total;
        }

        /// <summary>
        /// Take up to <paramref name="amount"/> out of the boxes. Returns how many actually left.
        ///
        /// Dupe safety, in order:
        ///   1. A container we are not the ZDO owner of is claimed first (ZNetView.ClaimOwnership
        ///      sets the owner locally and synchronously). If the claim does not stick the box is
        ///      SKIPPED, never half-consumed - a write we do not own would be overwritten by the
        ///      real owner's next ZDO push and the items would come back.
        ///   2. The removal is MEASURED (count before minus count after), never assumed, so a
        ///      partial removal (quality mismatch, world-level mismatch, another mod's patch)
        ///      cannot make the caller think it took more than it did.
        ///   3. Container.Save() writes the inventory back into the ZDO and Inventory.Changed()
        ///      wakes any open UI, both only after a removal that really happened.
        /// </summary>
        internal static int Consume(string sharedName, int amount, int itemQuality, List<Box> boxes)
        {
            if (boxes == null || amount <= 0) return 0;

            int remaining = amount;
            int removed = 0;

            for (int i = 0; i < boxes.Count && remaining > 0; i++)
            {
                var b = boxes[i];
                var inv = b.Inv;
                if (inv == null) continue;

                int have = inv.CountItems(sharedName, itemQuality);
                if (LeaveOne) have -= 1;
                if (have <= 0) continue;

                int take = Math.Min(remaining, have);
                if (take <= 0) continue;

                if (!Claim(b))
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Chests] could not take ownership of " + b.Label +
                                                  " - skipping it rather than risking a duplicate");
                    continue;
                }

                int before = inv.CountItems(sharedName, itemQuality);
                try
                {
                    inv.RemoveItem(sharedName, take, itemQuality);
                }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[Chests] RemoveItem failed on " + b.Label + ": " + e.Message);
                    continue;
                }
                int actually = before - inv.CountItems(sharedName, itemQuality);
                if (actually <= 0) continue;

                Commit(b);
                removed += actually;
                remaining -= actually;
            }

            return removed;
        }

        private static bool Claim(Box b)
        {
            if (b.C == null) return true;                 // bare inventory: the self-test
            var nview = b.C.m_nview;
            if (nview == null || !nview.IsValid()) return false;
            if (!nview.IsOwner()) nview.ClaimOwnership();
            return nview.IsOwner();
        }

        private static void Commit(Box b)
        {
            try
            {
                if (b.C != null) b.C.Save();
                if (b.Inv != null) b.Inv.Changed();
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[Chests] could not save " + b.Label + ": " + e.Message);
            }
        }

        // ---- config parsing ---------------------------------------------------------------------

        internal static HashSet<string> ParseNames(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(csv)) return set;
            foreach (var raw in csv.Split(',', ';', ' ', '\t', '\n', '\r'))
            {
                var s = raw.Trim();
                if (s.Length > 0) set.Add(s);
            }
            return set;
        }
    }
}
