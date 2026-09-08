using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Headless proof for SafeSlots.
    ///
    /// The interesting half of this module is not the UI or the RPCs - it is the vault's file
    /// store and the one decision that decides whether a player is offered a restore. Both are
    /// pure: a string, a directory and two integers. So both can be driven to a verdict on a
    /// dedicated server with nobody online, which is exactly where a data-safety feature ought to
    /// be provable.
    ///
    /// It writes ONE throwaway vault file, under a player id no real character can have
    /// (<c>-selftest</c>: a real id is a signed long, so it can never contain a letter), and
    /// deletes it again at the end.
    /// </summary>
    internal static class SafeSlotsSelfTest
    {
        private const string TestId = "-selftest";
        private static int _pass, _fail;
        private static bool _ran;

        private static void Check(bool ok, string what)
        {
            if (ok) { _pass++; NoVikingLeftBehindPlugin.Log.LogInfo("[SafeSlots][SelfTest] PASS  " + what); }
            else { _fail++; NoVikingLeftBehindPlugin.Log.LogError("[SafeSlots][SelfTest] FAIL  " + what); }
        }

        private static void Info(string s)
        {
            NoVikingLeftBehindPlugin.Log.LogInfo("[SafeSlots][SelfTest] " + s);
        }

        public static void Run()
        {
            if (_ran) return;
            _ran = true;
            _pass = _fail = 0;
            Info("--- begin --- vault dir " + SlotVault.Dir);

            try
            {
                SlotVault.Delete(TestId);        // a previous run that died mid-way leaves nothing behind
                TestRoundTrip();
                TestVersionCap();
                TestSizeGuard();
                TestLoginDecision();
            }
            catch (Exception e)
            {
                _fail++;
                NoVikingLeftBehindPlugin.Log.LogError("[SafeSlots][SelfTest] threw: " + e);
            }
            finally
            {
                try
                {
                    SlotVault.Delete(TestId);
                    Check(!File.Exists(SlotVault.PathFor(TestId)),
                          "the throwaway vault file is gone again: " + SlotVault.PathFor(TestId));
                }
                catch (Exception e) { NoVikingLeftBehindPlugin.Log.LogWarning("[SafeSlots][SelfTest] cleanup: " + e.Message); }
            }

            Info("--- end --- " + _pass + " passed, " + _fail + " FAILED");
        }

        // ---- a blob to work with -------------------------------------------------------------------

        /// <summary>
        /// A real blob if ObjectDB is up (a dedicated server has one by Game.Start), otherwise a
        /// hand-built string in the same shape. The store never looks inside a blob, so the file
        /// tests are equally valid either way - but a real one also proves the encoder's own
        /// header is the header <see cref="SlotVault.CountIn"/> reads.
        /// </summary>
        private static string MakeBlob(out int expectedCount, out string how)
        {
            var wanted = new[] { "HelmetBronze", "ArrowWood", "CookedMeat" };
            var keys = new[] { "helmet", "ammo1", "food1" };

            if (ObjectDB.instance != null)
            {
                var entries = new List<SlotEntry>();
                for (int i = 0; i < wanted.Length; i++)
                {
                    var item = SlotBlob.MakeItem(wanted[i], i == 1 ? 42 : 1, 100f, false, 1, 0, 0L, "", null, 0, false);
                    if (item != null) entries.Add(new SlotEntry { SlotKey = keys[i], Item = item });
                }
                if (entries.Count > 0)
                {
                    expectedCount = entries.Count;
                    how = "SlotBlob.Encode of " + entries.Count + " real item(s)";
                    return SlotBlob.Encode(entries);
                }
            }

            // Fallback: same "1|<count>|<base64>" shape, no ObjectDB needed.
            expectedCount = 3;
            how = "a synthetic blob (ObjectDB was not ready)";
            var pkg = new ZPackage();
            pkg.Write(106);
            pkg.Write(3);
            return "1|3|" + pkg.GetBase64();
        }

        // ---- 1: write -> list -> read, byte for byte -------------------------------------------------

        private static void TestRoundTrip()
        {
            int expected;
            string how;
            var blob = MakeBlob(out expected, out how);
            Info("step 1: " + how + " -> " + blob.Length + " chars, header count=" +
                 SlotVault.CountIn(blob) + ", hash=" + SlotVault.Hash(blob));

            Check(SlotVault.CountIn(blob) == expected,
                  "CountIn reads " + SlotVault.CountIn(blob) + " out of the blob header, expected " + expected);

            VaultFile written;
            var r = SlotVault.Put(TestId, "SelfTest", blob, 5, out written);
            Check(r == SlotVault.PutResult.Stored, "the first put is Stored (was " + r + ")");
            Check(File.Exists(SlotVault.PathFor(TestId)),
                  "the vault file exists on disk: " + SlotVault.PathFor(TestId));

            // The list is what a login sees: it must find this character and count its versions.
            var all = SlotVault.All();
            bool listed = false;
            for (int i = 0; i < all.Count; i++)
                if (all[i].Key == TestId && all[i].Value == 1) listed = true;
            Check(listed, "SlotVault.All() lists " + TestId + " with 1 version (" + SlotVault.Summary() + ")");

            var back = SlotVault.Read(TestId);
            Check(back.versions.Count == 1, "reading it back gives 1 version, got " + back.versions.Count);
            if (back.versions.Count == 0) return;

            var v = back.versions[0];
            Check(string.Equals(v.blob, blob, StringComparison.Ordinal),
                  "the blob came back BYTE FOR BYTE identical (" + v.blob.Length + "/" + blob.Length + " chars)");
            Check(v.hash == SlotVault.Hash(blob), "the stored hash matches a fresh hash of the blob");
            Check(v.count == expected, "the stored count is " + v.count + ", expected " + expected);
            Check(back.name == "SelfTest", "the character name round-tripped ('" + back.name + "')");
            Check((DateTime.UtcNow - v.WhenUtc).TotalMinutes < 5,
                  "the stored timestamp parses back to about now (" + v.utc + " -> " + SlotVault.Ago(v.WhenUtc) + ")");

            // An unchanged blob must not burn a version slot - that is what stops a player who
            // saves every 20 minutes from pushing their real history out of the vault.
            VaultFile again;
            var r2 = SlotVault.Put(TestId, "SelfTest", blob, 5, out again);
            Check(r2 == SlotVault.PutResult.Unchanged, "putting the same blob again is Unchanged (was " + r2 + ")");
            Check(SlotVault.Read(TestId).versions.Count == 1, "and it is still 1 version on disk");
        }

        // ---- 2: the version cap ------------------------------------------------------------------------

        private static void TestVersionCap()
        {
            const int keep = 5;
            var hashes = new List<string>();
            for (int i = 0; i < 7; i++)
            {
                // Distinct payloads, valid header, increasing counts so the order is checkable.
                var pkg = new ZPackage();
                pkg.Write(106);
                pkg.Write(i + 1);
                pkg.Write("selftest-" + i);
                var blob = "1|" + (i + 1) + "|" + pkg.GetBase64();
                hashes.Add(SlotVault.Hash(blob));
                VaultFile _;
                SlotVault.Put(TestId, "SelfTest", blob, keep, out _);
            }

            var vf = SlotVault.Read(TestId);
            Check(vf.versions.Count == keep,
                  "after 7 more puts the vault holds exactly " + keep + " versions, got " + vf.versions.Count);
            if (vf.versions.Count != keep) return;

            Check(vf.versions[0].hash == hashes[6] && vf.versions[0].count == 7,
                  "the NEWEST put is first (count=" + vf.versions[0].count + ", want 7)");
            Check(vf.versions[keep - 1].count == 3,
                  "the oldest kept is the 3rd of the 7 (count=" + vf.versions[keep - 1].count +
                  ", want 3) - the two before it were trimmed, and so was the round-trip blob");

            bool descending = true;
            for (int i = 1; i < vf.versions.Count; i++)
                if (vf.versions[i].count >= vf.versions[i - 1].count) descending = false;
            Check(descending, "the five kept versions are in newest-first order");
        }

        // ---- 3: the 64 KB size guard ---------------------------------------------------------------------

        private static void TestSizeGuard()
        {
            int before = SlotVault.Read(TestId).versions.Count;

            var sb = new StringBuilder("1|9|");
            sb.Append('A', SlotVault.MaxBlobBytes + 4096);
            var huge = sb.ToString();
            Info("step 3: offering a " + Encoding.UTF8.GetByteCount(huge) + " byte blob (the limit is " +
                 SlotVault.MaxBlobBytes + ")");

            VaultFile _;
            var r = SlotVault.Put(TestId, "SelfTest", huge, 5, out _);
            Check(r == SlotVault.PutResult.TooBig, "an over-size blob is refused with TooBig (was " + r + ")");
            Check(SlotVault.Read(TestId).versions.Count == before,
                  "and nothing was written: still " + before + " version(s)");

            // One byte under the limit must still be accepted, so the guard is a limit and not a
            // wall a real (large) inventory could walk into by accident.
            var sb2 = new StringBuilder("1|9|");
            sb2.Append('B', SlotVault.MaxBlobBytes - 4 - 1);
            var justUnder = sb2.ToString();
            var r2 = SlotVault.Put(TestId, "SelfTest", justUnder, 5, out _);
            Check(r2 == SlotVault.PutResult.Stored,
                  "a blob one byte under the limit (" + Encoding.UTF8.GetByteCount(justUnder) +
                  " bytes) is still Stored (was " + r2 + ")");
            var backBlob = SlotVault.Read(TestId).versions[0].blob;
            Check(string.Equals(backBlob, justUnder, StringComparison.Ordinal),
                  "and that near-limit blob survived the file round trip intact (" + backBlob.Length + " chars)");
        }

        // ---- 4: the login decision --------------------------------------------------------------------------

        private static void TestLoginDecision()
        {
            Check(SafeSlotsModule.ShouldOffer(0, 0, 6),
                  "local store EMPTY + vault has 6 items -> OFFER a restore");
            Check(!SafeSlotsModule.ShouldOffer(3, 0, 6),
                  "local store has 3 items -> no offer (a full set is not second-guessed)");
            Check(!SafeSlotsModule.ShouldOffer(0, 2, 6),
                  "local store empty but the migration rescue found 2 -> no offer (they just came back)");
            Check(!SafeSlotsModule.ShouldOffer(0, 0, 0),
                  "local store empty and the vault is empty too -> no offer");
            Check(SafeSlotsModule.ShouldAsk(0, 0) && !SafeSlotsModule.ShouldAsk(1, 0),
                  "ShouldAsk keys only off the client's own state, so the server is always asked once");
        }
    }
}
