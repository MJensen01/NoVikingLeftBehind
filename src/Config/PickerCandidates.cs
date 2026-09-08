using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>One thing that can be ticked in a picker.</summary>
    internal sealed class Candidate
    {
        /// <summary>The name as it is stored in the cfg file, e.g. "rock4_copper".</summary>
        public string Prefab;

        /// <summary>Localised human name, e.g. "Copper deposit". Falls back to <see cref="Prefab"/>.</summary>
        public string Display;

        /// <summary>Optional grouping label ("Materials", "Food", ...), or null when there is none.</summary>
        public string Group;
    }

    /// <summary>
    /// One entry of a list setting, taken apart. <see cref="Raw"/> is set - and everything else
    /// left empty - for an entry this could not understand; the serialiser then writes that entry
    /// back exactly as it found it, so a value it does not recognise is never destroyed by a trip
    /// through the picker.
    ///
    /// The Src* fields are bookkeeping the parser fills in and the serialiser reads. They hold the
    /// ORIGINAL text of each token, whitespace and all, which is how re-serialising an untouched
    /// value comes back byte-identical: a number nobody changed is written out with the exact text
    /// it had, so "0.75" never becomes "0.7500" and "1" never becomes "1.0". A hand-built entry
    /// leaves them null and is written in the canonical shortest form instead.
    /// </summary>
    internal sealed class Entry
    {
        public string Prefab;

        /// <summary>
        /// One per <see cref="PickerSpec.Fields"/>, in order. May be shorter than the spec when the
        /// stored entry left trailing numbers off (see <see cref="PickerSpec.TrailingFieldsOptional"/>).
        /// </summary>
        public double[] Fields;

        /// <summary>Set when <see cref="PickerSpec.TrailingPrefabLabel"/> is, else null.</summary>
        public string TrailingPrefab;

        /// <summary>The original text of an entry that would not parse. Null for a good entry.</summary>
        public string Raw;

        internal string SrcPrefab;
        internal string[] SrcFields;
        internal string SrcTrailing;
    }

    /// <summary>
    /// Everything a picker needs to know about the world it is offering: what can be ticked, what
    /// each thing is called, and how the owning module's setting string comes apart and goes back
    /// together again.
    ///
    /// Every candidate list is resolved out of <c>ObjectDB</c> and <c>ZNetScene</c> at the moment
    /// it is asked for, so it always matches the world actually being played - including whatever
    /// another mod has added to it. On the main menu neither exists and <see cref="Available"/> is
    /// false; every provider then hands back an empty list rather than guessing.
    ///
    /// Nothing here may throw. It all runs inside a menu, several layers below Unity's own UI
    /// callbacks, where an exception is swallowed silently and leaves the settings tab half drawn.
    /// Every public entry point wraps its body and reports through
    /// <see cref="NoVikingLeftBehindPlugin.Log"/>.
    ///
    /// The parse/serialise half is the load-bearing part. NO MODULE CHANGES HOW IT PARSES ITS OWN
    /// SETTING: the picker takes the setting's existing string apart, hands the pieces to the UI,
    /// and puts them back in exactly the shape it found them. The rule the round trip has to keep
    /// is byte-identity - a value that goes through the picker unchanged must come out of it
    /// character for character the same, spacing and decimals included - because the tweak door
    /// compares strings to decide whether a setting actually changed.
    /// </summary>
    internal static class PickerCandidates
    {
        private static readonly double[] NoFields = new double[0];
        private static readonly List<Candidate> NoCandidates = new List<Candidate>();

        // ---- cache -----------------------------------------------------------------------------
        //
        // A picker's search box asks for the same list on every keystroke, and Items is the whole
        // of ObjectDB. The list is therefore built once per (source, filter) and thrown away as
        // soon as either instance is replaced - which is exactly when the world changed underneath
        // it (a different world loaded, or a mod rebuilt ObjectDB).

        private static readonly Dictionary<string, List<Candidate>> _cache =
            new Dictionary<string, List<Candidate>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, string>> _displayCache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        private static object _cachedScene;
        private static object _cachedDb;

        /// <summary>
        /// Every catch in this file ends here. It is null-guarded because the picker is reachable
        /// from a menu, and a logger that is not there yet must not turn a swallowed problem into
        /// a thrown one.
        /// </summary>
        private static void Warn(string message)
        {
            try
            {
                var log = NoVikingLeftBehindPlugin.Log;
                if (log != null) log.LogWarning(message);
            }
            catch { }
        }

        /// <summary>True when the world is loaded enough to answer at all.</summary>
        internal static bool Available
        {
            get
            {
                try { return ZNetScene.instance != null && ObjectDB.instance != null; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Every candidate for this spec, sorted by <see cref="Candidate.Display"/>. Never null,
        /// empty when the world is not loaded. The caller gets its own list and may sort or filter
        /// it without disturbing the cache.
        /// </summary>
        internal static List<Candidate> For(PickerSpec spec)
        {
            if (spec == null) return new List<Candidate>();
            try
            {
                return new List<Candidate>(Cached(spec));
            }
            catch (Exception e)
            {
                Warn("picker: could not list candidates for " + spec.Source + ": " + e.Message);
                return new List<Candidate>();
            }
        }

        /// <summary>
        /// Display name for one prefab name, or the prefab name itself when this world has never
        /// heard of it (a name left over from a mod that is not loaded, say).
        /// </summary>
        internal static string DisplayFor(PickerSpec spec, string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return prefab;
            if (spec == null) return prefab;
            try
            {
                Cached(spec);   // fills the display map for this key as a side effect
                Dictionary<string, string> map;
                string display;
                if (_displayCache.TryGetValue(Key(spec), out map) && map != null &&
                    map.TryGetValue(prefab, out display) && !string.IsNullOrEmpty(display))
                    return display;
            }
            catch (Exception e)
            {
                Warn("picker: could not name " + prefab + ": " + e.Message);
            }
            return prefab;
        }

        // ---- providers ---------------------------------------------------------------------------

        private static string Key(PickerSpec spec)
        {
            return ((int)spec.Source).ToString(CultureInfo.InvariantCulture) + "|" +
                   (spec.Filter ?? "");
        }

        private static List<Candidate> Cached(PickerSpec spec)
        {
            if (!Available)
            {
                Invalidate(null, null);
                return NoCandidates;
            }

            object scene = ZNetScene.instance;
            object db = ObjectDB.instance;
            if (!ReferenceEquals(scene, _cachedScene) || !ReferenceEquals(db, _cachedDb))
                Invalidate(scene, db);

            string key = Key(spec);
            List<Candidate> list;
            if (_cache.TryGetValue(key, out list)) return list;

            list = Build(spec);
            list.Sort(CompareCandidates);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in list)
                if (c != null && !string.IsNullOrEmpty(c.Prefab)) map[c.Prefab] = c.Display;

            _cache[key] = list;
            _displayCache[key] = map;
            return list;
        }

        private static void Invalidate(object scene, object db)
        {
            _cache.Clear();
            _displayCache.Clear();
            _cachedScene = scene;
            _cachedDb = db;
        }

        private static int CompareCandidates(Candidate a, Candidate b)
        {
            if (a == null) return b == null ? 0 : 1;
            if (b == null) return -1;
            int d = string.Compare(a.Display ?? "", b.Display ?? "",
                                   StringComparison.OrdinalIgnoreCase);
            return d != 0 ? d : string.CompareOrdinal(a.Prefab ?? "", b.Prefab ?? "");
        }

        private static List<Candidate> Build(PickerSpec spec)
        {
            switch (spec.Source)
            {
                case PickerSource.OreNodes: return OreNodes(spec);
                case PickerSource.Items: return Items(spec, null);
                case PickerSource.Materials: return Items(spec, ItemDrop.ItemData.ItemType.Material);
                case PickerSource.Foods: return Foods(spec);
                case PickerSource.Torches: return Items(spec, ItemDrop.ItemData.ItemType.Torch);
                case PickerSource.Stations: return Components<CraftingStation>(spec);
                case PickerSource.CookingStations: return Components<CookingStation>(spec);
                case PickerSource.Fireplaces: return Components<Fireplace>(spec);
                case PickerSource.Pieces: return Pieces(spec);
                case PickerSource.Envs: return Envs(spec);
                case PickerSource.Traders: return Components<Trader>(spec);
                default: return new List<Candidate>();
            }
        }

        /// <summary>
        /// Mineable nodes, from <c>ZNetScene.m_prefabs</c>.
        ///
        /// The component test is FastMining's own <c>FamilyOf</c>, in its order and with its
        /// <c>GetComponentInChildren&lt;T&gt;(true)</c> reach into inactive children: MineRock5,
        /// then MineRock, then Destructible. That is deliberate - the [Mining] OreNodes list and
        /// [Regrowth] Prefabs list are resolved by name against exactly these three families, so
        /// anything this offers is something those modules will accept.
        ///
        /// The Destructible arm is the one place this is NARROWER than the module. Every crate,
        /// barrel, pot and bush in the game is a Destructible; offering all of them would bury the
        /// seven ore veins in about a thousand rows. So a Destructible is offered only when it can
        /// actually be mined - it takes pickaxe damage, it is not a tree or a creature, and it
        /// either drops something or turns into something when it breaks, which is how every
        /// vanilla vein works (tin drops through DropOnDestroyed, copper and silver spawn their
        /// _frac stage). Filter "all" turns that narrowing off and offers every prefab the module
        /// would accept, for a modded ore built on something unusual. Nothing here stops a name
        /// being typed by hand either way.
        /// </summary>
        private static List<Candidate> OreNodes(PickerSpec spec)
        {
            var list = new List<Candidate>();
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return list;

            bool all = HasFilterWord(spec, "all");
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var go in scene.m_prefabs)
            {
                if (go == null) continue;
                string name = go.name;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                var mr5 = go.GetComponentInChildren<MineRock5>(true);
                if (mr5 != null)
                {
                    list.Add(new Candidate
                    {
                        Prefab = name,
                        Display = Localise(mr5.m_name, name),
                        Group = "Rock nodes"
                    });
                    continue;
                }

                var mr = go.GetComponentInChildren<MineRock>(true);
                if (mr != null)
                {
                    list.Add(new Candidate
                    {
                        Prefab = name,
                        Display = Localise(mr.m_name, name),
                        Group = "Rock nodes"
                    });
                    continue;
                }

                var d = go.GetComponentInChildren<Destructible>(true);
                if (d == null) continue;
                if (!all && !IsMineable(d)) continue;

                list.Add(new Candidate
                {
                    Prefab = name,
                    Display = DestructibleName(d, name, 0),
                    Group = "Destructibles"
                });
            }
            return list;
        }

        /// <summary>Can a pickaxe take this apart, and is there anything in it when it breaks?</summary>
        private static bool IsMineable(Destructible d)
        {
            if (d == null) return false;
            if (d.m_destructibleType == DestructibleType.Tree ||
                d.m_destructibleType == DestructibleType.Character) return false;

            var pickaxe = d.m_damages.m_pickaxe;
            if (pickaxe == HitData.DamageModifier.Immune ||
                pickaxe == HitData.DamageModifier.Ignore) return false;

            if (d.m_spawnWhenDestroyed != null) return true;
            return d.GetComponentInChildren<DropOnDestroyed>(true) != null;
        }

        /// <summary>
        /// A Destructible carries no name of its own - it is not Hoverable, it has no m_name. An
        /// un-fractured copper vein is therefore anonymous until you follow m_spawnWhenDestroyed to
        /// the MineRock5 it becomes on the first hit, which is where "$piece_mine_copper" lives.
        /// One hop is enough for every vanilla ore; the depth guard is there for a modded chain
        /// that spawns itself.
        /// </summary>
        private static string DestructibleName(Destructible d, string fallback, int depth)
        {
            if (d == null || depth > 2) return fallback;

            var next = d.m_spawnWhenDestroyed;
            if (next == null) return fallback;

            var mr5 = next.GetComponentInChildren<MineRock5>(true);
            if (mr5 != null && !string.IsNullOrEmpty(mr5.m_name)) return Localise(mr5.m_name, fallback);

            var mr = next.GetComponentInChildren<MineRock>(true);
            if (mr != null && !string.IsNullOrEmpty(mr.m_name)) return Localise(mr.m_name, fallback);

            return DestructibleName(next.GetComponentInChildren<Destructible>(true), fallback, depth + 1);
        }

        /// <summary>
        /// Items out of <c>ObjectDB.m_items</c>. <paramref name="only"/> null takes the lot; the
        /// spec's Filter may narrow it further to a comma-separated list of ItemType names
        /// ("Material,Consumable"), which is how a setting that wants, say, only ammo asks for it.
        /// </summary>
        private static List<Candidate> Items(PickerSpec spec, ItemDrop.ItemData.ItemType? only)
        {
            var list = new List<Candidate>();
            var db = ObjectDB.instance;
            if (db == null || db.m_items == null) return list;

            var wanted = FilterTypes(spec);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var go in db.m_items)
            {
                if (go == null) continue;
                string name = go.name;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                var shared = SharedOf(go);
                if (shared == null) continue;

                var type = shared.m_itemType;
                if (only.HasValue && type != only.Value) continue;
                if (wanted != null && !wanted.Contains((int)type)) continue;

                list.Add(new Candidate
                {
                    Prefab = name,
                    Display = Localise(shared.m_name, name),
                    Group = GroupOf(shared)
                });
            }
            return list;
        }

        /// <summary>
        /// Anything that can be eaten. Not "m_itemType == Consumable" alone: meads are Consumables
        /// too. The test is the game's own, from ItemDrop's tooltip - a consumable with health,
        /// stamina or eitr on it is food.
        /// </summary>
        private static List<Candidate> Foods(PickerSpec spec)
        {
            var list = new List<Candidate>();
            var db = ObjectDB.instance;
            if (db == null || db.m_items == null) return list;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var go in db.m_items)
            {
                if (go == null) continue;
                string name = go.name;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                var shared = SharedOf(go);
                if (shared == null) continue;
                if (shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                if (shared.m_food <= 0f && shared.m_foodStamina <= 0f && shared.m_foodEitr <= 0f) continue;

                list.Add(new Candidate
                {
                    Prefab = name,
                    Display = Localise(shared.m_name, name),
                    Group = "Food"
                });
            }
            return list;
        }

        /// <summary>
        /// Prefabs in <c>ZNetScene.m_prefabs</c> carrying one component: CraftingStation,
        /// CookingStation, Fireplace or Trader. All four keep their displayed name in a plain
        /// public m_name string, so one generic pass covers them.
        /// </summary>
        private static List<Candidate> Components<T>(PickerSpec spec) where T : Component
        {
            var list = new List<Candidate>();
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return list;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var go in scene.m_prefabs)
            {
                if (go == null) continue;
                string name = go.name;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                var comp = go.GetComponent<T>();
                if (comp == null) continue;

                list.Add(new Candidate
                {
                    Prefab = name,
                    Display = Localise(NameOf(comp), name),
                    Group = null
                });
            }
            return list;
        }

        /// <summary>
        /// Buildable pieces, grouped by their build-menu tab. Filter may narrow it to a
        /// comma-separated list of PieceCategory names ("BuildingWorkbench,Furniture").
        /// </summary>
        private static List<Candidate> Pieces(PickerSpec spec)
        {
            var list = new List<Candidate>();
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null) return list;

            var wanted = FilterCategories(spec);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var go in scene.m_prefabs)
            {
                if (go == null) continue;
                string name = go.name;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                var piece = go.GetComponent<Piece>();
                if (piece == null) continue;
                if (wanted != null && !wanted.Contains((int)piece.m_category)) continue;

                list.Add(new Candidate
                {
                    Prefab = name,
                    Display = Localise(piece.m_name, name),
                    Group = ConfigCatalog.Humanise(piece.m_category.ToString())
                });
            }
            return list;
        }

        /// <summary>
        /// EnvMan's weather list. These are not prefabs and not localised - "Clear", "Rain",
        /// "Misty" are the names a setting stores and the names a player reads - so Display is the
        /// name itself, put through Localize only in case a mod added a token.
        /// </summary>
        private static List<Candidate> Envs(PickerSpec spec)
        {
            var list = new List<Candidate>();
            var man = EnvMan.instance;
            if (man == null || man.m_environments == null) return list;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var env in man.m_environments)
            {
                if (env == null) continue;
                string name = env.m_name;
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

                list.Add(new Candidate
                {
                    Prefab = name,
                    Display = Localise(name, name),
                    Group = null
                });
            }
            return list;
        }

        // ---- naming ------------------------------------------------------------------------------

        private static ItemDrop.ItemData.SharedData SharedOf(GameObject go)
        {
            var drop = go.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData == null) return null;
            return drop.m_itemData.m_shared;
        }

        /// <summary>The m_name field of whichever of the four named components this is.</summary>
        private static string NameOf(Component comp)
        {
            var station = comp as CraftingStation;
            if (station != null) return station.m_name;
            var cooking = comp as CookingStation;
            if (cooking != null) return cooking.m_name;
            var fire = comp as Fireplace;
            if (fire != null) return fire.m_name;
            var trader = comp as Trader;
            if (trader != null) return trader.m_name;
            return null;
        }

        /// <summary>
        /// A token through Localization, or the prefab name when that does not produce something a
        /// player would recognise. Localize leaves a plain string alone and turns an unknown token
        /// into "MISSING KEY"-flavoured text rather than failing, so both of those - and a token it
        /// left with its '$' still on - fall back to the prefab name.
        /// </summary>
        private static string Localise(string token, string fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(token)) return fallback;
                var loc = Localization.instance;
                if (loc == null) return fallback;

                string s = loc.Localize(token);
                if (string.IsNullOrEmpty(s)) return fallback;
                s = s.Trim();
                if (s.Length == 0) return fallback;
                if (s.IndexOf('$') >= 0) return fallback;
                if (s.IndexOf("MISSING KEY", StringComparison.Ordinal) >= 0) return fallback;
                if (s.IndexOf("MISSING BUTTON", StringComparison.Ordinal) >= 0) return fallback;
                return s;
            }
            catch { return fallback; }
        }

        /// <summary>The cheap category the item type already gives us, for a UI grouping header.</summary>
        private static string GroupOf(ItemDrop.ItemData.SharedData shared)
        {
            switch (shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Material:
                    return "Materials";
                case ItemDrop.ItemData.ItemType.Consumable:
                    return (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f)
                        ? "Food" : "Other";
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                    return "Weapons";
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                    return "Armour";
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                    return "Tools";
                default:
                    return "Other";
            }
        }

        // ---- filters -----------------------------------------------------------------------------

        private static bool HasFilterWord(PickerSpec spec, string word)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Filter)) return false;
            foreach (var part in spec.Filter.Split(','))
                if (string.Equals(part.Trim(), word, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Filter read as ItemType names, or null when it names none of them.</summary>
        private static HashSet<int> FilterTypes(PickerSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Filter)) return null;
            var set = new HashSet<int>();
            foreach (var part in spec.Filter.Split(','))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                try
                {
                    // Convert.ToInt32, not a plain (int) cast: Enum.Parse hands back a BOXED enum,
                    // and unboxing that straight to int throws at runtime.
                    set.Add(Convert.ToInt32(Enum.Parse(typeof(ItemDrop.ItemData.ItemType), s, true),
                                            CultureInfo.InvariantCulture));
                }
                catch { /* not an item type - some other provider's filter word */ }
            }
            return set.Count > 0 ? set : null;
        }

        /// <summary>Filter read as PieceCategory names, or null when it names none of them.</summary>
        private static HashSet<int> FilterCategories(PickerSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Filter)) return null;
            var set = new HashSet<int>();
            foreach (var part in spec.Filter.Split(','))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                try
                {
                    set.Add(Convert.ToInt32(Enum.Parse(typeof(Piece.PieceCategory), s, true),
                                            CultureInfo.InvariantCulture));
                }
                catch { /* not a category - ignored */ }
            }
            return set.Count > 0 ? set : null;
        }

        // ---- parse ---------------------------------------------------------------------------------

        /// <summary>
        /// Take a stored setting value apart into entries, in order.
        ///
        /// The shapes actually in the config today, all of them covered here:
        ///   Wood,FineWood,RoundLog                       plain names
        ///   rock4_copper:1,MineRock_Tin:1                name:number
        ///   BCA_CookingPot:0.75                          name:number, not whole
        ///   Bronze:5:60,Iron:5:80                        name:number:number
        ///   rock4_copper_frac:1:rock4_copper             name:number:name
        ///   MineRock_Tin:1                               ...the same setting, trailing name left off
        ///
        /// Rules, in order:
        ///   * the value is split on EntrySeparator, and NOTHING ELSE - an entry keeps whatever it
        ///     contained, including its whitespace;
        ///   * an entry that is empty or all whitespace is kept as a Raw entry, so a trailing comma
        ///     survives the round trip;
        ///   * within an entry, tokens are split on FieldSeparator: the first is the prefab, then
        ///     one token per PickerSpec.Fields, then the trailing prefab when the spec has one;
        ///   * missing trailing tokens are allowed and simply produce a shorter Fields array. That
        ///     is what TrailingFieldsOptional describes, and it is accepted whether or not the flag
        ///     is set: the alternative is refusing an entry a module already reads happily;
        ///   * a token where a number was expected that is not a number makes the whole entry Raw.
        ///     It is tempting to read `frac:whole` - a hand-edited OreRegrowth line with the tier
        ///     left out - as a name in the trailing slot, but OreRegrowth itself does not read it
        ///     that way (its parser only looks for a respawn name in the THIRD part), and a picker
        ///     that quietly rewrote it into `frac:1:whole` would be changing a value it was only
        ///     asked to display. Kept verbatim instead;
        ///   * more tokens than the spec has room for is Raw.
        ///
        /// Never throws. An entry it cannot understand comes back with Fields empty and Raw set to
        /// its original text, and the serialiser writes that text straight back out.
        /// </summary>
        internal static List<Entry> Parse(PickerSpec spec, string stored)
        {
            var list = new List<Entry>();
            try
            {
                if (spec == null || string.IsNullOrEmpty(stored)) return list;

                char entrySep = EntrySep(spec);
                char fieldSep = FieldSep(spec);
                int fieldCount = spec.Fields != null ? spec.Fields.Length : 0;
                bool wantsTrailing = !string.IsNullOrEmpty(spec.TrailingPrefabLabel);

                foreach (var chunk in stored.Split(entrySep))
                    list.Add(ParseOne(chunk, fieldSep, fieldCount, wantsTrailing));
            }
            catch (Exception e)
            {
                Warn("picker: could not parse a list value: " + e.Message);
            }
            return list;
        }

        private static Entry ParseOne(string chunk, char fieldSep, int fieldCount, bool wantsTrailing)
        {
            if (chunk == null) chunk = "";
            if (chunk.Trim().Length == 0) return Unreadable(chunk);

            var tokens = chunk.Split(fieldSep);
            int max = 1 + fieldCount + (wantsTrailing ? 1 : 0);
            if (tokens.Length > max) return Unreadable(chunk);

            string prefab = tokens[0].Trim();
            if (prefab.Length == 0) return Unreadable(chunk);

            var srcFields = new string[fieldCount];
            var values = new List<double>(fieldCount);
            string trailing = null, trailingSrc = null;

            int t = 1;
            for (int j = 0; j < fieldCount && t < tokens.Length; j++, t++)
            {
                string tok = tokens[t];
                double d;
                if (!TryNumber(tok, out d)) return Unreadable(chunk);

                values.Add(d);
                srcFields[j] = tok;
            }

            if (wantsTrailing && t < tokens.Length)
            {
                string tok = tokens[t];
                if (tok.Trim().Length == 0) return Unreadable(chunk);
                trailingSrc = tok;
                trailing = tok.Trim();
                t++;
            }

            if (t < tokens.Length) return Unreadable(chunk);

            return new Entry
            {
                Prefab = prefab,
                Fields = values.Count == 0 ? NoFields : values.ToArray(),
                TrailingPrefab = trailing,
                SrcPrefab = tokens[0],
                SrcFields = srcFields,
                SrcTrailing = trailingSrc
            };
        }

        private static Entry Unreadable(string chunk)
        {
            return new Entry { Fields = NoFields, Raw = chunk };
        }

        // ---- serialise ------------------------------------------------------------------------------

        /// <summary>
        /// Put entries back into the setting's stored form.
        ///
        /// Byte-identity is the whole point, and it comes from one rule: a token nobody changed is
        /// written back with the exact text it arrived with. So the spacing convention of the file
        /// survives (a value with no spaces after its commas keeps none, one with spaces keeps
        /// them), and so does the way its numbers were written - "0.75" stays "0.75", "1" stays "1"
        /// and never becomes "1.0". Only a token whose value actually differs from what the source
        /// text says is reformatted, and even then it keeps the padding that was around it.
        ///
        /// An entry with Raw set - one the parser could not read - is written straight back out,
        /// which is why running an unfamiliar value through the picker cannot damage it.
        /// </summary>
        internal static string Serialise(PickerSpec spec, List<Entry> entries)
        {
            var sb = new StringBuilder();
            try
            {
                if (entries == null || entries.Count == 0) return "";
                if (spec == null) return "";

                char entrySep = EntrySep(spec);
                char fieldSep = FieldSep(spec);
                int fieldCount = spec.Fields != null ? spec.Fields.Length : 0;
                bool wantsTrailing = !string.IsNullOrEmpty(spec.TrailingPrefabLabel);

                // Per entry, not per value: one entry that somehow goes wrong must not truncate
                // the rest of the list on its way out and quietly shorten the setting.
                for (int i = 0; i < entries.Count; i++)
                {
                    string one;
                    try
                    {
                        one = SerialiseOne(spec, entries[i], entrySep, fieldSep, fieldCount, wantsTrailing);
                    }
                    catch (Exception ex)
                    {
                        Warn("picker: could not write one list entry: " + ex.Message);
                        var bad = entries[i];
                        one = bad == null ? "" : (bad.Raw ?? bad.Prefab ?? "");
                    }
                    if (i > 0) sb.Append(entrySep);
                    sb.Append(one);
                }
            }
            catch (Exception e)
            {
                Warn("picker: could not write a list value: " + e.Message);
            }
            return sb.ToString();
        }

        private static string SerialiseOne(PickerSpec spec, Entry e, char entrySep, char fieldSep,
                                           int fieldCount, bool wantsTrailing)
        {
            if (e == null) return "";
            if (e.Raw != null) return e.Raw;

            var parts = new List<string>(1 + fieldCount + 1);
            parts.Add(Render(e.SrcPrefab, Clean(e.Prefab, entrySep, fieldSep)));

            bool hasTrailing = wantsTrailing && !string.IsNullOrEmpty(e.TrailingPrefab);
            int have = e.Fields != null ? e.Fields.Length : 0;

            // A trailing prefab cannot be written without the numbers in front of it: `a:whole`
            // would read back as `a:field`. When one is set, every numeric slot is filled, using
            // the field's own default for any the caller never supplied.
            int count = hasTrailing ? fieldCount : Math.Min(have, fieldCount);

            for (int j = 0; j < count; j++)
            {
                var field = FieldAt(spec, j);
                double v = (e.Fields != null && j < e.Fields.Length)
                    ? e.Fields[j]
                    : (field != null ? field.Default : 0.0);
                parts.Add(RenderNumber(SrcField(e, j), v, field));
            }

            if (hasTrailing)
                parts.Add(Render(e.SrcTrailing, Clean(e.TrailingPrefab, entrySep, fieldSep)));

            return string.Join(fieldSep.ToString(), parts.ToArray());
        }

        /// <summary>Original text when the value still matches it, otherwise the value in its padding.</summary>
        private static string Render(string src, string value)
        {
            if (value == null) value = "";
            if (src == null) return value;
            if (string.Equals(src.Trim(), value, StringComparison.Ordinal)) return src;
            return LeadingSpace(src) + value + TrailingSpace(src);
        }

        private static string RenderNumber(string src, double value, PickerField field)
        {
            if (src != null)
            {
                double had;
                if (TryNumber(src, out had) && had.Equals(value)) return src;
            }
            string text = FormatNumber(value, field);
            return src == null ? text : LeadingSpace(src) + text + TrailingSpace(src);
        }

        /// <summary>
        /// The shortest text that reads back as this number. A whole number - either because the
        /// field says so or because that is what it is - is written without a decimal point, so a
        /// tier stays "1" and never becomes "1.0"; anything else gets as many decimals as it needs
        /// and no more.
        /// </summary>
        private static string FormatNumber(double value, PickerField field)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = field != null ? field.Default : 0.0;
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0.0;

            bool whole = (field != null && field.WholeNumbers) || value == Math.Floor(value);
            if (whole && Math.Abs(value) < 9.0e18)
                return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);

            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static bool TryNumber(string s, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// A separator inside a name would produce a value that reads back as something else, so a
        /// freshly written name has them stripped. A name that came out of the file cannot contain
        /// one - it was split on them - and is written back untouched by <see cref="Render"/>.
        /// </summary>
        private static string Clean(string value, char entrySep, char fieldSep)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOf(entrySep) < 0 && value.IndexOf(fieldSep) < 0) return value;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                if (c != entrySep && c != fieldSep) sb.Append(c);
            return sb.ToString();
        }

        // ---- small helpers ----------------------------------------------------------------------------

        private static char EntrySep(PickerSpec spec)
        {
            return spec.EntrySeparator == '\0' ? ',' : spec.EntrySeparator;
        }

        private static char FieldSep(PickerSpec spec)
        {
            return spec.FieldSeparator == '\0' ? ':' : spec.FieldSeparator;
        }

        private static PickerField FieldAt(PickerSpec spec, int index)
        {
            if (spec == null || spec.Fields == null) return null;
            return index >= 0 && index < spec.Fields.Length ? spec.Fields[index] : null;
        }

        private static string SrcField(Entry e, int index)
        {
            if (e == null || e.SrcFields == null) return null;
            return index >= 0 && index < e.SrcFields.Length ? e.SrcFields[index] : null;
        }

        private static string LeadingSpace(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return s.Substring(0, i);
        }

        private static string TrailingSpace(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.Length;
            while (i > 0 && char.IsWhiteSpace(s[i - 1])) i--;
            // An all-whitespace token has already been counted as leading; do not double it.
            return i == 0 ? "" : s.Substring(i);
        }
    }
}
