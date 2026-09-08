using System;
using System.IO;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The headless proof that the **Network panel** works end to end: that this mod can change a
    /// setting inside *another mod's* config file through the same door, and that the door refuses
    /// everything it is supposed to refuse.
    ///
    /// Runs only on a dedicated server, only when <c>[Access] NetworkSelfTest</c> is on, and only
    /// once per boot. It drives the real <see cref="TweakDoor"/> with the server's own peer id, so
    /// every step goes through the same permission check, allowlist, value validation, rate
    /// limiter, timestamped backup and undo history a player would - and after each step it
    /// re-reads the value **straight off SmoothServer's cfg file on disk**, which is the only way
    /// to prove the file (the thing <c>cfg.py</c> and the backups care about) really changed.
    ///
    /// The steps are spaced a couple of seconds apart on purpose. SmoothServer has its own file
    /// watcher and re-asserts its preset table after every reload; leaving room between steps
    /// means its own log lines land in order next to ours instead of racing them, and the log
    /// reads as the story it is.
    ///
    /// Nothing is left changed: the last two steps undo both changes, and the final check is that
    /// the file holds exactly the value it held before the test started.
    /// </summary>
    internal static class NetworkSelfTest
    {
        /// <summary>Seconds between steps, so SmoothServer's watcher and ours never overlap.</summary>
        private const double StepSeconds = 2.5;

        private static bool _armed;
        private static bool _done;
        private static int _step;
        private static DateTime _nextAt;

        private static SettingInfo _profile;
        private static SettingInfo _map;
        private static string _original;
        private static string _mapOriginal;
        private static int _backupsBefore;
        private static bool _failed;

        public static void Arm()
        {
            if (_armed || _done) return;
            _armed = true;
            _nextAt = DateTime.UtcNow.AddSeconds(StepSeconds);
        }

        /// <summary>Called every frame from the plugin's Update. Cheap when idle.</summary>
        public static void Pump()
        {
            if (!_armed || _done) return;

            // Never interleave with the ordinary door self-test: they share one undo stack.
            if (AccessModule.SelfTestWanted && !AccessSelfTest.Finished) return;

            if (DateTime.UtcNow < _nextAt) return;
            _nextAt = DateTime.UtcNow.AddSeconds(StepSeconds);

            try { Step(); }
            catch (Exception e) { _done = true; Log("FAILED with an exception: " + e); }
        }

        // ---- the script ---------------------------------------------------------------------

        private static void Step()
        {
            switch (_step++)
            {
                case 0: Setup(); break;
                case 1: Set("1/9", "Default"); break;
                case 2: Set("2/9", "FastLink"); break;
                case 3: Undo("3/9", "Default"); break;
                case 4: Undo("4/9", _original); break;
                case 5: SecondSection("5/9"); break;
                case 6: UndoSecondSection("6/9"); break;
                case 7: RefusePresetDriven("7/9"); break;
                case 8: RefuseNotOnTheAllowlist("8/9"); break;
                case 9: RefuseNonAdmin("9/9"); break;
                default: Finish(); break;
            }
        }

        private static void Setup()
        {
            if (!SmoothServerBridge.Available)
            {
                _done = true;
                Log(SmoothServerBridge.NotInstalled + " Nothing to test - the panel does not exist " +
                    "here and the door would refuse every SmoothServer key with that same sentence.");
                return;
            }

            Log("SmoothServer " + SmoothServerBridge.Version + " found, config " + SmoothServerBridge.CfgPath);
            Log("allowlist (" + SmoothServerBridge.Rows().Count + " rows): " + SmoothServerBridge.AllowlistLine());

            _profile = SmoothServerBridge.Find("Profiles", "Profile");
            _map = SmoothServerBridge.Find("Map", "Enabled");
            if (_profile == null || _map == null)
            {
                _done = true;
                Log("FAILED - the allowlist did not resolve " +
                    (_profile == null ? "[Profiles] Profile" : "[Map] Enabled"));
                return;
            }

            _original = OnDisk();
            _backupsBefore = Backups();
            Log("before: file has Profile = " + _original + ", entry = " + _profile.CurrentString +
                ", backups = " + _backupsBefore);

            if (_original != _profile.CurrentString)
                Fail("the file and SmoothServer's live entry disagree before we touched anything");
        }

        private static void Set(string label, string want)
        {
            string before = _profile.CurrentString;
            string message;
            bool ok = TweakDoor.ApplyOne(ZNet.GetUID(), SmoothServerBridge.Prefix + "Profiles",
                                         "Profile", want, out message);
            Log("step " + label + " set " + want + ": door ok=" + ok + " \"" + message + "\"");
            if (!ok) { Fail("the door refused a change the server itself made"); return; }

            Verify(label, before, want);
        }

        private static void Undo(string label, string want)
        {
            string before = _profile.CurrentString;
            string message;
            bool ok = TweakDoor.Undo(ZNet.GetUID(), out message);
            Log("step " + label + " undo: door ok=" + ok + " \"" + message + "\"");
            if (!ok) { Fail("the door refused an undo of its own change"); return; }

            Verify(label, before, want);
        }

        private static void Verify(string label, string before, string want)
        {
            string onDisk = OnDisk();
            string live = _profile.CurrentString;
            int backups = Backups();
            bool pass = onDisk == want && live == want && backups > _backupsBefore;

            Log("step " + label + " check: " + before + " -> file=" + onDisk + " entry=" + live +
                " backups=" + backups + "  -> " + (pass ? "OK" : "MISMATCH"));

            if (!pass)
                Fail("expected " + want + " in both SmoothServer's cfg file and its live entry" +
                     (backups > _backupsBefore ? "" : ", and a new .bak- file next to it"));
        }

        /// <summary>
        /// A second SmoothServer section, so the proof is of the allowlist mechanism and not of
        /// one lucky key. [Map] Enabled is nothing to do with the preset table, so it is a clean
        /// bool round trip: on -> off -> undo.
        /// </summary>
        private static void SecondSection(string label)
        {
            _mapOriginal = _map.CurrentString;
            string before = _mapOriginal;
            string want = string.Equals(before, "true", StringComparison.OrdinalIgnoreCase) ? "false" : "true";

            string message;
            bool ok = TweakDoor.ApplyOne(ZNet.GetUID(), SmoothServerBridge.Prefix + "Map",
                                         "Enabled", want, out message);
            Log("step " + label + " a second section, [Map] Enabled -> " + want + ": ok=" + ok +
                " \"" + message + "\"");
            if (!ok) { Fail("the door refused a change to [Map] Enabled"); return; }

            string onDisk = CfgFile.GetValue(SmoothServerBridge.CfgPath, "Map", "Enabled");
            bool pass = onDisk == want && _map.CurrentString == want;
            Log("step " + label + " check: file=" + onDisk + " entry=" + _map.CurrentString +
                "  -> " + (pass ? "OK" : "MISMATCH"));
            if (!pass) Fail("[Map] Enabled did not reach both the file and the live entry");
        }

        private static void UndoSecondSection(string label)
        {
            string message;
            bool ok = TweakDoor.Undo(ZNet.GetUID(), out message);
            string onDisk = CfgFile.GetValue(SmoothServerBridge.CfgPath, "Map", "Enabled");
            bool pass = ok && onDisk == _mapOriginal && _map.CurrentString == _mapOriginal;
            Log("step " + label + " undo it: ok=" + ok + " \"" + message + "\" file=" + onDisk +
                " entry=" + _map.CurrentString + " (was " + _mapOriginal + ")  -> " +
                (pass ? "OK" : "MISMATCH"));
            if (!pass) Fail("[Map] Enabled was not put back");
        }

        /// <summary>
        /// SmoothServer's preset owns [Compression] Enabled while the profile is Default or
        /// FastLink: it would re-assert its own value a second later, so the door refuses the
        /// change up front and says what to do instead. Turning it ON is always allowed, because
        /// that is what both presets want anyway.
        /// </summary>
        private static void RefusePresetDriven(string label)
        {
            string profile = SmoothServerBridge.CurrentProfile();
            var compression = SmoothServerBridge.Find("Compression", "Enabled");
            string before = compression != null ? compression.CurrentString : "?";

            string message;
            bool ok = TweakDoor.ApplyOne(ZNet.GetUID(), SmoothServerBridge.Prefix + "Compression",
                                         "Enabled", "false", out message);
            string now = compression != null ? compression.CurrentString : "?";
            bool pass = !ok && now == before;

            Log("step " + label + " a key the preset owns, switched off while Profile=" + profile +
                " ([Compression] Enabled=false): ok=" + ok + " \"" + message + "\" still=" + now +
                "  -> " + (pass ? "OK" : "MISMATCH"));
            if (!pass) Fail("the door let a preset-driven key be changed to a value the preset " +
                            "would silently revert");
        }

        private static void RefuseNotOnTheAllowlist(string label)
        {
            // A real SmoothServer key, deliberately NOT on the panel's list. A client asking for
            // it - however it asks - must be refused, or the panel would be a remote control for
            // all ~60 of that mod's knobs.
            string message;
            bool ok = TweakDoor.ApplyOne(ZNet.GetUID(), SmoothServerBridge.Prefix + "AdaptiveBudget",
                                         "CeilingBytes", "262144", out message);
            Log("step " + label + " a SmoothServer key that is not on the allowlist " +
                "(AdaptiveBudget.CeilingBytes, asked for by the server itself): ok=" + ok +
                " \"" + message + "\"  -> " + (!ok ? "OK" : "LEAK"));
            if (ok) Fail("the door applied a SmoothServer key that is not on the allowlist");
        }

        private static void RefuseNonAdmin(string label)
        {
            // A peer id that is not on this server, so ZNet.IsAdmin says no - exactly the answer
            // an ordinary player gets for an admin-tier row.
            const long stranger = 987654321L;
            string message;
            bool ok = TweakDoor.ApplyOne(stranger, SmoothServerBridge.Prefix + "General",
                                         "EnforceClientMod", "true", out message);
            Log("step " + label + " an admin-only row asked for by a non-admin caller " +
                "(General.EnforceClientMod): ok=" + ok + " \"" + message + "\"  -> " +
                (!ok ? "OK" : "LEAK"));
            if (ok) Fail("a non-admin changed an admin-only SmoothServer setting");
        }

        private static void Finish()
        {
            _done = true;

            string onDisk = OnDisk();
            string live = _profile != null ? _profile.CurrentString : "?";
            string mapNow = _map != null ? _map.CurrentString : "?";
            bool restored = onDisk == _original && live == _original &&
                            (_mapOriginal == null || mapNow == _mapOriginal);

            Log("restored: file has Profile = " + onDisk + ", entry = " + live +
                " (was " + _original + "); [Map] Enabled = " + mapNow +
                " (was " + (_mapOriginal ?? "?") + ")  -> " + (restored ? "OK" : "MISMATCH"));
            if (!restored) Fail("the original value was not restored - check the .bak-* files " +
                                "next to " + SmoothServerBridge.CfgPath);

            Log(_failed
                ? "FAILED - see the MISMATCH/LEAK lines above"
                : "PASSED - the door writes SmoothServer's own cfg file with a backup, its watcher " +
                  "and ServerSync pick it up, undo puts it back, and everything off the allowlist " +
                  "is refused");

            foreach (var line in TweakDoor.ServerAudit()) Log("audit | " + line);
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static string OnDisk()
        {
            try { return CfgFile.GetValue(SmoothServerBridge.CfgPath, "Profiles", "Profile"); }
            catch (Exception e) { return "unreadable (" + e.Message + ")"; }
        }

        private static int Backups()
        {
            try
            {
                string path = SmoothServerBridge.CfgPath;
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return 0;
                return Directory.GetFiles(dir, Path.GetFileName(path) + ".bak-*").Length;
            }
            catch { return 0; }
        }

        private static void Fail(string why)
        {
            _failed = true;
            Log("FAILED - " + why);
        }

        private static void Log(string s)
        {
            NoVikingLeftBehindPlugin.Log.LogInfo("[Access][NetworkSelfTest] " + s);
        }
    }
}
