using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// Line-level edits to the plugin's own BepInEx cfg file, with the same timestamped backups
    /// <c>/opt/modlab/tools/cfg.py</c> makes.
    ///
    /// Why edit the file rather than call <c>ConfigFile.Save()</c>: the cfg file on disk is the
    /// single source of truth for this mod - cfg.py reads and writes it, the cut-over seeds it,
    /// the backups are the rollback - and the existing <see cref="ConfigWatcher"/> already turns a
    /// file change into <c>Config.Reload()</c> -> <c>SettingChanged</c> -> ServerSync push. Writing
    /// the one line keeps every byte of the rest of the file (comments, ordering, orphaned keys)
    /// exactly as cfg.py left it, so the two tools cannot drift apart.
    ///
    /// Backup naming is deliberately identical to cfg.py's <c>make_backup()</c>:
    /// <c>&lt;file&gt;.bak-YYYYMMDD-HHMMSS</c>, disambiguated with <c>-N</c>, pruned to the newest 20.
    /// </summary>
    internal static class CfgFile
    {
        private const int BackupKeep = 20;

        /// <summary>Backs the file up, then rewrites the one <c>Key = value</c> line in-place.</summary>
        /// <returns>The backup path.</returns>
        public static string SetValue(string path, string section, string key, string newValue)
        {
            var edits = new List<KeyValuePair<string, string>>();
            edits.Add(new KeyValuePair<string, string>(section + "." + key, newValue));
            return SetValues(path, edits);
        }

        /// <summary>
        /// Apply several edits under ONE backup and ONE write - used by "reset this module to
        /// defaults", so a module with twenty settings does not produce twenty backup files.
        /// Keys are "Section.Key".
        /// </summary>
        public static string SetValues(string path, IList<KeyValuePair<string, string>> edits)
        {
            if (edits == null || edits.Count == 0) return null;

            var lines = new List<string>(File.ReadAllLines(path, new UTF8Encoding(false)));
            int applied = 0;

            foreach (var edit in edits)
            {
                int dot = edit.Key.IndexOf('.');
                if (dot <= 0) continue;
                string section = edit.Key.Substring(0, dot);
                string key = edit.Key.Substring(dot + 1);

                int idx = FindLine(lines, section, key);
                if (idx < 0)
                    throw new Exception("[" + section + "] " + key + " is not in " + Path.GetFileName(path));

                lines[idx] = key + " = " + edit.Value;
                applied++;
            }

            if (applied == 0) return null;

            string backup = MakeBackup(path);

            // Write via a sibling temp file so a crash mid-write cannot leave a half file where
            // the server (and cfg.py) expect a whole one.
            string tmp = path + ".nvlbtmp";
            File.WriteAllText(tmp, string.Join("\n", lines.ToArray()) + "\n", new UTF8Encoding(false));
            File.Copy(tmp, path, true);
            try { File.Delete(tmp); } catch { /* best effort */ }

            return backup;
        }

        /// <summary>
        /// Read a value straight off the disk, bypassing every in-memory copy. Used by the
        /// self-test to prove the file itself changed, not just the ConfigEntry.
        /// </summary>
        public static string GetValue(string path, string section, string key)
        {
            var lines = File.ReadAllLines(path, new UTF8Encoding(false));
            int idx = FindLine(lines, section, key);
            if (idx < 0) return null;
            int eq = lines[idx].IndexOf('=');
            return eq < 0 ? null : lines[idx].Substring(eq + 1).Trim();
        }

        /// <summary>Index of the <c>Key = ...</c> line inside <c>[section]</c>, or -1.</summary>
        private static int FindLine(IList<string> lines, string section, string key)
        {
            bool inSection = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string raw = lines[i];
                string t = raw.Trim();
                if (t.Length == 0 || t[0] == '#') continue;

                if (t[0] == '[' && t.EndsWith("]", StringComparison.Ordinal))
                {
                    inSection = string.Equals(t.Substring(1, t.Length - 2).Trim(), section,
                                              StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection) continue;

                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        // ---- backups (mirrors cfg.py) ----------------------------------------------------------

        public static string MakeBackup(string path)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string backup = path + ".bak-" + stamp;
            int n = 2;
            while (File.Exists(backup)) backup = path + ".bak-" + stamp + "-" + (n++).ToString(CultureInfo.InvariantCulture);
            File.Copy(path, backup, false);
            Prune(path);
            return backup;
        }

        private static void Prune(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return;
                var found = Directory.GetFiles(dir, Path.GetFileName(path) + ".bak-*");
                if (found.Length <= BackupKeep) return;
                Array.Sort(found, StringComparer.Ordinal);   // the stamp sorts chronologically
                for (int i = 0; i < found.Length - BackupKeep; i++)
                {
                    try { File.Delete(found[i]); } catch { /* best effort */ }
                }
            }
            catch { /* pruning is housekeeping, never fatal */ }
        }
    }
}
