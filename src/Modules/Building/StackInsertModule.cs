using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **StackInsert** - hold a modifier (default LeftShift) while using a smelter's ore or fuel
    /// switch and the whole stack goes in, instead of one item per press. Covers every
    /// <c>Smelter</c>: charcoal kiln, smelter, blast furnace, spinning wheel, windmill, eitr
    /// refinery. Plain Use is untouched and stays exactly vanilla - one item, one message.
    ///
    /// HOW IT IS BUILT, AND WHY THIS SHAPE
    /// -----------------------------------
    /// Vanilla's <c>Smelter.OnAddOre</c> / <c>OnAddFuel</c> (1.0 decompile, Smelter.cs:201 and 341)
    /// are each "check capacity, check you have one, <c>RemoveItem(item, 1)</c>,
    /// <c>InvokeRPC(RPC_AddOre/RPC_AddFuel)</c>". We do not reimplement any of that. We POSTFIX
    /// them, and only when <c>__result</c> is true - i.e. vanilla (or another mod's prefix) has
    /// already decided this press was legal and has already put the first item in - and then repeat
    /// exactly that RemoveItem + InvokeRPC pair for the rest of the burst. Anything else patching
    /// the same two methods still composes: we never skip an original and never change an argument.
    ///
    /// CAPACITY IS COUNTED LOCALLY, NOT READ BACK
    /// ------------------------------------------
    /// <c>GetQueueSize()</c> and <c>GetFuel()</c> read the station's ZDO. On a client that does not
    /// own it, the RPC has to reach the owner and the ZDO has to come back before either number
    /// moves, which is several frames away - so a loop that re-reads them would happily send twenty
    /// RPCs into a ten-slot smelter and the owner would silently drop the surplus. So the free
    /// capacity is measured ONCE, in our prefix, BEFORE vanilla's own insert, and the burst counts
    /// down against that local number. See <see cref="Plan"/>, which is pure and self-tested.
    ///
    /// HOW IT COMPOSES WITH CraftFromChests, AND WHY WE RUN FIRST (0.10.1)
    /// -------------------------------------------------------------------
    /// That module prefixes the same two methods and takes the press over - returning false - in
    /// exactly one case: the player has NONE of the item and a nearby container does. Harmony skips
    /// the remaining prefixes once one returns false, so whoever runs second loses.
    ///
    /// It registers its two prefixes at Harmony's DEFAULT priority, with nothing to reorder them:
    ///     Harmony.Patch(m, prefix: M(nameof(SmelterAddOrePre)));      // CraftFromChestsModule.cs:342
    ///     Harmony.Patch(m, prefix: M(nameof(SmelterAddFuelPre)));     // CraftFromChestsModule.cs:346
    /// where that module's own helper is
    ///     private static HarmonyMethod M(string name)                 // CraftFromChestsModule.cs:293
    ///     { return new HarmonyMethod(typeof(CraftFromChestsModule), name); }
    /// - no <c>priority</c>, no <c>before</c>/<c>after</c>, and no <c>[HarmonyPriority]</c> on
    /// either method, so both sit at <c>Priority.Normal</c> (400). Ours are registered with
    /// <c>priority = Priority.High</c> (600) and Harmony sorts prefixes by priority DESCENDING, so
    /// ours always runs first. That is safe precisely because our prefix never returns false and
    /// never touches an argument: it only measures.
    ///
    /// Before 0.10.1 we ran second, so a chest-fed press skipped us, <c>__state</c> arrived null and
    /// the postfix had to rebuild the capacity from a <c>GetQueueSize()</c>/<c>GetFuel()</c> that may
    /// or may not have caught up with the insert that just happened - which it did "one slot more
    /// conservatively", and that is why Shift+E filled a 20-slot station to 19. Running first
    /// removes the guess: the capacity is captured before ANY insert, every time, so the count is
    /// exact on both paths and the conservative fallback is gone.
    ///
    /// Within a burst the bag is spent first and the shortfall comes out of nearby containers
    /// through <see cref="ChestSource"/> - the same API, the same exclusions, the same LeaveOne
    /// rule, the same ownership-claiming consume - but only while CraftFromChests is itself live
    /// and allowed to feed smelters (<see cref="CraftFromChestsModule.SmelterPullLive"/>). Turn that
    /// module off, or <c>[Chests] PullForSmelters</c> off, and Shift+E fills from the bag alone.
    ///
    /// NOTHING IS EVER REMOVED THAT IS NOT SENT
    /// ----------------------------------------
    /// Every bag removal is MEASURED (count before minus count after) and the RPC is only sent when
    /// the count really dropped; every container removal goes through <c>ChestSource.Consume</c>,
    /// which returns how many actually left, and we send exactly that many RPCs. An item can
    /// therefore never be eaten without arriving, and a stack can never arrive twice.
    ///
    /// THE MODIFIER STAYS CONFIG-ONLY
    /// ------------------------------
    /// It is not declared through <see cref="NvlbKeys"/>, for the reason already written into
    /// <c>DualPowersModule</c> and <c>CraftFromChestsModule</c>: Valheim's Keyboard &amp; Mouse page
    /// binds one key per action and has no notion of a key you HOLD while pressing another. A bare
    /// LeftShift registered there would also collide with vanilla's own run/sneak button. So the
    /// modifier half is ours, read straight from <c>[StackInsert] Modifier</c>, exactly like
    /// <c>[Powers] SecondSlotModifier</c> and the <c>LeftAlt</c> half of <c>[Chests] ToggleKey</c>.
    /// </summary>
    internal sealed class StackInsertModule : FeatureModule
    {
        public override string Name => "StackInsert";
        public override ModuleSide Side => ModuleSide.Client;
        public override string Section => "StackInsert";
        public override string Theme => "Building & gathering";
        public override string Hint => "Hold the modifier at a smelter to insert a whole stack";

        private static StackInsertModule _self;

        private static ConfigEntry<string> _modifier;
        private static ConfigEntry<int> _maxPerPress;
        private static ConfigEntry<bool> _selfTest;

        /// <summary>Parsed <c>Modifier</c>. None = the feature is off without disabling the module.</summary>
        private static KeyCode _mod = KeyCode.LeftShift;

        private static bool _ranSelfTest;
        private static int _hoverErrors;

        /// <summary>Patches installed, config on, we are a client, and there is a player to ask.</summary>
        private static bool Live()
        {
            return _self != null && _self.Active && ClientActive() && Player.m_localPlayer != null;
        }

        // ---- config ---------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _modifier = BindLocal("Modifier", "LeftShift",
                "MACHINE-LOCAL. The key you HOLD while using a smelter's ore or fuel switch to put " +
                "the whole stack in instead of one item. A Unity KeyCode name - LeftShift, " +
                "LeftControl, LeftAlt, CapsLock - or None to switch the whole thing off without " +
                "disabling the module. Left/Right twins count as the same key, so LeftShift also " +
                "answers to right shift. Not on Valheim's Keyboard & Mouse page: that page binds " +
                "one key per action and knows nothing about a key you hold.",
                Opt.T("Key held with Use to insert a whole stack"));

            _maxPerPress = BindSynced("MaxPerPress", 0,
                "Most items one press may insert, counting vanilla's own first one. 0 = no limit " +
                "(fill it). 1 = exactly vanilla, whether or not the modifier is held. Server-synced, " +
                "so it is a knob for the server owner rather than the player.",
                Opt.N("Most items one press may insert, 0 for no limit", 0, 100, 1));

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic. Once per world load, log a unit run of the capacity arithmetic (how " +
                "much of a burst comes out of the bag, how much out of nearby containers, and where " +
                "the caps bite). Pure maths against numbers this module makes up - it touches no " +
                "station, no inventory and no ZDO. Machine-local, never synced.",
                Opt.B("Log a one-time check of the stack-insert maths"));

            Push();
        }

        private static void Push()
        {
            _mod = ParseKey(_modifier.Value, KeyCode.LeftShift, "Modifier");
        }

        private static KeyCode ParseKey(string s, KeyCode fallback, string what)
        {
            if (string.IsNullOrEmpty(s)) return KeyCode.None;
            var t = s.Trim();
            if (t.Length == 0) return KeyCode.None;
            try { return (KeyCode)Enum.Parse(typeof(KeyCode), t, true); }
            catch
            {
                Log.LogWarning("[StackInsert] " + what + " = '" + s + "' is not a UnityEngine.KeyCode " +
                               "name, falling back to " + fallback + ".");
                return fallback;
            }
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            Push();
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        public override string ValidateValue(SettingInfo info, string raw)
        {
            if (info == null || info.Key != "Modifier") return null;
            if (string.IsNullOrEmpty(raw)) return null;
            var t = raw.Trim();
            if (t.Length == 0 || string.Equals(t, "None", StringComparison.OrdinalIgnoreCase)) return null;
            try { Enum.Parse(typeof(KeyCode), t, true); return null; }
            catch { return "'" + raw + "' is not a Unity KeyCode name (try LeftShift, LeftControl, LeftAlt or None)."; }
        }

        // ---- patches --------------------------------------------------------------------------

        private static HarmonyMethod M(string name)
        {
            return new HarmonyMethod(typeof(StackInsertModule), name);
        }

        /// <summary>
        /// A prefix that must run before every other prefix on the same method - see the ordering
        /// argument in the class comment. <c>Priority.High</c> is 600 against the default 400, and
        /// Harmony runs prefixes in descending priority order.
        /// </summary>
        private static HarmonyMethod First(string name)
        {
            return new HarmonyMethod(typeof(StackInsertModule), name) { priority = Priority.High };
        }

        private void Need(ref System.Reflection.MethodInfo slot, Type t, string name, Type[] args, string label)
        {
            slot = args == null ? AccessTools.DeclaredMethod(t, name) : AccessTools.DeclaredMethod(t, name, args);
            if (slot == null) throw new Exception(label + " not found");
        }

        protected override void ApplyPatches()
        {
            System.Reflection.MethodInfo m = null;

            Need(ref m, typeof(Smelter), "OnAddOre",
                 new[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData) }, "Smelter.OnAddOre");
            Harmony.Patch(m, prefix: First(nameof(AddOrePre)), postfix: M(nameof(AddOrePost)));

            Need(ref m, typeof(Smelter), "OnAddFuel",
                 new[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData) }, "Smelter.OnAddFuel");
            Harmony.Patch(m, prefix: First(nameof(AddFuelPre)), postfix: M(nameof(AddFuelPost)));

            // The two hover callbacks are PRIVATE instance methods assigned as Switch.TooltipCallback
            // delegates in Smelter.Awake (Smelter.cs:100 / 106). A delegate calls the method, and the
            // method is what Harmony patches, so patching them works exactly like any other method.
            Need(ref m, typeof(Smelter), "OnHoverAddOre", new Type[0], "Smelter.OnHoverAddOre");
            Harmony.Patch(m, postfix: M(nameof(HoverOrePost)));

            Need(ref m, typeof(Smelter), "OnHoverAddFuel", new Type[0], "Smelter.OnHoverAddFuel");
            Harmony.Patch(m, postfix: M(nameof(HoverFuelPost)));

            Need(ref m, typeof(ZoneSystem), "Start", null, "ZoneSystem.Start()");
            Harmony.Patch(m, postfix: M(nameof(WorldReady)));

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        // ---- the modifier ---------------------------------------------------------------------

        /// <summary>Is the modifier down? Left/Right twins count as the same key, as in DualPowers.</summary>
        private static bool ModifierHeld()
        {
            KeyCode k = _mod;
            if (k == KeyCode.None) return false;
            try
            {
                if (ZInput.GetKey(k, false)) return true;
                KeyCode twin = k == KeyCode.LeftShift ? KeyCode.RightShift
                             : k == KeyCode.RightShift ? KeyCode.LeftShift
                             : k == KeyCode.LeftControl ? KeyCode.RightControl
                             : k == KeyCode.RightControl ? KeyCode.LeftControl
                             : k == KeyCode.LeftAlt ? KeyCode.RightAlt
                             : k == KeyCode.RightAlt ? KeyCode.LeftAlt
                             : KeyCode.None;
                return twin != KeyCode.None && ZInput.GetKey(twin, false);
            }
            catch { return false; }
        }

        /// <summary>"LeftShift" -&gt; "Shift", "LeftControl" -&gt; "Ctrl". "" when unbound.</summary>
        private static string ModifierLabel()
        {
            if (_mod == KeyCode.None) return "";
            string s = _mod.ToString();
            if (s.StartsWith("Left", StringComparison.Ordinal)) s = s.Substring(4);
            else if (s.StartsWith("Right", StringComparison.Ordinal)) s = s.Substring(5);
            if (s == "Control") s = "Ctrl";
            return s;
        }

        /// <summary>The press we might extend: module live, modifier held, local player at the switch.</summary>
        private static bool BurstWanted(Humanoid user)
        {
            if (!Live()) return false;
            if (user == null || user != (Humanoid)Player.m_localPlayer) return false;
            if (_maxPerPress != null && _maxPerPress.Value == 1) return false;   // "exactly vanilla"
            return ModifierHeld();
        }

        // ---- the pure arithmetic (self-tested; no game state anywhere in here) -----------------

        /// <summary>
        /// How the rest of a burst is paid for, after vanilla's own first item has gone in.
        ///
        /// <paramref name="freeBefore"/> is how many MORE items the station could take at the moment
        /// the press arrived, vanilla's first one included - so the budget for us is one less than
        /// that. <paramref name="maxPerPress"/> counts vanilla's item too (0 = no limit), so 1 means
        /// "exactly vanilla" and 5 means "four more after vanilla's".
        ///
        /// The bag is always spent before the containers, and neither is ever over-drawn: the two
        /// outputs sum to at most the budget, and each is at most what that source holds.
        /// </summary>
        internal static void Plan(int freeBefore, int bagCount, int chestCount, int maxPerPress,
                                  out int fromBag, out int fromChest)
        {
            fromBag = 0;
            fromChest = 0;

            int budget = freeBefore - 1;
            if (budget <= 0) return;
            if (maxPerPress > 0)
            {
                int allowed = maxPerPress - 1;
                if (allowed < budget) budget = allowed;
                if (budget <= 0) return;
            }

            if (bagCount > 0) fromBag = Math.Min(budget, bagCount);
            int left = budget - fromBag;
            if (left > 0 && chestCount > 0) fromChest = Math.Min(left, chestCount);
        }

        // ---- ore ------------------------------------------------------------------------------

        /// <summary>
        /// What the postfix needs to know and can only learn BEFORE the insert: which item this
        /// press is about, and how much room the station had.
        ///
        /// <see cref="Free"/> is always filled - it is the whole reason the prefix runs first, and
        /// the one number that cannot be recovered afterwards. <see cref="Shared"/> may still be
        /// null for ore: when the bag holds none of the station's inputs there is nothing to name
        /// yet, and the item is whatever CraftFromChests then pulls out of a container, which the
        /// postfix works out from the containers themselves (<see cref="ResolveChestOre"/>).
        /// Null <c>__state</c> means we were skipped entirely and no burst happens.
        /// </summary>
        private sealed class Burst
        {
            public string Shared;       // m_shared.m_name - what the inventories are keyed on
            public string Prefab;       // what RPC_AddOre names; unused for fuel
            public bool Cheated;        // vanilla passes item.m_cheated straight through
            public int Free;            // items the station could still take, vanilla's own included
        }

        /// <summary>
        /// Ore capacity, exactly as vanilla gates it: <c>OnAddOre</c> refuses at
        /// <c>GetQueueSize() &gt;= m_maxOre</c> (Smelter.cs:216) and each accepted press queues one,
        /// so the number of legal adds from a queue of <paramref name="queued"/> is
        /// <c>m_maxOre - queued</c>. A charcoal kiln is a <c>Smelter</c> whose <c>m_maxFuel</c> is 0
        /// and whose <c>m_maxOre</c> is its wood capacity, so the kiln goes down THIS path, not the
        /// fuel one. Pure, so the self-test can hold it to the vanilla numbers.
        /// </summary>
        internal static int FreeOreSlots(int maxOre, int queued)
        {
            int free = maxOre - queued;
            return free < 0 ? 0 : free;
        }

        private static void AddOrePre(Smelter __instance, Humanoid user, ItemDrop.ItemData item,
                                      out Burst __state)
        {
            __state = null;
            if (!BurstWanted(user)) return;
            try
            {
                var inv = user.GetInventory();
                if (inv == null || __instance == null) return;

                // The capacity is captured unconditionally, BEFORE anyone inserts anything - ours is
                // the first prefix on this method (see the class comment), so this is the station's
                // true free room for this press whether vanilla, CraftFromChests or we fill it.
                var b = new Burst { Free = FreeOreSlots(__instance.m_maxOre, __instance.GetQueueSize()) };

                // Exactly what vanilla is about to use: the argument when the player picked an item,
                // otherwise the first conversion input they are carrying (Smelter.FindCookableItem).
                // Carrying none of them is not a failure - it is the chest-fed press, and the
                // postfix names the item once it knows the chests were used.
                var chosen = item ?? FindCookable(__instance, inv);
                if (chosen != null && chosen.m_dropPrefab != null && chosen.m_shared != null)
                {
                    b.Shared = chosen.m_shared.m_name;
                    b.Prefab = chosen.m_dropPrefab.name;
                    b.Cheated = chosen.m_cheated;
                }
                __state = b;
            }
            catch (Exception e)
            {
                __state = null;
                Log.LogWarning("[StackInsert] Smelter.OnAddOre prefix: " + e.Message);
            }
        }

        private static void AddOrePost(Smelter __instance, Humanoid user, ItemDrop.ItemData item,
                                       bool __result, Burst __state)
        {
            if (!__result) return;                      // vanilla refused - there is nothing to extend
            if (!BurstWanted(user)) return;

            try
            {
                var inv = user.GetInventory();
                var nview = __instance != null ? __instance.m_nview : null;
                if (inv == null || nview == null || !nview.IsValid()) return;

                var b = __state;
                if (b == null) { SkippedPrefix("ore"); return; }
                if (b.Shared == null && !ResolveChestOre(__instance, b)) return;

                Run(__instance, user, inv, nview, b, "ore", delegate(string prefab, bool cheated)
                {
                    // Vanilla's own call, argument for argument (Smelter.cs:225). 1.0 added the
                    // `cheated` bool; RPC_AddOre is registered as Register<string, bool>.
                    nview.InvokeRPC("RPC_AddOre", prefab, cheated);
                });
            }
            catch (Exception e)
            {
                Log.LogWarning("[StackInsert] Smelter.OnAddOre postfix: " + e.Message);
            }
        }

        /// <summary>Smelter.FindCookableItem, re-walked: the first conversion input in the bag.</summary>
        private static ItemDrop.ItemData FindCookable(Smelter s, Inventory inv)
        {
            if (s == null || s.m_conversion == null) return null;
            foreach (var conv in s.m_conversion)
            {
                if (conv == null || conv.m_from == null) continue;
                var found = inv.GetItem(conv.m_from.m_itemData.m_shared.m_name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// The bag held none of the station's inputs when the press arrived, yet the press
        /// succeeded - so CraftFromChests' <c>SmelterAddOrePre</c> fed it from a container. Only the
        /// ITEM is unknown; the capacity is already in <paramref name="b"/>, measured before the
        /// insert. Reproduce that module's choice exactly: the first conversion, in the station's own
        /// order, not on the excluded-items list, that a nearby container can still supply. Returns
        /// false when we cannot say which item went in, in which case the burst simply does not
        /// happen - we never guess an item and never guess a capacity. (There is no need to re-check
        /// that the bag is empty: <c>b.Shared</c> is null precisely because <c>FindCookable</c> found
        /// nothing in it at prefix time, which is that module's own entry condition.)
        /// </summary>
        private static bool ResolveChestOre(Smelter s, Burst b)
        {
            if (s == null || s.m_conversion == null || b == null) return false;
            if (!CraftFromChestsModule.SmelterPullLive) return false;

            var boxes = ChestSource.Nearby(s.transform.position);
            if (boxes.Count == 0) return false;

            foreach (var conv in s.m_conversion)
            {
                if (conv == null || conv.m_from == null) continue;
                string shared = conv.m_from.m_itemData.m_shared.m_name;
                string prefab = Utils.GetPrefabName(conv.m_from.gameObject);
                if (ChestSource.ItemBlocked(prefab, shared)) continue;
                if (ChestSource.Count(shared, boxes) <= 0) continue;
                b.Shared = shared;
                b.Prefab = prefab;
                b.Cheated = false;      // nothing out of a chest is a debug-spawned item
                return true;
            }
            return false;
        }

        /// <summary>
        /// Something with a priority above ours returned false and skipped our prefix, so the free
        /// capacity was never measured. We will NOT guess it: over-guessing by one sends an RPC the
        /// owner silently drops, which eats an item. Log it once per session per switch and leave
        /// the press exactly as vanilla (plus whoever handled it) left it.
        /// </summary>
        private static void SkippedPrefix(string what)
        {
            if (what == "ore") { if (_skipLoggedOre) return; _skipLoggedOre = true; }
            else { if (_skipLoggedFuel) return; _skipLoggedFuel = true; }
            Log.LogWarning("[StackInsert] another mod's " + what + " prefix skipped ours, so the " +
                           "station's free capacity could not be measured - that press stays a " +
                           "single item rather than risk overfilling. (Ours runs at Priority.High; " +
                           "only a prefix above that can get in front of it.)");
        }

        private static bool _skipLoggedOre;
        private static bool _skipLoggedFuel;

        // ---- fuel -----------------------------------------------------------------------------

        private static void AddFuelPre(Smelter __instance, Humanoid user, ItemDrop.ItemData item,
                                       out Burst __state)
        {
            __state = null;
            if (!BurstWanted(user)) return;
            try
            {
                if (__instance == null || __instance.m_fuelItem == null) return;
                string shared = __instance.m_fuelItem.m_itemData.m_shared.m_name;
                if (item != null && item.m_shared.m_name != shared) return;   // vanilla says "$msg_wrongitem"

                __state = new Burst
                {
                    Shared = shared,
                    Prefab = Utils.GetPrefabName(__instance.m_fuelItem.gameObject),
                    Cheated = false,
                    Free = FreeFuel(__instance)
                };
            }
            catch (Exception e)
            {
                __state = null;
                Log.LogWarning("[StackInsert] Smelter.OnAddFuel prefix: " + e.Message);
            }
        }

        private static void AddFuelPost(Smelter __instance, Humanoid user, ItemDrop.ItemData item,
                                        bool __result, Burst __state)
        {
            if (!__result) return;
            if (!BurstWanted(user)) return;

            try
            {
                var inv = user.GetInventory();
                var nview = __instance != null ? __instance.m_nview : null;
                if (inv == null || nview == null || !nview.IsValid()) return;
                if (__instance.m_fuelItem == null) return;

                // A station has exactly one fuel, so the fuel prefix always knows the item and
                // always fills __state - unless it was skipped outright, and then we do not guess.
                var b = __state;
                if (b == null) { SkippedPrefix("fuel"); return; }

                Run(__instance, user, inv, nview, b, "fuel", delegate(string prefab, bool cheated)
                {
                    nview.InvokeRPC("RPC_AddFuel");
                });
            }
            catch (Exception e)
            {
                Log.LogWarning("[StackInsert] Smelter.OnAddFuel postfix: " + e.Message);
            }
        }

        /// <summary>
        /// How many more times fuel may be added. Vanilla refuses at
        /// <c>GetFuel() &gt; (float)(m_maxFuel - 1)</c> (Smelter.cs:345) and each accepted add is
        /// <c>SetFuel(fuel + 1f)</c> (Smelter.cs:357), so from fuel f the k'th add needs
        /// <c>f + k - 1 &lt;= m_maxFuel - 1</c>, i.e. <c>k &lt;= m_maxFuel - f</c>, i.e.
        /// <c>floor(m_maxFuel - f)</c> legal adds - 0 exactly when vanilla would have said
        /// "$msg_itsfull", including on the charcoal kiln, whose <c>m_maxFuel</c> is 0 (a kiln takes
        /// its wood through the ORE switch instead, see <see cref="FreeOreSlots"/>). Fuel is a float
        /// that burns down continuously, so flooring is also the conservative answer for a
        /// fractional value. Pure, so the self-test can hold it to the vanilla numbers.
        /// </summary>
        internal static int FreeFuelSlots(int maxFuel, float fuel)
        {
            int free = Mathf.FloorToInt(maxFuel - fuel);
            return free < 0 ? 0 : free;
        }

        private static int FreeFuel(Smelter s)
        {
            return FreeFuelSlots(s.m_maxFuel, s.GetFuel());
        }

        // ---- the burst itself ------------------------------------------------------------------

        /// <summary>
        /// Insert the rest of the burst: bag first, containers for the shortfall, one HUD message
        /// and one log line for the lot. Every removal is measured, and an RPC is sent only for an
        /// item that actually left an inventory.
        /// </summary>
        private static void Run(Smelter s, Humanoid user, Inventory inv, ZNetView nview, Burst b,
                                string what, Action<string, bool> send)
        {
            int bagHas = inv.CountItems(b.Shared);

            bool chestsOn = CraftFromChestsModule.SmelterPullLive &&
                            !ChestSource.ItemBlocked(b.Prefab, b.Shared);
            List<Box> boxes = null;
            int chestHas = 0;
            if (chestsOn)
            {
                boxes = ChestSource.Nearby(s.transform.position);
                chestHas = ChestSource.Count(b.Shared, boxes);
            }

            int wantBag, wantChest;
            Plan(b.Free, bagHas, chestHas, _maxPerPress != null ? _maxPerPress.Value : 0,
                 out wantBag, out wantChest);
            if (wantBag <= 0 && wantChest <= 0) return;

            int fromBag = 0;
            for (int i = 0; i < wantBag; i++)
            {
                var stack = inv.GetItem(b.Shared);
                if (stack == null) break;
                int before = inv.CountItems(b.Shared);
                inv.RemoveItem(stack, 1);
                if (inv.CountItems(b.Shared) >= before) break;   // nothing left the bag - send nothing
                send(b.Prefab, b.Cheated);
                fromBag++;
            }

            int fromChest = 0;
            if (wantChest > 0 && boxes != null)
            {
                fromChest = ChestSource.Consume(b.Shared, wantChest, -1, boxes);
                for (int i = 0; i < fromChest; i++) send(b.Prefab, false);
            }

            int extra = fromBag + fromChest;
            if (extra <= 0) return;

            int total = extra + 1;                                // vanilla's own item makes the first
            try
            {
                // Vanilla's own string with a count on the end - MessageHud localizes both tokens.
                // One message for the burst, not one per item.
                user.Message(MessageHud.MessageType.Center, "$msg_added " + b.Shared + " x" + total);
            }
            catch { /* no HUD (a headless host, a menu): the items still went in */ }

            Log.LogInfo("[StackInsert] " + what + " " + b.Shared + " x" + total + " into " +
                        (s.m_name ?? "smelter") + ": vanilla 1 + bag " + fromBag + " + chests " + fromChest +
                        " (bag held " + bagHas + ", chests held " + chestHas + ", free " + b.Free +
                        ", room left " + (b.Free - total) + ", MaxPerPress=" +
                        (_maxPerPress != null ? _maxPerPress.Value : 0) + ")");
        }

        // ---- hover text --------------------------------------------------------------------------

        private static void HoverOrePost(Smelter __instance, ref string __result)
        {
            Append(__instance, ref __result);
        }

        private static void HoverFuelPost(Smelter __instance, ref string __result)
        {
            Append(__instance, ref __result);
        }

        /// <summary>
        /// One extra line under vanilla's own "[E] Add ore", in vanilla's own shape - the yellow bold
        /// bracketed key, then the action. Nothing is anchored on or rewritten: we only append, so a
        /// game patch that changes the line above cannot be mangled by us.
        /// </summary>
        private static void Append(Smelter s, ref string text)
        {
            if (!Live() || _mod == KeyCode.None) return;
            if (_maxPerPress != null && _maxPerPress.Value == 1) return;   // capped to vanilla: no hint
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var loc = Localization.instance;
                if (loc == null) return;
                text = text + "\n" + loc.Localize("[<color=yellow><b>" + ModifierLabel() +
                                                  " + $KEY_Use</b></color>] Add stack");
            }
            catch (Exception e)
            {
                // A hover postfix must never take the tooltip down with it.
                if (_hoverErrors++ < 3)
                    Log.LogWarning("[StackInsert] hover text postfix failed, leaving vanilla text: " + e.Message);
            }
        }

        // ---- reporting ----------------------------------------------------------------------------

        internal static string Numbers()
        {
            var sb = new StringBuilder();
            sb.Append("modifier=").Append(_modifier != null ? _modifier.Value : "?")
              .Append(" (parsed ").Append(_mod == KeyCode.None ? "None - stack insert is off" : _mod.ToString())
              .Append("), maxPerPress=").Append(_maxPerPress != null ? _maxPerPress.Value : 0)
              .Append(_maxPerPress != null && _maxPerPress.Value == 0 ? " (no limit)" : "")
              .Append(", chests=").Append(CraftFromChestsModule.SmelterPullLive ? "available" : "off")
              .Append(", selfTest=").Append(_selfTest != null && _selfTest.Value);
            return sb.ToString();
        }

        public override string StatusDetail()
        {
            return Numbers();
        }

        // ---- headless self-test: the arithmetic, with no world around it ---------------------------

        private static void WorldReady()
        {
            if (_ranSelfTest || _selfTest == null || !_selfTest.Value) return;
            _ranSelfTest = true;
            try
            {
                foreach (var line in SelfTest().Split('\n')) Log.LogInfo(line);
            }
            catch (Exception e)
            {
                Log.LogError("[StackInsert] self-test threw: " + e);
            }
        }

        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            Action<string, int, int, int, int, int, int> check =
                delegate (string label, int free, int bag, int chest, int cap, int wantBag, int wantChest)
                {
                    int gotBag, gotChest;
                    Plan(free, bag, chest, cap, out gotBag, out gotChest);
                    bool ok = gotBag == wantBag && gotChest == wantChest;
                    if (ok) pass++; else fail++;
                    sb.Append("\n  ").Append(ok ? "ok   " : "FAIL ").Append(label)
                      .Append(": free=").Append(free).Append(" bag=").Append(bag)
                      .Append(" chest=").Append(chest).Append(" cap=").Append(cap)
                      .Append(" -> bag ").Append(gotBag).Append(" + chest ").Append(gotChest)
                      .Append(ok ? "" : " (expected bag " + wantBag + " + chest " + wantChest + ")");
                };

            sb.Append("[SelfTest][StackInsert] --- begin ---");
            sb.Append("\n  ").Append(Numbers());

            //      label                        free bag chest cap  wantBag wantChest
            check("empty smelter, full stack",     10, 50,    0,  0,       9,        0);
            check("bag exactly fills it",          10,  9,    0,  0,       9,        0);
            check("bag one short, no chests",      10,  8,    0,  0,       8,        0);
            check("bag short, chests finish",      10,  3,  100,  0,       3,        6);
            check("chests alone",                  10,  0,  100,  0,       0,        9);
            check("nothing anywhere",              10,  0,    0,  0,       0,        0);
            check("one slot left (vanilla took it)", 1, 50,  50,  0,       0,        0);
            check("already full",                   0, 50,   50,  0,       0,        0);
            check("negative free is not a burst",  -3, 50,   50,  0,       0,        0);
            check("cap 1 is exactly vanilla",      10, 50,   50,  1,       0,        0);
            check("cap 5 = four more",             10, 50,   50,  5,       4,        0);
            check("cap above capacity never wins", 10, 50,   50, 99,       9,        0);
            check("cap splits across sources",     10,  2,   50,  5,       2,        2);
            check("bag alone under the cap",       10,  1,    0,  5,       1,        0);
            check("kiln fuel: no room at all",      0,  9,    9,  0,       0,        0);
            check("two slots free",                 2, 99,   99,  0,       1,        0);

            // The capacity formulas themselves, against vanilla's own gates (Smelter.cs:216 for ore,
            // :345 + :357 for fuel). These are what the 0.10.1 ordering fix made exact: before it a
            // chest-fed press reconstructed them one slot short and filled a 20 to 19.
            Action<string, int, int> cap =
                delegate (string label, int got, int want)
                {
                    bool ok = got == want;
                    if (ok) pass++; else fail++;
                    sb.Append("\n  ").Append(ok ? "ok   " : "FAIL ").Append(label)
                      .Append(" -> ").Append(got).Append(ok ? "" : " (expected " + want + ")");
                };

            cap("ore: empty smelter m_maxOre=10", FreeOreSlots(10, 0), 10);
            cap("ore: 7 queued of 10",            FreeOreSlots(10, 7),  3);
            cap("ore: full",                      FreeOreSlots(10, 10), 0);
            cap("ore: over-full never negative",  FreeOreSlots(10, 12), 0);
            cap("ore: kiln m_maxOre=25 empty",    FreeOreSlots(25, 0), 25);
            cap("fuel: empty smelter maxFuel=20", FreeFuelSlots(20, 0f), 20);
            cap("fuel: one coal in",              FreeFuelSlots(20, 1f), 19);
            cap("fuel: half-burnt coal",          FreeFuelSlots(20, 1.4f), 18);
            cap("fuel: one short of full",        FreeFuelSlots(20, 19f), 1);
            cap("fuel: full",                     FreeFuelSlots(20, 20f), 0);
            cap("fuel: kiln maxFuel=0 (ore path)", FreeFuelSlots(0, 0f), 0);

            sb.Append("\n  ").Append(pass).Append(" passed, ").Append(fail).Append(" failed");
            sb.Append("\n[SelfTest][StackInsert] --- end ---");
            return sb.ToString();
        }
    }
}
