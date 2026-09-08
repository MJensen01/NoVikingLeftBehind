using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Headless proof for the picker infrastructure (<see cref="PickerSpec"/> /
    /// <c>PickerCandidates</c>) that backs every "tick items off a list of what is in the world"
    /// setting in the mod. <c>PickerCandidates</c> only reads ObjectDB and ZNetScene - both of
    /// which a dedicated server has fully populated once <c>ZoneSystem.Start</c> has run - so
    /// unlike a client-only UI module this one is provable, and cheaply, by a server boot alone.
    ///
    /// Two promises are checked, matching what <see cref="PickerSpec"/>'s own doc comment
    /// describes as the contract:
    ///   1. every provider (<c>PickerSource</c>) any setting actually uses resolves at least some
    ///      candidates, and
    ///   2. every picker-backed setting's CURRENT stored string survives Parse() then Serialise()
    ///      byte-identical - the round trip the picker UI depends on to never silently rewrite a
    ///      value it merely displayed.
    ///
    /// With [PickerSelfTest] SelfTest = true (machine-local, default false, never synced) this
    /// runs once, after ZoneSystem.Start has populated ObjectDB and ZNetScene, and writes the
    /// answers to the log. It reads config and the picker tables only - it changes no config and
    /// no game state.
    ///
    /// Dedicated-server-only: <see cref="Side"/> is <see cref="ModuleSide.Server"/>, so on a
    /// player's client this module is never configured, never patched, and none of this ever runs.
    /// </summary>
    internal sealed class PickerSelfTestModule : FeatureModule
    {
        public override string Name => "PickerSelfTest";
        public override ModuleSide Side => ModuleSide.Server;
        public override string Section => "PickerSelfTest";
        public override string Theme => "Server";
        public override string Hint => "Logs a one-time diagnostic self-test for the item/prefab pickers";

        protected override Opt EnabledOpt => base.EnabledOpt.Admin();

        private static ConfigEntry<bool> _selfTest;
        private static bool _ran;
        private static int _pass, _fail;

        protected override void Bind()
        {
            _selfTest = BindLocal("SelfTest", false,
                "Diagnostic. Once per world load, log how many candidates each picker provider " +
                "resolves and round-trip every picker-backed setting's current stored value " +
                "through Parse() and Serialise(), asserting the result is byte-identical to what " +
                "is on disk. Machine-local and never synced, so turning it on for a server boot " +
                "affects no client. Leave it false in normal use.",
                Opt.B("Log a one-time diagnostic self-test of the item pickers at world load").Admin());
        }

        protected override void ApplyPatches()
        {
            var start = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (start == null) throw new Exception("ZoneSystem.Start() not found");
            Harmony.Patch(start, postfix: new HarmonyMethod(typeof(PickerSelfTestModule), nameof(WorldReady)));
        }

        private static void WorldReady()
        {
            if (_ran || _selfTest == null || !_selfTest.Value) return;
            _ran = true;
            _pass = _fail = 0;

            Log.LogInfo("[PickerSelfTest] --- begin ---");
            try
            {
                Log.LogInfo("[PickerSelfTest] PickerCandidates.Available = " + PickerCandidates.Available);
                ProviderCounts();
                var rawFindings = RoundTripAll();
                Coverage(rawFindings);
            }
            catch (Exception e)
            {
                _fail++;
                Log.LogError("[PickerSelfTest] threw: " + e);
            }

            Log.LogInfo("[PickerSelfTest] --- end --- " + (_pass + _fail) + " settings, " +
                        _pass + " round-trip PASS, " + _fail + " FAIL");
        }

        // ---- part 1: provider counts ---------------------------------------------------------

        /// <summary>
        /// For each distinct PickerSource actually used by a setting in the catalog, how many
        /// candidates PickerCandidates.For() resolves and a sample of the first three display
        /// names. One setting's PickerSpec stands in for the source when more than one setting
        /// shares it - the count is per-provider, not per-setting.
        /// </summary>
        private static void ProviderCounts()
        {
            var bySource = new Dictionary<PickerSource, PickerSpec>();
            foreach (var s in ConfigCatalog.All)
            {
                if (s.Picker == null) continue;
                if (!bySource.ContainsKey(s.Picker.Source)) bySource[s.Picker.Source] = s.Picker;
            }

            Log.LogInfo("[PickerSelfTest] " + bySource.Count + " distinct picker provider(s) in use");

            foreach (var kv in bySource)
            {
                var source = kv.Key;
                var spec = kv.Value;
                try
                {
                    var candidates = PickerCandidates.For(spec);
                    int n = candidates != null ? candidates.Count : 0;

                    var sb = new StringBuilder();
                    sb.Append("[PickerSelfTest] provider ").Append(source).Append(": ").Append(n).Append(" candidate(s)");
                    if (n > 0)
                    {
                        sb.Append(", sample: ");
                        int shown = Math.Min(3, n);
                        for (int i = 0; i < shown; i++)
                        {
                            if (i > 0) sb.Append(", ");
                            sb.Append(candidates[i] != null ? candidates[i].Display : "?");
                        }
                    }
                    Log.LogInfo(sb.ToString());
                }
                catch (Exception e)
                {
                    Log.LogError("[PickerSelfTest] provider " + source + " threw: " + e);
                }
            }
        }

        // ---- part 2: round trip every picker-backed setting -------------------------------------

        /// <summary>
        /// For every setting in the catalog whose Picker is non-null: Parse() its current stored
        /// string, Serialise() the result straight back, and assert the output is byte-identical
        /// to the input. One try/catch per setting, so one broken parser cannot hide the result
        /// of every other setting. Returns the id/Raw text of every entry the parser could not
        /// fully understand, for the coverage section that follows.
        /// </summary>
        private static List<string> RoundTripAll()
        {
            var rawFindings = new List<string>();

            foreach (var s in ConfigCatalog.All)
            {
                if (s.Picker == null) continue;
                try
                {
                    string input = s.CurrentString;
                    var entries = PickerCandidates.Parse(s.Picker, input);
                    string output = PickerCandidates.Serialise(s.Picker, entries);
                    int count = entries != null ? entries.Count : 0;

                    bool same = string.Equals(input, output, StringComparison.Ordinal);
                    Check(same, s.Id + ": " + count + " entry/entries");
                    if (!same)
                    {
                        Log.LogError("[PickerSelfTest]   input : " + Escape(input));
                        Log.LogError("[PickerSelfTest]   output: " + Escape(output));
                    }

                    if (entries != null)
                    {
                        foreach (var e in entries)
                            if (e != null && e.Raw != null)
                                rawFindings.Add(s.Id + ": " + Escape(e.Raw));
                    }
                }
                catch (Exception e)
                {
                    _fail++;
                    Log.LogError("[PickerSelfTest] roundtrip " + s.Id + " threw: " + e);
                }
            }

            return rawFindings;
        }

        private static void Check(bool ok, string what)
        {
            if (ok) { _pass++; Log.LogInfo("[PickerSelfTest] roundtrip PASS  " + what); }
            else { _fail++; Log.LogError("[PickerSelfTest] roundtrip FAIL  " + what); }
        }

        // ---- part 3: coverage ------------------------------------------------------------------

        private static void Coverage(List<string> rawFindings)
        {
            int total = ConfigCatalog.All.Count;
            int pickered = _pass + _fail;
            Log.LogInfo("[PickerSelfTest] coverage: " + pickered + " of " + total + " setting(s) carry a picker");

            if (rawFindings.Count == 0)
            {
                Log.LogInfo("[PickerSelfTest] no entry came back with a non-null Raw - the parser understood every entry in every stored value");
            }
            else
            {
                Log.LogInfo("[PickerSelfTest] " + rawFindings.Count +
                            " entry/entries the parser could not resolve against this world (Raw != null), kept and flagged:");
                foreach (var line in rawFindings) Log.LogInfo("[PickerSelfTest]   " + line);
            }
        }

        // ---- helpers ------------------------------------------------------------------------

        /// <summary>
        /// Make control characters and trailing/leading whitespace visible in a log line: escapes
        /// \r \n \t, wraps the whole string in brackets, and appends its length so a difference of
        /// one trailing space is unmistakable even when it renders invisibly in a terminal.
        /// </summary>
        private static string Escape(string s)
        {
            if (s == null) return "<null>";

            var sb = new StringBuilder();
            sb.Append('[');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append("] (").Append(s.Length).Append(" chars)");
            return sb.ToString();
        }

        public override string StatusDetail()
        {
            return "SelfTest=" + (_selfTest != null && _selfTest.Value) +
                   (_ran ? " (ran: " + _pass + " pass, " + _fail + " fail)" : "");
        }
    }
}
