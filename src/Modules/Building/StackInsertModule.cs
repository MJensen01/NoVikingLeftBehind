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
    /// HOW IT COMPOSES WITH CraftFromChests
    /// ------------------------------------
    /// That module prefixes the same two methods (<c>SmelterAddOrePre</c> / <c>SmelterAddFuelPre</c>)
    /// and takes the press over - returning false - in exactly one case: the player has NONE of the
    /// item and a nearby container does. Harmony skips the remaining prefixes when one returns
    /// false, so in that case OUR prefix never runs and <c>__state</c> arrives null; the postfix
    /// recognises that and reconstructs both the item chosen and the capacity (one slot more
    /// conservatively, since it can no longer see the pre-insert queue). In every other case our
    /// prefix ran and the numbers are exact.
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
            Harmony.Patch(m, prefix: M(nameof(AddOrePre)), postfix: M(nameof(AddOrePost)));

            Need(ref m, typeof(Smelter), "OnAddFuel",
                 new[] { typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData) }, "Smelter.OnAddFuel");
            Harmony.Patch(m, prefix: M(nameof(AddFuelPre)), postfix: M(nameof(AddFuelPost)));

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
        /// press is about, and how much room the station had. Null when this is not a burst, or
        /// when another prefix took the press over and skipped us.
        /// </summary>
        private sealed class Burst
        {
            public string Shared;       // m_shared.m_name - what the inventories are keyed on
            public string Prefab;       // what RPC_AddOre names; unused for fuel
            public bool Cheated;        // vanilla passes item.m_cheated straight through
            public int Free;            // items the station could still take, vanilla's own included
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

                // Exactly what vanilla is about to use: the argument when the player picked an item,
                // otherwise the first conversion input they are carrying (Smelter.FindCookableItem).
                var chosen = item ?? FindCookable(__instance, inv);
                if (chosen == null || chosen.m_dropPrefab == null || chosen.m_shared == null) return;

                __state = new Burst
                {
                    Shared = chosen.m_shared.m_name,
                    Prefab = chosen.m_dropPrefab.name,
                    Cheated = chosen.m_cheated,
                    Free = __instance.m_maxOre - __instance.GetQueueSize()
                };
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

                var b = __state ?? AfterChestInsert(__instance, inv);
                if (b == null) return;

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
        /// Our prefix did not run, which (given <c>__result</c> is true) means another prefix
        /// returned false and handled the press itself. In this build that is CraftFromChests'
        /// <c>SmelterAddOrePre</c>, whose one branch is "the player has none of any conversion input
        /// and a container could give one". Reproduce its choice - first conversion, in the
        /// station's own order, not on the excluded-items list, that containers can still supply -
        /// and be one slot more conservative about capacity, because <c>GetQueueSize()</c> may or
        /// may not have caught up with the insert that just happened (it has on the owner, it has
        /// not on anyone else). Returns null when nothing else patched us, or when we cannot say
        /// which item went in - in which case the burst simply does not happen.
        /// </summary>
        private static Burst AfterChestInsert(Smelter s, Inventory inv)
        {
            if (s == null || s.m_conversion == null) return null;
            if (!CraftFromChestsModule.SmelterPullLive) return null;

            // The branch we are reconstructing only fires when the bag is empty of every input.
            foreach (var conv in s.m_conversion)
            {
                if (conv == null || conv.m_from == null) continue;
                if (inv.HaveItem(conv.m_from.m_itemData.m_shared.m_name)) return null;
            }

            var boxes = ChestSource.Nearby(s.transform.position);
            if (boxes.Count == 0) return null;

            foreach (var conv in s.m_conversion)
            {
                if (conv == null || conv.m_from == null) continue;
                string shared = conv.m_from.m_itemData.m_shared.m_name;
                string prefab = Utils.GetPrefabName(conv.m_from.gameObject);
                if (ChestSource.ItemBlocked(prefab, shared)) continue;
                if (ChestSource.Count(shared, boxes) <= 0) continue;
                return new Burst
                {
                    Shared = shared,
                    Prefab = prefab,
                    Cheated = false,
                    Free = s.m_maxOre - s.GetQueueSize() - 1
                };
            }
            return null;
        }

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

                var b = __state;
                if (b == null)
                {
                    // CraftFromChests fed the fire from a container and skipped our prefix. The item
                    // is never in doubt here (a station has exactly one fuel), only the capacity.
                    if (!CraftFromChestsModule.SmelterPullLive) return;
                    b = new Burst
                    {
                        Shared = __instance.m_fuelItem.m_itemData.m_shared.m_name,
                        Prefab = Utils.GetPrefabName(__instance.m_fuelItem.gameObject),
                        Cheated = false,
                        Free = FreeFuel(__instance) - 1
                    };
                }

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
        /// How many more times fuel may be added. Vanilla refuses at <c>GetFuel() &gt; m_maxFuel - 1</c>
        /// and each add is +1, so the count of legal adds from fuel f is <c>floor(m_maxFuel - f)</c>,
        /// which is 0 exactly when vanilla would have said "$msg_itsfull" - including on the charcoal
        /// kiln, whose <c>m_maxFuel</c> is 0. Fuel burns down continuously, so flooring is also the
        /// conservative answer for a fractional value.
        /// </summary>
        private static int FreeFuel(Smelter s)
        {
            int free = Mathf.FloorToInt(s.m_maxFuel - s.GetFuel());
            return free < 0 ? 0 : free;
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

            sb.Append("\n  ").Append(pass).Append(" passed, ").Append(fail).Append(" failed");
            sb.Append("\n[SelfTest][StackInsert] --- end ---");
            return sb.ToString();
        }
    }
}
