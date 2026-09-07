using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Headless proof for ExtraSlots and Loadouts. Both are client-side, so on a dedicated server
    /// they correctly report disabled(side) and never patch anything - which makes them unprovable
    /// by a server boot alone. Their DANGEROUS half is not the UI though, it is the storage layer,
    /// and that is pure: an Inventory, ObjectDB, a ZPackage and a string. All three exist on a
    /// dedicated server, so the whole data-safety contract can be exercised there.
    ///
    /// With [Slots] SelfTest = true (machine-local, default false, never synced) this module:
    ///   1. builds a test inventory, puts items in extra slots, runs the SAVE path and asserts the
    ///      vanilla package contains none of them while the blob contains all of them, then runs
    ///      the LOAD path and asserts every item came back into the right slot;
    ///   2. builds a vanilla package with an item parked at grid (0,7) - what a character migrating
    ///      from shudnal's ExtraSlots actually looks like - and asserts the rescue catches it
    ///      instead of letting Inventory.Load delete it;
    ///   3. round-trips a loadout through Encode/Decode.
    /// It patches the rescue hook onto its OWN Harmony instance (the ExtraSlots module is not
    /// patched on a server), touches no game state, and changes nothing when SelfTest is false.
    /// </summary>
    internal sealed class SlotsSelfTestModule : FeatureModule
    {
        public override string Name => "SlotsSelfTest";
        public override ModuleSide Side => ModuleSide.Both;
        public override string Section => "SlotsSelfTest";

        private static ConfigEntry<bool> _selfTest;
        private static bool _ran;
        private static int _pass, _fail;

        protected override void Bind()
        {
            // Deliberately in the [Slots] section, next to the feature it proves.
            _selfTest = BindLocal("Slots", "SelfTest", false,
                "Diagnostic. Once per world load, prove the extra-slot storage layer headlessly: " +
                "save lift, blob round trip, migration rescue and loadout encoding. Machine-local, " +
                "never synced. Leave it false in normal use.");
        }

        protected override void ApplyPatches()
        {
            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(SlotsSelfTestModule), nameof(WorldReady)));

            if (_selfTest != null && _selfTest.Value)
            {
                // The rescue prefix normally comes from ExtraSlotsModule, which is client-only.
                var addItemLoad = AccessTools.Method(typeof(Inventory), "AddItem", new[]
                {
                    typeof(string), typeof(int), typeof(float), typeof(Vector2i), typeof(bool), typeof(int),
                    typeof(int), typeof(long), typeof(string), typeof(Dictionary<string, string>), typeof(int), typeof(bool)
                });
                if (addItemLoad == null)
                    throw new Exception("Inventory.AddItem(string,int,float,Vector2i,...) not found");
                Harmony.Patch(addItemLoad,
                    prefix: new HarmonyMethod(typeof(SlotsRescue), nameof(SlotsRescue.AddItemLoadPrefix)));
            }
        }

        private static void WorldReady()
        {
            if (_ran || _selfTest == null || !_selfTest.Value) return;
            _ran = true;
            _pass = _fail = 0;
            Log.LogInfo("[SlotsSelfTest] --- begin --- " + SlotLayout.Describe());
            try
            {
                TestSaveLoad();
                TestGenericSlots();
                TestMigrationRescue();
                TestLoadoutRoundTrip();
            }
            catch (Exception e) { Log.LogError("[SlotsSelfTest] threw: " + e); _fail++; }
            Log.LogInfo("[SlotsSelfTest] --- end --- " + _pass + " passed, " + _fail + " FAILED");
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static void Check(bool ok, string what)
        {
            if (ok) { _pass++; Log.LogInfo("[SlotsSelfTest] PASS  " + what); }
            else { _fail++; Log.LogError("[SlotsSelfTest] FAIL  " + what); }
        }

        private static ItemDrop.ItemData Make(string prefab, int stack)
        {
            return SlotBlob.MakeItem(prefab, stack, 100f, false, 1, 0, 0L, "", null, 0, false);
        }

        private static string NamesIn(Inventory inv)
        {
            var sb = new StringBuilder();
            var all = inv.GetAllItems();
            for (int i = 0; i < all.Count; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(SlotBlob.PrefabNameOf(all[i]) ?? "?").Append("@").Append(all[i].m_gridPos.x)
                  .Append(",").Append(all[i].m_gridPos.y);
            }
            return sb.ToString();
        }

        private static bool Has(Inventory inv, string prefab)
        {
            var all = inv.GetAllItems();
            for (int i = 0; i < all.Count; i++)
                if (SlotBlob.PrefabNameOf(all[i]) == prefab) return true;
            return false;
        }

        // ---- test 1: the save lift and the blob --------------------------------------------------

        private static void TestSaveLoad()
        {
            if (ObjectDB.instance == null) { Log.LogWarning("[SlotsSelfTest] ObjectDB not ready - test 1 skipped"); return; }

            // slotKey -> prefab. BeltStrength is Megingjord's prefab name; the others are the
            // ordinary bronze set plus a food and an ammo stack.
            var wanted = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("helmet",   "HelmetBronze"),
                new KeyValuePair<string, string>("chest",    "ArmorBronzeChest"),
                new KeyValuePair<string, string>("utility1", "BeltStrength"),
                new KeyValuePair<string, string>("food1",    "CookedMeat"),
                new KeyValuePair<string, string>("ammo1",    "ArrowWood"),
                new KeyValuePair<string, string>("generic1", "Stone"),
            };

            var inv = new Inventory("nvlb-selftest", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);

            var wood = ObjectDB.instance.GetItemPrefab("Wood");
            if (wood != null) inv.AddItem(wood, 12);
            int vanillaCount = inv.GetAllItems().Count;

            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);

            var placed = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < wanted.Count; i++)
            {
                var slot = SlotLayout.ByKey(wanted[i].Key);
                if (slot == null) { Log.LogWarning("[SlotsSelfTest] no slot '" + wanted[i].Key + "' in this layout - skipped"); continue; }
                int stack = wanted[i].Value == "ArrowWood" ? 20 : (wanted[i].Value == "Stone" ? 5 : 1);
                var item = Make(wanted[i].Value, stack);
                if (item == null) { Log.LogWarning("[SlotsSelfTest] prefab '" + wanted[i].Value + "' not in ObjectDB - skipped"); continue; }
                if (!SlotLayout.Accepts(slot, item))
                { Check(false, wanted[i].Value + " should be accepted by slot " + slot.Key); continue; }
                Check(SlotStore.PlaceRaw(inv, item, slot.Pos),
                      "placed " + wanted[i].Value + " into slot " + slot.Key + " at " + slot.Pos.x + "," + slot.Pos.y);
                placed.Add(wanted[i]);
            }
            Check(placed.Count > 0, "at least one extra-slot item placed (" + placed.Count + ")");
            Log.LogInfo("[SlotsSelfTest] live grid: " + NamesIn(inv));

            // --- the save path: lift, write the package, put back
            var entries = SlotStore.Collect(inv);
            Check(entries.Count == placed.Count, "Collect() saw all " + placed.Count + " extra item(s), got " + entries.Count);
            var blob = SlotBlob.Encode(entries);

            SlotStore.Stash(inv);
            var pkg = new ZPackage();
            inv.Save(pkg);
            SlotStore.Unstash(inv);

            Check(inv.GetAllItems().Count == vanillaCount + placed.Count,
                  "unstash restored the grid (" + inv.GetAllItems().Count + " items)");

            var check = new Inventory("nvlb-verify", null, SlotLayout.VanillaWidth, SlotLayout.TotalHeight);
            pkg.SetPos(0);
            check.Load(pkg);
            bool leaked = false;
            for (int i = 0; i < placed.Count; i++) if (Has(check, placed[i].Value)) leaked = true;
            Check(!leaked, "the vanilla ZPackage contains NONE of the extra-slot items");
            Check(check.GetAllItems().Count == vanillaCount,
                  "the vanilla ZPackage still contains the " + vanillaCount + " ordinary item(s)");

            // --- the blob holds them all
            var decoded = SlotBlob.Decode(blob);
            Check(decoded != null && decoded.Count == placed.Count,
                  "the blob decodes to all " + placed.Count + " item(s), got " + (decoded == null ? -1 : decoded.Count));
            if (decoded != null)
            {
                for (int i = 0; i < placed.Count; i++)
                {
                    bool found = false;
                    for (int j = 0; j < decoded.Count; j++)
                        if (decoded[j].SlotKey == placed[i].Key &&
                            SlotBlob.PrefabNameOf(decoded[j].Item) == placed[i].Value) found = true;
                    Check(found, "blob holds " + placed[i].Value + " keyed to slot '" + placed[i].Key + "'");
                }
            }
            Log.LogInfo("[SlotsSelfTest] blob is " + blob.Length + " chars: " +
                        blob.Substring(0, Math.Min(72, blob.Length)) + (blob.Length > 72 ? "..." : ""));

            // --- the load path: vanilla package back, then re-inject the blob
            var reload = new Inventory("nvlb-reload", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            pkg.SetPos(0);
            reload.Load(pkg);
            SlotStore.SetHeight(reload, SlotLayout.TotalHeight);
            var leftovers = new List<ItemDrop.ItemData>();
            int injected = SlotStore.Inject(reload, SlotBlob.Decode(blob), leftovers);
            Check(injected == placed.Count && leftovers.Count == 0,
                  "re-injected " + injected + "/" + placed.Count + " item(s), " + leftovers.Count + " leftover");
            for (int i = 0; i < placed.Count; i++)
            {
                var slot = SlotLayout.ByKey(placed[i].Key);
                var at = reload.GetItemAt(slot.Pos.x, slot.Pos.y);
                Check(at != null && SlotBlob.PrefabNameOf(at) == placed[i].Value,
                      placed[i].Value + " is back in slot '" + placed[i].Key + "'");
            }
            Log.LogInfo("[SlotsSelfTest] reloaded grid: " + NamesIn(reload));
        }

        // ---- test 1b: the generic bottom row ------------------------------------------------------

        /// <summary>
        /// 0.4.2 replaced the three quick slots with plain storage cells. Two things must hold: a
        /// generic slot accepts anything, and a blob written by 0.4.1 - whose bottom-row items are
        /// keyed "quick1".."quick3" - still finds a home instead of being evacuated to the floor.
        /// </summary>
        private static void TestGenericSlots()
        {
            if (ObjectDB.instance == null) { Log.LogWarning("[SlotsSelfTest] ObjectDB not ready - test 1b skipped"); return; }

            var g1 = SlotLayout.ByKey("generic1");
            Check(g1 != null, "the layout has a generic1 slot (GenericSlots=" + SlotLayout.GenericCount + ")");
            if (g1 == null) return;

            var helmet = Make("HelmetBronze", 1);
            var arrow = Make("ArrowWood", 10);
            Check(helmet == null || SlotLayout.Accepts(g1, helmet), "a helmet fits in generic1");
            Check(arrow == null || SlotLayout.Accepts(g1, arrow), "an arrow stack fits in generic1");

            Check(SlotLayout.QuickCount > 0 || SlotLayout.ByKey("quick1") == null,
                  "no quick slot exists at the default QuickSlots=0 (QuickCount=" + SlotLayout.QuickCount + ")");
            var mapped = SlotLayout.LegacyKey("quick1");
            Check(mapped != null && mapped.Key == "generic1",
                  "a 0.4.1 blob key 'quick1' maps onto '" + (mapped == null ? "null" : mapped.Key) + "'");

            // The whole migration, end to end: a blob keyed the old way lands on the bottom row.
            if (arrow != null)
            {
                var inv = new Inventory("nvlb-migrate-quick", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
                SlotStore.SetHeight(inv, SlotLayout.TotalHeight);
                var blob = SlotBlob.Encode(new List<SlotEntry> { new SlotEntry { SlotKey = "quick1", Item = arrow } });
                var leftovers = new List<ItemDrop.ItemData>();
                int placed = SlotStore.Inject(inv, SlotBlob.Decode(blob), leftovers);
                var at = inv.GetItemAt(g1.Pos.x, g1.Pos.y);
                Check(placed == 1 && leftovers.Count == 0 && at != null &&
                      SlotBlob.PrefabNameOf(at) == "ArrowWood" && at.m_stack == 10,
                      "a legacy 'quick1' item is migrated into generic1 with its stack intact");
            }
        }

        // ---- test 2: migration rescue -------------------------------------------------------------

        private static void TestMigrationRescue()
        {
            if (ObjectDB.instance == null) { Log.LogWarning("[SlotsSelfTest] ObjectDB not ready - test 2 skipped"); return; }
            if (ObjectDB.instance.GetItemPrefab("ArrowWood") == null)
            { Log.LogWarning("[SlotsSelfTest] no ArrowWood - test 2 skipped"); return; }

            // A vanilla inventory package in format 106 holding one item at grid (0,7) - exactly
            // what a character saved by shudnal's ExtraSlots looks like to vanilla code.
            var pkg = new ZPackage();
            pkg.Write(106);
            pkg.Write(1);
            pkg.Write("ArrowWood");
            pkg.Write(42);                       // stack
            pkg.Write(1f);                       // durability
            pkg.Write(new Vector2i(0, 7));       // OUT OF BOUNDS for an 8x4 grid
            pkg.Write(false);                    // equipped
            pkg.Write(1);                        // quality
            pkg.Write(0);                        // variant
            pkg.Write(0L);                       // crafterID
            pkg.Write("");                       // crafterName
            pkg.Write(0);                        // customData count
            pkg.Write(0);                        // worldLevel
            pkg.Write(false);                    // pickedUp

            // First: prove vanilla really does delete it (capture disarmed).
            var vanilla = new Inventory("nvlb-vanilla", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            pkg.SetPos(0);
            vanilla.Load(pkg);
            Check(vanilla.GetAllItems().Count == 0,
                  "vanilla Inventory.Load silently DELETES the out-of-bounds item (the bug we fix)");

            // Now with the rescue armed.
            var inv = new Inventory("nvlb-migrate", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            SlotsRescue.BeginCapture(inv);
            pkg.SetPos(0);
            inv.Load(pkg);
            SlotsRescue.EndCapture();
            Check(SlotsRescue.CapturedCount == 1, "rescue captured " + SlotsRescue.CapturedCount + " item(s), expected 1");

            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);
            var summary = SlotsRescue.Place(null, inv);
            Check(summary != null, "rescue reported: " + summary);
            Check(Has(inv, "ArrowWood"), "the rescued ArrowWood is in the inventory: " + NamesIn(inv));

            var ammo = SlotLayout.ByKey("ammo1");
            if (ammo != null)
            {
                var at = inv.GetItemAt(ammo.Pos.x, ammo.Pos.y);
                Check(at != null && SlotBlob.PrefabNameOf(at) == "ArrowWood" && at.m_stack == 42,
                      "it landed in the ammo1 slot with its stack of 42 intact");
            }
        }

        // ---- test 3: loadout encoding ---------------------------------------------------------------

        private static void TestLoadoutRoundTrip()
        {
            var spec = new LoadoutSpec
            {
                RightPrefab = "SwordIron",
                RightQuality = 3,
                RightVariant = 0,
                LeftPrefab = "ShieldBronzeBuckler",
                LeftQuality = 2,
                LeftVariant = 1
            };
            var s = LoadoutsModule.Encode(spec);
            var back = LoadoutsModule.Decode(s);
            Check(back != null && back.RightPrefab == spec.RightPrefab && back.RightQuality == spec.RightQuality &&
                  back.RightVariant == spec.RightVariant && back.LeftPrefab == spec.LeftPrefab &&
                  back.LeftQuality == spec.LeftQuality && back.LeftVariant == spec.LeftVariant,
                  "loadout round trip: '" + s + "' -> " + (back == null ? "null" : back.ToString()));

            var oneHanded = new LoadoutSpec { RightPrefab = "AtgeirBronze", RightQuality = 1, RightVariant = 0 };
            var s2 = LoadoutsModule.Encode(oneHanded);
            var back2 = LoadoutsModule.Decode(s2);
            Check(back2 != null && back2.RightPrefab == "AtgeirBronze" && string.IsNullOrEmpty(back2.LeftPrefab),
                  "empty off-hand round trip: '" + s2 + "'");
            Check(LoadoutsModule.Decode("garbage") == null, "a corrupt loadout string decodes to null, not an exception");
            Check(LoadoutsModule.Decode(null) == null, "a missing loadout string decodes to null");
        }

        public override string StatusDetail()
        {
            return "SelfTest=" + (_selfTest != null && _selfTest.Value) +
                   (_ran ? " (ran: " + _pass + " pass, " + _fail + " fail)" : "");
        }
    }
}
