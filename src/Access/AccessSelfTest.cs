using System;
using System.Globalization;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// The headless proof that the tweak door works end to end, and the regression test that
    /// keeps it working. Runs only on a dedicated server, only when <c>[Access] SelfTest</c> is
    /// on, and only once per boot.
    ///
    /// It drives the real door with the server's own peer id as the caller - so it goes through
    /// the same permission check, the same value validation, the same rate limiter, the same cfg
    /// file write with a timestamped backup, and the same undo history a player would - and after
    /// each step it re-reads the value **straight off the disk** rather than from the ConfigEntry,
    /// which is the only way to prove the file (the thing cfg.py and the backups care about)
    /// really changed.
    ///
    /// There is a deliberate pause between the change and the undo so an operator watching from
    /// outside can see the new value with `cfg.py get` while it is in force, and see it restored
    /// afterwards. Nothing is left changed when it finishes.
    /// </summary>
    internal static class AccessSelfTest
    {
        /// <summary>Seconds the changed value is left in place before being undone.</summary>
        private const double HoldSeconds = 45.0;

        private static bool _armed;
        private static bool _done;
        private static DateTime _undoAt;
        private static SettingInfo _target;
        private static string _original;

        /// <summary>
        /// True once this test has stopped touching the door. The network self-test waits on it,
        /// so the two never interleave on the shared undo stack when both are switched on.
        /// </summary>
        public static bool Finished { get { return _done; } }

        public static void Arm()
        {
            if (_armed || _done) return;
            _armed = true;

            try { Phase1(); }
            catch (Exception e)
            {
                _done = true;
                Log("FAILED to start: " + e);
            }
        }

        /// <summary>Called every frame from the plugin's Update. Cheap when idle.</summary>
        public static void Pump()
        {
            if (!_armed || _done) return;
            if (DateTime.UtcNow < _undoAt) return;
            _done = true;

            try { Phase2(); }
            catch (Exception e) { Log("FAILED during undo: " + e); }
        }

        // ---- step 1: change it ------------------------------------------------------------------

        private static void Phase1()
        {
            _target = PickTarget();
            if (_target == null) { _done = true; Log("no suitable setting to exercise - skipped"); return; }

            string path = NoVikingLeftBehindPlugin.Cfg.ConfigFilePath;
            _original = CfgFile.GetValue(path, _target.Section, _target.Key);
            string proposed = Different(_target, _original);

            Log("target " + _target.Id + " (" + _target.TypeName + ", tier " + _target.Tier +
                (_target.HasRange ? ", range " + N(_target.Min) + ".." + N(_target.Max) : "") + ")");
            Log("before: file has " + _target.Key + " = " + _original +
                ", entry = " + _target.CurrentString);

            string message;
            bool ok = TweakDoor.ApplyOne(ZNet.GetUID(), _target.Section, _target.Key, proposed, out message);
            Log("door says: ok=" + ok + " \"" + message + "\"");
            if (!ok) { _done = true; Log("FAILED - the door refused a change the server itself made"); return; }

            string onDisk = CfgFile.GetValue(path, _target.Section, _target.Key);
            string inMemory = _target.CurrentString;
            bool pass = onDisk == proposed && inMemory == proposed;
            Log("after set: file has " + onDisk + ", entry = " + inMemory + "  -> " + (pass ? "OK" : "MISMATCH"));
            if (!pass) { _done = true; Log("FAILED - the file and the entry disagree"); return; }

            Log("holding for " + (int)HoldSeconds + "s so an operator can see it with " +
                "`cfg.py get nvlb " + _target.Section + " " + _target.Key + "`, then undoing");
            _undoAt = DateTime.UtcNow.AddSeconds(HoldSeconds);
        }

        // ---- step 2: put it back ------------------------------------------------------------------

        private static void Phase2()
        {
            string path = NoVikingLeftBehindPlugin.Cfg.ConfigFilePath;

            string message;
            bool ok = TweakDoor.Undo(ZNet.GetUID(), out message);
            Log("undo says: ok=" + ok + " \"" + message + "\"");

            string onDisk = CfgFile.GetValue(path, _target.Section, _target.Key);
            string inMemory = _target.CurrentString;
            bool pass = ok && onDisk == _original && inMemory == _original;
            Log("after undo: file has " + onDisk + ", entry = " + inMemory +
                " (was " + _original + ")  -> " + (pass ? "OK" : "MISMATCH"));

            Log(pass
                ? "PASSED - write, backup, live push and undo all work; the config is back where it started"
                : "FAILED - the value was not restored; check the .bak-* files next to the cfg");

            foreach (var line in TweakDoor.ServerAudit()) Log("audit | " + line);
        }

        // ---- helpers ----------------------------------------------------------------------------------

        /// <summary>
        /// A setting that is synced, live, open to everyone, numeric and ranged - i.e. the most
        /// ordinary thing a player could change. [Regrowth] RegrowDays by preference, because it
        /// is the example in the docs and is harmless to nudge for a minute.
        /// </summary>
        private static SettingInfo PickTarget()
        {
            var preferred = ConfigCatalog.Find("Regrowth", "RegrowDays");
            if (Suitable(preferred)) return preferred;
            foreach (var s in ConfigCatalog.All)
                if (Suitable(s)) return s;
            return null;
        }

        private static bool Suitable(SettingInfo s)
        {
            return s != null && !s.IsLocal && s.Live && !s.IsEnabledToggle &&
                   s.Tier == SettingTier.Everyone && s.HasRange &&
                   (s.TypeName == "float" || s.TypeName == "int");
        }

        /// <summary>A valid, in-range value that is definitely not the current one.</summary>
        private static string Different(SettingInfo s, string current)
        {
            double now;
            if (!double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out now))
                now = s.Min;

            double step = s.Step > 0 ? s.Step : 1;
            double candidate = now - step;
            if (candidate < s.Min) candidate = now + step;
            if (candidate > s.Max) candidate = s.Min;

            if (s.TypeName == "int")
                return ((long)Math.Round(candidate)).ToString(CultureInfo.InvariantCulture);
            return ((float)candidate).ToString("R", CultureInfo.InvariantCulture);
        }

        private static string N(double d)
        {
            return d == Math.Floor(d) && Math.Abs(d) < 1e15
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static void Log(string s)
        {
            NoVikingLeftBehindPlugin.Log.LogInfo("[Access][SelfTest] " + s);
        }
    }
}
