using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Watches this plugin's own cfg file on disk and reloads it live, so edits made while the
    /// server is running take effect without a restart.
    ///
    /// Two detection paths feed the same debounced pipeline:
    ///  - a FileSystemWatcher, for platforms where it works.
    ///  - a last-write-time poll every ~500ms from <see cref="Pump"/>, which is the mechanism
    ///    that actually fires here: empirically, Unity's embedded Mono does not raise
    ///    FileSystemWatcher events for this BepInEx config directory under valheim_server's
    ///    -batchmode/-nographics headless player (confirmed by logging every Changed/Created/
    ///    Renamed callback across dozens of live `sed -i` edits on valheim-test: zero fired).
    ///    The poll is cheap (one stat call twice a second) and has no dependency on
    ///    inotify/FAM support inside the player, so it is the mechanism to rely on.
    ///
    /// Either path only marks a pending reload with a timestamp - the real work happens in
    /// <see cref="Pump"/>, called every frame from the main thread (Update()). Reload is
    /// debounced ~500ms after the last detected change (an edit can touch the file more than
    /// once, e.g. `sed -i` writes a temp file then renames it over the original), and
    /// Config.Reload() itself must run on the main thread: it raises SettingChanged for every
    /// changed entry, and module handlers touch game state.
    ///
    /// BepInEx can rewrite/normalise the file when SaveOnConfigSet is on, which would otherwise
    /// produce a second detected change for our own reload and loop forever. Reload() runs with
    /// SaveOnConfigSet forced off for the duration, the poll's last-known write time is updated
    /// immediately after reloading, and detected changes are additionally ignored for ~1s right
    /// after a reload completes.
    /// </summary>
    internal sealed class ConfigWatcher : IDisposable
    {
        private const double DebounceSeconds = 0.5;
        private const double PollIntervalSeconds = 0.5;
        private const double IgnoreAfterReloadSeconds = 1.0;

        private readonly ConfigFile _cfg;
        private readonly ManualLogSource _log;
        private readonly string _fileName;
        private readonly string _logPrefix;
        private FileSystemWatcher _fsw;

        private volatile bool _pending;
        private DateTime _lastEventUtc;
        private DateTime _lastReloadUtc = DateTime.MinValue;
        private DateTime _lastPollUtc = DateTime.MinValue;
        private DateTime _lastKnownWriteUtc = DateTime.MinValue;

        public ConfigWatcher(ConfigFile cfg, ManualLogSource log, string logPrefix = "[Config]")
        {
            _cfg = cfg;
            _log = log;
            _logPrefix = logPrefix;
            _fileName = Path.GetFileName(cfg.ConfigFilePath);
            _lastKnownWriteUtc = SafeGetLastWriteUtc();

            var dir = Path.GetDirectoryName(cfg.ConfigFilePath);
            if (string.IsNullOrEmpty(dir))
            {
                _log.LogWarning(_logPrefix + " watcher: could not resolve a directory for " + cfg.ConfigFilePath);
                return;
            }

            try
            {
                _fsw = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    IncludeSubdirectories = false
                };
                _fsw.Changed += OnFsEvent;
                _fsw.Created += OnFsEvent;
                _fsw.Renamed += OnFsRenamed;
                _fsw.Error += OnFsError;
                _fsw.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                _log.LogWarning(_logPrefix + " could not start FileSystemWatcher, relying on polling: " + e.Message);
                _fsw = null;
            }

            _log.LogInfo(_logPrefix + " watching " + cfg.ConfigFilePath + " for live edits");
        }

        private void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            if (!string.Equals(e.Name, _fileName, StringComparison.OrdinalIgnoreCase)) return;
            Schedule();
        }

        private void OnFsRenamed(object sender, RenamedEventArgs e)
        {
            // most editors (and `sed -i`) write a temp file, then rename it over the original -
            // the rename's *new* name is the one that matters.
            if (!string.Equals(e.Name, _fileName, StringComparison.OrdinalIgnoreCase)) return;
            Schedule();
        }

        // Note: on this BepInEx/Unity build (Unity's embedded Mono under valheim_server's
        // -batchmode/-nographics headless player), the above FileSystemWatcher callbacks were
        // never observed to fire across dozens of live edits during development, even with
        // unconditional logging on every Changed/Created/Renamed event. The poll in Pump() is
        // what actually detects changes here; the watcher is kept as a zero-cost best-effort
        // path in case a future runtime does support it.

        private void OnFsError(object sender, ErrorEventArgs e)
        {
            _log.LogWarning(_logPrefix + " watcher error (falling back to polling): " + e.GetException());
        }

        private void Schedule()
        {
            if ((DateTime.UtcNow - _lastReloadUtc).TotalSeconds < IgnoreAfterReloadSeconds) return;
            _pending = true;
            _lastEventUtc = DateTime.UtcNow;
        }

        private DateTime SafeGetLastWriteUtc()
        {
            try { return File.GetLastWriteTimeUtc(_cfg.ConfigFilePath); }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Call every frame from the main thread. Cheap when there is nothing to do.</summary>
        public void Pump()
        {
            if ((DateTime.UtcNow - _lastPollUtc).TotalSeconds >= PollIntervalSeconds)
            {
                _lastPollUtc = DateTime.UtcNow;
                var lwt = SafeGetLastWriteUtc();
                if (lwt != DateTime.MinValue && lwt != _lastKnownWriteUtc)
                {
                    _lastKnownWriteUtc = lwt;
                    Schedule();
                }
            }

            if (!_pending) return;
            if ((DateTime.UtcNow - _lastEventUtc).TotalSeconds < DebounceSeconds) return;
            _pending = false;

            try { DoReload(); }
            catch (Exception e) { _log.LogError(_logPrefix + " reload failed: " + e); }
        }

        private void DoReload()
        {
            var before = Snapshot();

            bool wasSaveOnSet = _cfg.SaveOnConfigSet;
            _cfg.SaveOnConfigSet = false;
            try { _cfg.Reload(); }
            finally { _cfg.SaveOnConfigSet = wasSaveOnSet; }

            _lastReloadUtc = DateTime.UtcNow;
            _lastKnownWriteUtc = SafeGetLastWriteUtc();

            var changes = Diff(before);
            _log.LogInfo(changes.Count == 0
                ? _logPrefix + " reloaded: no changes"
                : _logPrefix + " reloaded: " + string.Join(", ", changes.ToArray()));
        }

        private Dictionary<ConfigDefinition, string> Snapshot()
        {
            var map = new Dictionary<ConfigDefinition, string>();
            foreach (var kv in _cfg)
            {
                try { map[kv.Key] = kv.Value.GetSerializedValue(); }
                catch { /* best effort */ }
            }
            return map;
        }

        private List<string> Diff(Dictionary<ConfigDefinition, string> before)
        {
            var changes = new List<string>();
            foreach (var kv in _cfg)
            {
                string newVal;
                try { newVal = kv.Value.GetSerializedValue(); }
                catch { continue; }

                string oldVal;
                if (!before.TryGetValue(kv.Key, out oldVal)) oldVal = "?";
                if (oldVal != newVal)
                    changes.Add("[" + kv.Key.Section + "] " + kv.Key.Key + " " + oldVal + " -> " + newVal);
            }
            return changes;
        }

        public void Dispose()
        {
            if (_fsw == null) return;
            try
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Changed -= OnFsEvent;
                _fsw.Created -= OnFsEvent;
                _fsw.Renamed -= OnFsRenamed;
                _fsw.Error -= OnFsError;
                _fsw.Dispose();
            }
            catch { /* shutting down */ }
            _fsw = null;
        }
    }
}
