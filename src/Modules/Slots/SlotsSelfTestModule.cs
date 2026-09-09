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
        public override string Theme => "Server";
        public override string Hint => "Prove the extra-slot storage code works with no players online";

        protected override Opt EnabledOpt => base.EnabledOpt.Admin();

        private static ConfigEntry<bool> _selfTest;
        private static bool _ran;
        private static int _pass, _fail;

        protected override void Bind()
        {
            // Deliberately in the [Slots] section, next to the feature it proves.
            _selfTest = BindLocal("Slots", "SelfTest", false,
                "Diagnostic. Once per world load, prove the extra-slot storage layer headlessly: " +
                "save lift, blob round trip, migration rescue and loadout encoding. Machine-local, " +
                "never synced. Leave it false in normal use.",
                Opt.B("Prove the extra-slot storage code works with no players online").Admin().Restart());
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
                    typeof(int), typeof(long), typeof(string), typeof(Dictionary<string, string>), typeof(int), typeof(bool),
                    typeof(bool), typeof(bool)
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
                TestFullCycle();
                TestStashDepthLeak();
                TestLayoutDesyncNeverEmptiesTheBlob();
                TestEnabledToggleReturnsItems();
                TestStackAllSkipsExtraSlots();
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

        // ---- test 1c: the FULL round trip, twice ----------------------------------------------------

        /// <summary>
        /// Everything test 1 does, but as the sequence a real session actually performs, twice, and
        /// with the 0.8.0 logout upload in its real place. Written after Matt's characters came back
        /// from a session with their extra slots empty and the items sitting in the bag: the one
        /// thing test 1 never did was run the cycle a SECOND time on the same statics, which is
        /// where a leaked stash depth or a blob written over an emptied grid would show up.
        ///
        /// The order below is exactly the runtime order:
        ///   Player.Save   prefix  -> SlotStore.WriteBlob            (blob from the live grid)
        ///   Inventory.Save prefix -> SlotStore.Stash                (lift the extra items out)
        ///                 [vanilla writes the package]
        ///   Inventory.Save postfix-> SlotStore.Unstash              (put them straight back)
        ///   Game.Logout    prefix -> SlotBlob.Encode(Collect(inv))  (the vault upload; READ ONLY)
        ///   Player.Load   prefix  -> shrink + arm the rescue
        ///                 [vanilla reads the package]
        ///   Player.Load   postfix -> grow + ReadBlob + Inject
        /// </summary>
        private static void TestFullCycle()
        {
            if (ObjectDB.instance == null) { Log.LogWarning("[SlotsSelfTest] ObjectDB not ready - test 1c skipped"); return; }

            var wanted = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("helmet",   "HelmetBronze"),
                new KeyValuePair<string, string>("chest",    "ArmorBronzeChest"),
                new KeyValuePair<string, string>("food1",    "CookedMeat"),
                new KeyValuePair<string, string>("generic1", "Stone"),
            };

            var inv = new Inventory("nvlb-cycle", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            var wood = ObjectDB.instance.GetItemPrefab("Wood");
            if (wood != null) inv.AddItem(wood, 7);
            int vanillaCount = inv.GetAllItems().Count;
            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);

            int want = 0;
            for (int i = 0; i < wanted.Count; i++)
            {
                var slot = SlotLayout.ByKey(wanted[i].Key);
                if (slot == null) continue;
                var item = Make(wanted[i].Value, wanted[i].Value == "Stone" ? 4 : 1);
                if (item == null) continue;
                if (!SlotLayout.Accepts(slot, item)) continue;
                if (SlotStore.PlaceRaw(inv, item, slot.Pos)) want++;
            }
            Check(want >= 3, "cycle: seeded " + want + " extra-slot item(s)");
            if (want == 0) return;

            var customData = new Dictionary<string, string>();

            // Two full round trips on the SAME statics - one pass can hide a leaked stash depth.
            for (int pass = 1; pass <= 2; pass++)
            {
                // --- Player.Save prefix
                SlotStore.WriteBlob(customData, inv);
                string blob;
                customData.TryGetValue(SlotStore.BlobKey, out blob);
                Check(SlotBlob.Decode(blob) != null && SlotBlob.Decode(blob).Count == want,
                      "cycle pass " + pass + ": the save blob holds " + want + " item(s)");

                // --- Inventory.Save prefix / vanilla / postfix
                SlotStore.Stash(inv);
                Check(SlotStore.ExtraItems(inv).Count == 0,
                      "cycle pass " + pass + ": the lift emptied the extra cells before vanilla wrote");
                var pkg = new ZPackage();
                inv.Save(pkg);
                SlotStore.Unstash(inv);

                Check(SlotStore.ExtraItems(inv).Count == want,
                      "cycle pass " + pass + ": unstash put all " + want + " back (got " +
                      SlotStore.ExtraItems(inv).Count + ")");
                Check(SlotStore.StashedCount == 0,
                      "cycle pass " + pass + ": the stash buffer is empty again (" + SlotStore.StashedCount + ")");

                // --- Game.Logout prefix: the vault upload. It must READ and never write.
                string before = customData[SlotStore.BlobKey];
                var uploaded = SlotBlob.Encode(SlotStore.Collect(inv));
                Check(SlotVault.CountIn(uploaded) == want,
                      "cycle pass " + pass + ": the logout upload counts " + SlotVault.CountIn(uploaded) +
                      " item(s), wanted " + want);
                Check(customData[SlotStore.BlobKey] == before,
                      "cycle pass " + pass + ": the logout upload did NOT touch custom data");

                // --- the package vanilla actually wrote must be clean
                var check = new Inventory("nvlb-cycle-pkg", null, SlotLayout.VanillaWidth, SlotLayout.TotalHeight);
                pkg.SetPos(0);
                check.Load(pkg);
                Check(check.GetAllItems().Count == vanillaCount,
                      "cycle pass " + pass + ": the vanilla package holds only the " + vanillaCount +
                      " ordinary item(s), got " + check.GetAllItems().Count);

                // --- Player.Load prefix / vanilla / postfix, into a brand-new inventory
                var reload = new Inventory("nvlb-cycle-load", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
                SlotStore.SetHeight(reload, SlotLayout.VanillaHeight);
                SlotsRescue.BeginCapture(reload);
                pkg.SetPos(0);
                reload.Load(pkg);
                SlotsRescue.EndCapture();
                Check(SlotsRescue.CapturedCount == 0,
                      "cycle pass " + pass + ": nothing needed rescuing - the package never held an " +
                      "extra-slot item (" + SlotsRescue.CapturedCount + " captured)");

                SlotStore.SetHeight(reload, SlotLayout.TotalHeight);
                var entries = SlotStore.ReadBlob(customData, 0);
                var leftovers = new List<ItemDrop.ItemData>();
                int placed = SlotStore.Inject(reload, entries, leftovers);
                Check(entries != null && entries.Count == want && placed == want && leftovers.Count == 0,
                      "cycle pass " + pass + ": reloaded " + placed + "/" +
                      (entries == null ? -1 : entries.Count) + " item(s), " + leftovers.Count +
                      " evacuated (wanted " + want + "/0)");
                Check(SlotStore.Orphans(reload).Count == 0,
                      "cycle pass " + pass + ": no orphan sits in a cell with no slot under it");

                inv = reload;   // the reloaded grid is what the next pass saves, as in a real session
            }
        }

        // ---- test 1d: the stash depth must never leak -------------------------------------------------

        /// <summary>
        /// The save lift is re-entrant by a static counter, and the counter is the single point of
        /// failure in the whole design: if a Stash is ever not matched by an Unstash, the NEXT save
        /// takes the "already stashed" early return, lifts nothing, and vanilla writes the
        /// extra-slot items into its own package at out-of-grid positions - which is precisely the
        /// deletion this module exists to prevent, and (when the next load has the grid grown
        /// already) shows up as items appearing loose in the bag.
        ///
        /// This proves the counter returns to zero on the paths a session actually takes, including
        /// a nested Inventory.Save inside a Player.Save.
        /// </summary>
        private static void TestStashDepthLeak()
        {
            if (ObjectDB.instance == null) { Log.LogWarning("[SlotsSelfTest] ObjectDB not ready - test 1d skipped"); return; }

            var inv = new Inventory("nvlb-stash", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);
            var g1 = SlotLayout.ByKey("generic1");
            if (g1 == null) { Log.LogWarning("[SlotsSelfTest] no generic1 - test 1d skipped"); return; }
            var stone = Make("Stone", 3);
            if (stone == null || !SlotStore.PlaceRaw(inv, stone, g1.Pos)) return;

            // A plain lift.
            SlotStore.Stash(inv);
            SlotStore.Unstash(inv);
            Check(SlotStore.ExtraItems(inv).Count == 1 && SlotStore.StashedCount == 0,
                  "stash: one lift returns the item and empties the buffer");

            // Nested, the way Player.Save -> Inventory.Save arrives.
            SlotStore.Stash(inv);
            SlotStore.Stash(inv);
            SlotStore.Unstash(inv);
            Check(SlotStore.ExtraItems(inv).Count == 0,
                  "stash: the inner unstash does NOT put the items back early");
            SlotStore.Unstash(inv);
            Check(SlotStore.ExtraItems(inv).Count == 1 && SlotStore.StashedCount == 0,
                  "stash: the outer unstash restores, and the buffer is empty");

            // The failure mode itself: a Stash with no Unstash must not poison the NEXT save.
            // This is the assertion that would have caught items leaking into the vanilla package.
            SlotStore.Stash(inv);                       // leaked - no Unstash
            SlotStore.ResetLift();                      // what a load/spawn now does
            SlotStore.Stash(inv);
            bool lifted = SlotStore.ExtraItems(inv).Count == 0;
            SlotStore.Unstash(inv);
            Check(lifted,
                  "stash: after a leaked lift is reset, the next save still lifts the items out " +
                  "(this is the assertion that catches items leaking into the vanilla package)");
            Check(SlotStore.ExtraItems(inv).Count == 1 && SlotStore.StashedCount == 0,
                  "stash: and they come back afterwards");
        }

        // ---- test 1e: the bug Matt hit -----------------------------------------------------------------

        /// <summary>
        /// THE REGRESSION TEST for the 0.8.0-0.8.6 "my extra slots emptied themselves into my
        /// inventory" bug, reproduced from the evidence in the character file: live blob count 0,
        /// bak1 count 5, bak2 count 5 - a good blob overwritten with an empty one and rotated down.
        ///
        /// The mechanism: <see cref="SlotStore.Collect"/> keys every item by the slot under its
        /// cell and skips any item whose cell has no slot in the CURRENT layout. Let the layout and
        /// the grid disagree - which is what a server pushing a changed [Slots] count does to a
        /// client that is already holding items - and Collect goes quiet, WriteBlob persists "you
        /// own nothing", and the still-present items get evacuated into the bag by the next Orphans
        /// pass. One cause, both halves of what Matt saw.
        ///
        /// The test shrinks the layout under a loaded grid on purpose and asserts the blob is NOT
        /// written. Before the fix, `after` was the empty blob and this failed.
        /// </summary>
        private static void TestLayoutDesyncNeverEmptiesTheBlob()
        {
            if (ObjectDB.instance == null) { Log.LogWarning("[SlotsSelfTest] ObjectDB not ready - test 1e skipped"); return; }

            var inv = new Inventory("nvlb-desync", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);

            // Park items in the LAST slots of the current layout - the ones a shrink takes away.
            var keys = new List<string>();
            for (int i = 0; i < SlotLayout.Slots.Count; i++) keys.Add(SlotLayout.Slots[i].Key);
            int seeded = 0;
            for (int i = keys.Count - 1; i >= 0 && seeded < 2; i--)
            {
                var slot = SlotLayout.ByKey(keys[i]);
                var item = Make("Stone", 2);
                if (slot == null || item == null || !SlotLayout.Accepts(slot, item)) continue;
                if (SlotStore.PlaceRaw(inv, item, slot.Pos)) seeded++;
            }
            Check(seeded > 0, "desync: seeded " + seeded + " item(s) in the last slot(s)");
            if (seeded == 0) return;

            var data = new Dictionary<string, string>();
            SlotStore.WriteBlob(data, inv);
            string good;
            data.TryGetValue(SlotStore.BlobKey, out good);
            int goodCount = SlotBlob.Decode(good) == null ? -1 : SlotBlob.Decode(good).Count;
            Check(goodCount == SlotStore.ExtraItems(inv).Count,
                  "desync: the good blob holds all " + SlotStore.ExtraItems(inv).Count + " item(s)");

            // Now the disagreement: the layout shrinks while the items stay exactly where they are,
            // which is what a [Slots] push from the server does before anything can relayout.
            // The live counts are captured first so the layout can be put back exactly - the
            // ExtraSlots module itself is client-side and has no instance on a dedicated server.
            string was = SlotLayout.Describe();
            bool wasEquip = SlotLayout.EquipmentOn;
            int wasUtil = SlotLayout.UtilityCount, wasFood = SlotLayout.FoodCount;
            int wasAmmo = SlotLayout.AmmoCount, wasQuick = SlotLayout.QuickCount;
            int wasGeneric = SlotLayout.GenericCount;
            SlotLayout.Rebuild(false, 0, 0, 0, 0, 0);
            int stranded = SlotStore.ExtraItems(inv).Count;
            int keyed = SlotStore.Collect(inv).Count;
            Check(stranded > 0 && keyed < stranded,
                  "desync: with the layout shrunk, the grid still holds " + stranded +
                  " item(s) but Collect can key only " + keyed + " - this is the lossy state");

            SlotStore.WriteBlob(data, inv);
            string after;
            data.TryGetValue(SlotStore.BlobKey, out after);
            Check(after == good,
                  "desync: WriteBlob REFUSED to overwrite the good blob (this is the 0.8.7 fix; " +
                  "before it, the blob became 1|0| and the real one rotated into bak1)");
            string bak1;
            data.TryGetValue(SlotStore.BackupKey(1), out bak1);
            Check(string.IsNullOrEmpty(bak1),
                  "desync: and nothing was rotated into bak1 (" + (bak1 == null ? "absent" : "len " + bak1.Length) + ")");

            int which;
            Check(SlotStore.BestBackup(data, out which) <= 0,
                  "desync: there is still nothing in the backups to have to offer back");

            // Put the layout back before anything else runs against it.
            SlotLayout.Rebuild(wasEquip, wasUtil, wasFood, wasAmmo, wasQuick, wasGeneric);
            Check(SlotLayout.Describe() == was,
                  "desync: the layout was restored afterwards (" + SlotLayout.Describe() + ")");
        }

        // ---- test 1f: THE bug - toggling [Slots] Enabled off and on again ------------------------------

        /// <summary>
        /// The regression test for what actually happened to Matt's character on 2026-09-08.
        ///
        /// The cfg backups show `[Slots] Enabled` written false at 17:37:59 and true again at
        /// 17:38:05 - six seconds apart, from the in-game settings tab. Turning the module OFF
        /// evacuates every extra-slot item into the bag, which is correct and deliberate (rule 3:
        /// never leave an item in a cell that is about to stop existing). Turning it back ON only
        /// regrew the grid, so the items stayed in the bag and the slots stayed empty - and the
        /// next save then honestly wrote "nothing in the slots", rotating the real blob down into
        /// bak1. Both halves of the report - "the slots are empty" and "the items are in my
        /// inventory" - from one missing return leg.
        ///
        /// So: off, then on, must end with the items back in their own slots.
        /// </summary>
        private static void TestEnabledToggleReturnsItems()
        {
            if (ObjectDB.instance == null) { Log.LogWarning("[SlotsSelfTest] ObjectDB not ready - test 1f skipped"); return; }

            var inv = new Inventory("nvlb-toggle", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);
            SlotStore.Managed = inv;

            // The five Matt actually had: armour in chest and legs, and all three food slots.
            var wanted = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("chest", "ArmorRagsChest"),
                new KeyValuePair<string, string>("legs",  "ArmorRagsLegs"),
                new KeyValuePair<string, string>("food1", "CookedMeat"),
                new KeyValuePair<string, string>("food2", "Blueberries"),
                new KeyValuePair<string, string>("food3", "Raspberry"),
            };
            var placedKeys = new List<string>();
            for (int i = 0; i < wanted.Count; i++)
            {
                var slot = SlotLayout.ByKey(wanted[i].Key);
                var item = Make(wanted[i].Value, 1);
                if (slot == null || item == null || !SlotLayout.Accepts(slot, item)) continue;
                if (SlotStore.PlaceRaw(inv, item, slot.Pos)) placedKeys.Add(wanted[i].Key);
            }
            Check(placedKeys.Count >= 3, "toggle: seeded " + placedKeys.Count + " item(s) in their slots");
            if (placedKeys.Count == 0) { SlotStore.Managed = null; return; }

            int seeded = placedKeys.Count;
            int totalBefore = inv.GetAllItems().Count;

            // --- OFF: everything must end up in the bag, nothing lost.
            ExtraSlotsModule.TestToggleOff("self test: module turned off");
            Check(SlotStore.ExtraItems(inv).Count == 0,
                  "toggle: OFF emptied the extra cells");
            Check(inv.GetAllItems().Count == totalBefore,
                  "toggle: OFF lost nothing - still " + inv.GetAllItems().Count + " item(s) in the inventory");

            // --- ON: and this is the leg that was missing.
            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);
            int back = ExtraSlotsModule.TestToggleOn();
            Check(back == seeded,
                  "toggle: ON put " + back + "/" + seeded + " item(s) back into their slots " +
                  "(before 0.8.7 this was 0 and they stayed loose in the bag)");
            Check(inv.GetAllItems().Count == totalBefore,
                  "toggle: ON lost nothing either (" + inv.GetAllItems().Count + " item(s))");

            for (int i = 0; i < placedKeys.Count; i++)
            {
                var slot = SlotLayout.ByKey(placedKeys[i]);
                Check(slot != null && inv.GetItemAt(slot.Pos.x, slot.Pos.y) != null,
                      "toggle: slot '" + placedKeys[i] + "' is occupied again");
            }

            // --- and a save now records them, instead of writing "nothing".
            var data = new Dictionary<string, string>();
            SlotStore.WriteBlob(data, inv);
            string blob;
            data.TryGetValue(SlotStore.BlobKey, out blob);
            var decoded = SlotBlob.Decode(blob);
            Check(decoded != null && decoded.Count == seeded,
                  "toggle: the save after the toggle holds " + (decoded == null ? -1 : decoded.Count) +
                  " item(s), not 0 - this is the blob that went empty on the real character");

            // --- AND the recovery path must not duplicate. This is the state Matt's character is
            // actually in: the five items sitting loose in the bag, and a backup that still lists
            // all five. Restoring on top of that would hand him a second copy of everything.
            ExtraSlotsModule.TestToggleOff("self test: off again, to leave them in the bag");
            var bak = SlotBlob.Decode(blob);
            Check(bak != null && bak.Count == seeded,
                  "toggle: the backup blob still lists all " + seeded + " item(s)");
            Check(SlotStore.MissingFrom(inv, bak) == 0,
                  "toggle: with the items loose in the bag, MissingFrom says " +
                  SlotStore.MissingFrom(inv, bak) + " are missing - nothing to restore, so a " +
                  "restore cannot duplicate them");
            Check(SlotStore.NotAlreadyHeld(inv, bak).Count == 0,
                  "toggle: and NotAlreadyHeld hands the restore command an empty list");

            // One genuinely gone: remove a single item and prove exactly one is offered back.
            var live = SlotStore.Items(inv);
            if (live.Count > 0)
            {
                live.RemoveAt(live.Count - 1);
                Check(SlotStore.MissingFrom(inv, bak) == 1,
                      "toggle: drop one item and exactly 1 is reported missing (got " +
                      SlotStore.MissingFrom(inv, bak) + ") - only that one would be restored");
            }

            SlotStore.Managed = null;
        }

        // ---- test 1g: vanilla Stack-all must not empty the extra slots -------------------------------

        /// <summary>
        /// The 0.9.1 bug: vanilla's "Stack all" button and the hold-E gesture both run
        /// <c>Inventory.StackAll(from, message)</c>, which walks <c>from.GetAllItems()</c> with no
        /// grid-row filter - so a chest holding arrows pulled the arrows out of the ammo slots.
        /// <c>ExtraSlotsModule.StackAllSource</c> replaces that one call; this proves the filter it
        /// applies.
        ///
        /// Vanilla's own <c>StackAll</c> cannot be called here: it dereferences
        /// <c>Player.m_localPlayer</c> (IsItemEquiped, Message) and <c>Game.instance</c>, neither of
        /// which exists on a dedicated server. So the test drives
        /// <c>ExtraSlotsModule.FilterVanillaRows</c> - the half of the guard that is pure - and then
        /// replays vanilla's move loop verbatim over what it returned, which is exactly the sequence
        /// the transpiled method executes in game. It also asserts the guard is inert on an
        /// inventory the module does not manage.
        /// </summary>
        private static void TestStackAllSkipsExtraSlots()
        {
            if (ObjectDB.instance == null) { Log.LogWarning("[SlotsSelfTest] ObjectDB not ready - test 1g skipped"); return; }

            var ammo = SlotLayout.ByKey("ammo1");
            if (ammo == null) { Log.LogWarning("[SlotsSelfTest] no ammo1 slot in this layout - test 1g skipped"); return; }

            var inSlot = Make("ArrowWood", 20);
            var inBag = Make("ArrowWood", 15);
            if (inSlot == null || inBag == null)
            { Log.LogWarning("[SlotsSelfTest] no ArrowWood prefab - test 1g skipped"); return; }

            // The bag stack goes in FIRST, while the grid is still four rows high, so vanilla's own
            // FindEmptySlot cannot put it in an extra row (FindEmptySlotPrefix is client-side and is
            // not installed here). Then the grid grows and the ammo slot is filled by exact
            // placement - PlaceRaw never merges, which is the whole reason it exists.
            var inv = new Inventory("nvlb-stackall", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            Check(inv.AddItem(inBag), "stackall: 15 arrows seeded into the bag");
            Check(inBag.m_gridPos.y < SlotLayout.VanillaHeight,
                  "stackall: the bag stack really is in a vanilla row (y=" + inBag.m_gridPos.y + ")");

            SlotStore.SetHeight(inv, SlotLayout.TotalHeight);
            SlotStore.Managed = inv;
            Check(SlotStore.PlaceRaw(inv, inSlot, ammo.Pos),
                  "stackall: 20 more arrows seeded into the ammo slot at " + ammo.Pos.x + "," + ammo.Pos.y);

            // The chest already holds the same arrow, which is what makes StackAll interested in it.
            var chest = new Inventory("nvlb-stackall-chest", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            var seed = Make("ArrowWood", 1);
            Check(seed != null && chest.AddItem(seed), "stackall: the chest already holds an arrow");

            // --- the guard: what vanilla's loop is allowed to see
            var source = ExtraSlotsModule.FilterVanillaRows(inv.GetAllItems());
            Check(!source.Contains(inSlot), "stackall: the ammo-slot stack is NOT offered to StackAll");
            Check(source.Contains(inBag), "stackall: the bag stack IS offered to StackAll");
            Check(source.Count == inv.GetAllItems().Count - 1,
                  "stackall: exactly one stack was filtered out (" + source.Count + " of " +
                  inv.GetAllItems().Count + " offered)");

            // --- vanilla's move loop, verbatim, over that source (Inventory.cs:246-252 minus the
            //     IsItemEquiped call, which needs a local player; neither arrow stack is equipped).
            var moved = new List<ItemDrop.ItemData>(source);
            for (int i = 0; i < moved.Count; i++)
            {
                var item = moved[i];
                if (chest.ContainsItemByName(item.m_shared.m_name) && chest.AddItem(item)) inv.RemoveItem(item);
            }

            Check(inv.GetItemAt(ammo.Pos.x, ammo.Pos.y) == inSlot && inSlot.m_stack == 20,
                  "stackall: the ammo slot still holds its own 20 arrows after Stack all");
            Check(!inv.GetAllItems().Contains(inBag), "stackall: the bag stack left the inventory");
            Check(ArrowsIn(chest) == 16, "stackall: the chest received the bag's 15 arrows on top of " +
                  "its own 1 (chest holds " + ArrowsIn(chest) + ")");
            Check(SlotStore.ExtraItems(inv).Count == 1,
                  "stackall: exactly one item is still in an extra slot (" +
                  SlotStore.ExtraItems(inv).Count + ")");

            // --- the transpiler must still find its one call site in the live game's IL. ExtraSlots
            //     is client-side, so a dedicated server never applies that patch and a game update
            //     that moved the call would otherwise go unnoticed until somebody played. Running
            //     the transpiler over the real method body here catches it on the server boot.
            try
            {
                var stackAll = AccessTools.Method(typeof(Inventory), "StackAll",
                                                  new[] { typeof(Inventory), typeof(bool) });
                Check(stackAll != null, "stackall: Inventory.StackAll(Inventory,bool) still exists");
                if (stackAll != null)
                {
                    var body = PatchProcessor.GetOriginalInstructions(stackAll);
                    var patched = new List<CodeInstruction>(ExtraSlotsModule.StackAllTranspiler(body));
                    Check(patched.Count == body.Count,
                          "stackall: the transpiler replaced one call and moved nothing else (" +
                          patched.Count + " vs " + body.Count + " instructions)");
                }
            }
            catch (Exception e)
            {
                Check(false, "stackall: the transpiler no longer matches Inventory.StackAll - " + e.Message);
            }

            // --- and the guard is inert on anything we do not manage: same list, same reference.
            var other = new Inventory("nvlb-stackall-other", null, SlotLayout.VanillaWidth, SlotLayout.VanillaHeight);
            var loose = Make("ArrowWood", 3);
            if (loose != null) other.AddItem(loose);
            Check(ReferenceEquals(ExtraSlotsModule.StackAllSource(other), other.GetAllItems()),
                  "stackall: an unmanaged inventory gets vanilla's own list back, untouched");

            SlotStore.Managed = null;
        }

        /// <summary>Total ArrowWood across every stack, counted by prefab so no name token is assumed.</summary>
        private static int ArrowsIn(Inventory inv)
        {
            int n = 0;
            var all = inv.GetAllItems();
            for (int i = 0; i < all.Count; i++)
                if (SlotBlob.PrefabNameOf(all[i]) == "ArrowWood") n += all[i].m_stack;
            return n;
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
