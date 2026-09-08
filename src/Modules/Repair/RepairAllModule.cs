using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Repair every item the station in front of you is allowed to repair, in one go, instead of
    /// one hammer-button click per damaged item.
    ///
    /// Nothing about vanilla's RULES changes. Repairs are still free, a forge still cannot repair
    /// a workbench item, and a station whose level is below the recipe's m_minStationLevel still
    /// refuses - because eligibility is decided by calling vanilla's OWN private
    /// InventoryGui.CanRepair(ItemDrop.ItemData) (visible to us at compile time through the
    /// assembly publicizer, see src/Directory.Build.props), not by a re-implementation that could
    /// drift from it. The candidate list is vanilla's own Inventory.GetWornItems() too - "uses
    /// durability AND is below max durability" - and each item is healed exactly the way
    /// InventoryGui.RepairOneItem() heals it: RaiseSkill(Crafting, 1 - dur/maxDur) then
    /// m_durability = GetMaxDurability().
    ///
    /// Building pieces and the hammer are deliberately OUT of scope: this only ever touches items
    /// in the local player's own inventory, never anything standing in the world.
    ///
    /// Patch point - InventoryGui.UpdateRepair() postfix (verified in the 0.221.13 decompile):
    ///
    ///   * It is the one vanilla path that has already resolved the station
    ///     (Player.m_localPlayer.GetCurrentCraftingStation()) and already owns the decision
    ///     "is there anything here that can be repaired" - CanRepair/HaveRepairableItems are
    ///     called from nowhere else. InventoryGui.Show(Container) does NOT resolve a station.
    ///   * InventoryGui.Update() calls it every frame while the GUI is visible (inside the
    ///     `if (@bool)` block alongside UpdateInventory/UpdateRecipe), so one postfix covers both
    ///     "the station GUI just opened" and "the repair panel refreshed while it is open" with no
    ///     second hook, and it is also where the hotkey can be sampled once per frame.
    ///   * The button state vanilla just computed is corrected in place after a batch
    ///     (interactable = false, glow off) exactly as OnRepairPressed's follow-up UpdateRepair()
    ///     would have done, so the hammer doesn't pulse for a frame with nothing left to fix.
    ///
    /// Re-fire policy (Trigger=OnOpen/Both). A naive "repair whenever something is repairable"
    /// would fire EVERY FRAME for a player standing at a workbench with a lit torch equipped -
    /// Humanoid.DrainEquipedItemDurability() drains it continuously, so it is repairable again on
    /// the very next frame - which would machine-gun the repair sound. Instead the module arms
    /// once and disarms after a batch, and only re-arms on a real event:
    ///   - InventoryGui.Hide() (postfix)                       - the GUI closed,
    ///   - no current crafting station                         - you walked away,
    ///   - the player inventory's item COUNT changed           - you dragged something in/out
    ///                                                           (e.g. a damaged item out of a
    ///                                                           chest), which is the "items
    ///                                                           changed" case worth re-firing on
    ///                                                           and, unlike durability, does not
    ///                                                           tick every frame.
    /// A 0.25 s floor between batches is belt-and-braces on top of that.
    ///
    /// ExtraSlots interaction: NVLB's extra slots are real cells of the player's OWN Inventory
    /// (see Modules/Slots/), not a second container, so Inventory.GetWornItems() walks them like
    /// any other cell and gear parked in an equipment/utility/generic slot is repaired too, with
    /// no extra code. The same is true of the item-count signature above.
    ///
    /// Equipped items are repaired, exactly as vanilla repairs them - GetWornItems() makes no
    /// distinction and neither does this module.
    /// </summary>
    internal sealed class RepairAllModule : FeatureModule
    {
        public override string Name => "RepairAll";

        /// <summary>
        /// Normally Client - the repair GUI only exists on a player's game. [Repair] SelfTest
        /// (machine-local) flips this to Both so a dedicated server can prove the eligibility
        /// rules headlessly against its own ObjectDB.
        /// </summary>
        public override ModuleSide Side => _selfTest != null && _selfTest.Value ? ModuleSide.Both : ModuleSide.Client;

        public override string Section => "Repair";

        private enum TriggerMode { OnOpen, Hotkey, Both }

        private ConfigEntry<string> _trigger;
        private ConfigEntry<bool> _showMessage;
        private ConfigEntry<string> _hotkey;
        private ConfigEntry<bool> _selfTest;

        private static RepairAllModule _self;
        private static TriggerMode _mode = TriggerMode.OnOpen;
        private static KeyCode _key = KeyCode.R;

        // Re-fire state - see the class doc.
        private static bool _armed = true;
        private static int _lastItemCount = -1;
        private static float _lastBatchTime = -999f;

        // Own scratch list: vanilla's InventoryGui.m_tempWornItems is live during the same frame.
        private static readonly List<ItemDrop.ItemData> _worn = new List<ItemDrop.ItemData>();

        // One throw anywhere in a per-frame GUI path logs once and stops the module for the
        // session rather than spamming the log 60x a second.
        private static bool _broken;

        private static bool _selfTestRan;

        private static bool Live()
        {
            return !_broken && _self != null && _self.Active && ClientActive();
        }

        // ---- config ---------------------------------------------------------------------------

        protected override void Bind()
        {
            _self = this;

            _trigger = BindSynced("Trigger", "OnOpen",
                "When the batch repair happens. OnOpen = the moment a crafting station's GUI " +
                "opens, and again whenever the repair panel refreshes with something new to fix " +
                "while it is open (you dragged a damaged item out of a chest). Hotkey = only when " +
                "you press [Repair] Hotkey with the station GUI open. Both = either. " +
                "Values: OnOpen | Hotkey | Both.");

            _showMessage = BindSynced("ShowMessage", true,
                "Show one top-left message per batch (\"Repaired 7 items\"), using vanilla's own " +
                "$msg_repaired localisation. Never one message per item.");

            _hotkey = BindLocal("Hotkey", "R",
                "Local: key that repairs everything, while a crafting station's inventory GUI is " +
                "open. Unity KeyCode name, or None. Only used when Trigger is Hotkey or Both.");

            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic, local only, never synced. Logs every repairable item in ObjectDB " +
                "grouped by the station (and station level) vanilla would require to repair it, " +
                "so the eligibility rules can be checked without a client. Flips this module's " +
                "side to Both so a dedicated server runs it. Changes no game state.");

            ParseSettings();
        }

        public override void OnConfigChanged(ConfigEntryBase entry)
        {
            ParseSettings();
            // A trigger/hotkey change should take effect at the next station, not mid-batch.
            _armed = true;
            _lastItemCount = -1;
            if (Active) Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private void ParseSettings()
        {
            var raw = _trigger != null ? (_trigger.Value ?? "") : "";
            raw = raw.Trim();
            if (raw.Length == 0) _mode = TriggerMode.OnOpen;
            else
            {
                try { _mode = (TriggerMode)Enum.Parse(typeof(TriggerMode), raw, true); }
                catch
                {
                    Log.LogWarning("[" + Name + "] Trigger='" + raw +
                                   "' is not OnOpen/Hotkey/Both - falling back to OnOpen");
                    _mode = TriggerMode.OnOpen;
                }
            }

            var keyName = _hotkey != null ? (_hotkey.Value ?? "") : "";
            keyName = keyName.Trim();
            if (keyName.Length == 0) _key = KeyCode.None;
            else
            {
                try { _key = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true); }
                catch
                {
                    Log.LogWarning("[" + Name + "] Hotkey '" + keyName +
                                   "' is not a Unity KeyCode - the hotkey is off");
                    _key = KeyCode.None;
                }
            }
        }

        // ---- patches --------------------------------------------------------------------------

        protected override void ApplyPatches()
        {
            var updateRepair = AccessTools.Method(typeof(InventoryGui), "UpdateRepair", Type.EmptyTypes);
            if (updateRepair == null) throw new Exception("InventoryGui.UpdateRepair() not found");
            Harmony.Patch(updateRepair,
                postfix: new HarmonyMethod(typeof(RepairAllModule), nameof(UpdateRepairPostfix)));

            var hide = AccessTools.Method(typeof(InventoryGui), "Hide", Type.EmptyTypes);
            if (hide == null) throw new Exception("InventoryGui.Hide() not found");
            Harmony.Patch(hide,
                postfix: new HarmonyMethod(typeof(RepairAllModule), nameof(HidePostfix)));

            // Sanity: the three vanilla members the batch leans on must exist, and fail LOUDLY
            // (module summary shows FAILED(...)) rather than silently no-op at a workbench.
            if (AccessTools.Method(typeof(InventoryGui), "CanRepair", new[] { typeof(ItemDrop.ItemData) }) == null)
                throw new Exception("InventoryGui.CanRepair(ItemDrop.ItemData) not found");
            if (AccessTools.Method(typeof(Inventory), "GetWornItems", new[] { typeof(List<ItemDrop.ItemData>) }) == null)
                throw new Exception("Inventory.GetWornItems(List<ItemData>) not found");
            if (AccessTools.Field(typeof(CraftingStation), "m_repairItemDoneEffects") == null)
                throw new Exception("CraftingStation.m_repairItemDoneEffects not found");

            if (_selfTest.Value)
            {
                // Same "world is ready" point WorldSelfTest/EconomySelfTest use: by ZoneSystem.Start
                // the real ObjectDB (recipes included) is in place on both halves. ObjectDB's own
                // UpdateRegisters fires too early on a dedicated server (empty recipe list).
                var start = AccessTools.Method(typeof(ZoneSystem), "Start", Type.EmptyTypes);
                if (start == null) throw new Exception("ZoneSystem.Start() not found");
                Harmony.Patch(start,
                    postfix: new HarmonyMethod(typeof(RepairAllModule), nameof(WorldReadyPost)));
            }

            Log.LogInfo("[" + Name + "] " + Numbers());
        }

        private static void HidePostfix()
        {
            _armed = true;
            _lastItemCount = -1;
        }

        private static void UpdateRepairPostfix(InventoryGui __instance)
        {
            if (!Live() || __instance == null) return;

            try
            {
                Run(__instance);
            }
            catch (Exception e)
            {
                _broken = true;
                Log.LogError("[RepairAll] disabled for this session after an error in " +
                             "InventoryGui.UpdateRepair postfix: " + e);
            }
        }

        private static void Run(InventoryGui gui)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // Vanilla's own gate, mirrored: no station and no no-cost cheat = no repair panel.
            var station = player.GetCurrentCraftingStation();
            bool noCost = player.NoCostCheat();
            if (station == null && !noCost)
            {
                _armed = true;
                _lastItemCount = -1;
                return;
            }
            // A station you cannot currently use (no roof / too exposed / no fire) repairs
            // nothing in vanilla either. Silent - vanilla only messages on a button press.
            if (station != null && !station.CheckUsable(player, false)) return;

            var inv = player.GetInventory();
            if (inv == null) return;

            int count = inv.m_inventory != null ? inv.m_inventory.Count : 0;
            if (count != _lastItemCount)
            {
                _armed = true;
                _lastItemCount = count;
            }

            bool fire = false;
            if (_mode == TriggerMode.Hotkey || _mode == TriggerMode.Both)
            {
                if (HotkeyPressed()) fire = true;
            }
            if ((_mode == TriggerMode.OnOpen || _mode == TriggerMode.Both) && _armed) fire = true;
            if (!fire) return;

            if (Time.time - _lastBatchTime < 0.25f) return;   // never machine-gun the effect
            _armed = false;

            int repaired = RepairEverything(gui, player, inv, out string lastName);
            _lastBatchTime = Time.time;
            if (repaired <= 0) return;

            // One effect for the whole batch, from the station, exactly the EffectList
            // RepairOneItem() plays per item. No station (no-cost cheat) = no effect, as vanilla.
            if (station != null)
            {
                station.m_repairItemDoneEffects.Create(station.transform.position, Quaternion.identity);
            }

            if (_self != null && _self._showMessage.Value)
            {
                string what = repaired == 1 ? lastName : repaired + " items";
                player.Message(MessageHud.MessageType.TopLeft,
                    Localization.instance.Localize("$msg_repaired", what));
            }

            // What OnRepairPressed's follow-up UpdateRepair() would have left behind: nothing
            // repairable, so the hammer is dead and the glow is off. Vanilla recomputes both next
            // frame anyway; doing it here stops a one-frame pulse with nothing left to fix.
            if (gui.m_repairButton != null) gui.m_repairButton.interactable = false;
            if (gui.m_repairButtonGlow != null) gui.m_repairButtonGlow.gameObject.SetActive(false);

            Log.LogInfo("[RepairAll] repaired " + repaired + " item(s) at " +
                        (station != null ? station.m_name : "(no-cost cheat)"));
        }

        /// <summary>
        /// The batch. Candidate list and per-item healing are vanilla's, verbatim:
        /// Inventory.GetWornItems() + InventoryGui.CanRepair() + RaiseSkill/m_durability.
        /// </summary>
        private static int RepairEverything(InventoryGui gui, Player player, Inventory inv, out string lastName)
        {
            lastName = null;
            _worn.Clear();
            inv.GetWornItems(_worn);
            if (_worn.Count == 0) return 0;

            int n = 0;
            for (int i = 0; i < _worn.Count; i++)
            {
                var item = _worn[i];
                if (item == null || item.m_shared == null) continue;
                if (!gui.CanRepair(item)) continue;

                float max = item.GetMaxDurability();
                player.RaiseSkill(Skills.SkillType.Crafting, 1f - item.m_durability / max);
                item.m_durability = max;
                lastName = item.m_shared.m_name;
                n++;
            }
            _worn.Clear();
            return n;
        }

        private static bool HotkeyPressed()
        {
            if (_key == KeyCode.None) return false;
            // UpdateRepair runs regardless of chat/console focus, so guard typing ourselves.
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;
            // global:: - `using System;` above would otherwise resolve System.Console here.
            if (global::Console.IsVisible()) return false;
            if (TextInput.IsVisible()) return false;
            return ZInput.GetKeyDown(_key, false);
        }

        // ---- reporting --------------------------------------------------------------------------

        private string Numbers()
        {
            return "Trigger=" + _mode +
                   " ShowMessage=" + (_showMessage != null && _showMessage.Value) +
                   " Hotkey=" + _key +
                   " SelfTest=" + (_selfTest != null && _selfTest.Value);
        }

        public override string StatusDetail() { return Numbers(); }

        // ---- headless proof ----------------------------------------------------------------------

        private static void WorldReadyPost()
        {
            if (_self == null || !_self.Active || _selfTestRan) return;
            // ObjectDB.UpdateRegisters() fires more than once, and the FIRST call (the client's
            // own bootstrap ObjectDB / the server before the world's DB is copied in) can still
            // have an empty recipe list - so don't burn the once-guard until there is something
            // to report.
            var odb = ObjectDB.instance;
            if (odb == null || odb.m_recipes == null || odb.m_recipes.Count == 0) return;
            _selfTestRan = true;
            try { _self.RunSelfTest(); }
            catch (Exception e) { Log.LogError("[RepairAll][SelfTest] threw: " + e); }
        }

        /// <summary>
        /// Cheap headless proof of the eligibility rules: for every repairable item in ObjectDB,
        /// which station (and which station LEVEL) vanilla's CanRepair would demand. No player and
        /// no GUI needed, so a dedicated server can run it. Changes nothing.
        /// </summary>
        private void RunSelfTest()
        {
            var odb = ObjectDB.instance;
            if (odb == null || odb.m_recipes == null)
            {
                Log.LogWarning("[RepairAll][SelfTest] ObjectDB has no recipes yet - skipped");
                return;
            }

            var byStation = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int repairable = 0, noStation = 0, noRecipe = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var recipe in odb.m_recipes)
            {
                if (recipe == null || recipe.m_item == null) continue;
                var data = recipe.m_item.m_itemData;
                if (data == null || data.m_shared == null) continue;
                string prefab = Tiers.CleanName(recipe.m_item.gameObject.name);
                if (!seen.Add(prefab)) continue;

                if (!data.m_shared.m_canBeReparied) continue;
                repairable++;

                // CanRepair accepts EITHER station: the recipe's repair station or its crafting
                // station, and only if min(stationLevel,4) >= recipe.m_minStationLevel.
                var repairStation = recipe.m_repairStation;
                var craftStation = recipe.m_craftingStation;
                if (repairStation == null && craftStation == null) { noStation++; continue; }

                string label = (repairStation != null ? repairStation.m_name : craftStation.m_name) +
                               " L" + recipe.m_minStationLevel;
                if (repairStation != null && craftStation != null &&
                    repairStation.m_name != craftStation.m_name)
                {
                    label += " (or " + craftStation.m_name + ")";
                }
                int c;
                byStation.TryGetValue(label, out c);
                byStation[label] = c + 1;
            }

            foreach (var go in odb.m_items ?? new List<GameObject>())
            {
                if (go == null) continue;
                var drop = go.GetComponent<ItemDrop>();
                var d = drop != null ? drop.m_itemData : null;
                if (d == null || d.m_shared == null || !d.m_shared.m_canBeReparied) continue;
                if (!seen.Contains(Tiers.CleanName(go.name))) noRecipe++;
            }

            var sb = new StringBuilder();
            sb.Append("[RepairAll][SelfTest] --- begin --- ").Append(Numbers());
            sb.Append("\n  repairable items with a recipe: ").Append(repairable)
              .Append("; of those, no crafting/repair station (never repairable): ").Append(noStation);
            sb.Append("\n  repairable items with NO recipe at all (never repairable): ").Append(noRecipe);
            foreach (var kv in byStation)
            {
                sb.Append("\n  requires ").Append(kv.Key).Append(": ").Append(kv.Value).Append(" item(s)");
            }
            sb.Append("\n  a batch repairs exactly the subset of Inventory.GetWornItems() that " +
                      "InventoryGui.CanRepair() accepts for the station you are standing at");
            sb.Append("\n[RepairAll][SelfTest] --- end ---");
            Log.LogInfo(sb.ToString());
        }
    }
}
