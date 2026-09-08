using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace NoVikingLeftBehind
{
    /// <summary>One stored copy of a character's extra-slot blob, as it sits in the vault file.</summary>
    internal sealed class VaultVersion
    {
        /// <summary>UTC, ISO-8601 with a trailing Z. The only clock the server trusts.</summary>
        public string utc = "";
        /// <summary>FNV-1a 64 of the blob string, lower-case hex. Cheap change detection.</summary>
        public string hash = "";
        /// <summary>How many items the blob holds, read out of its own header by the client.</summary>
        public int count;
        /// <summary>The blob string itself - byte for byte what <see cref="SlotBlob.Encode"/> made.</summary>
        public string blob = "";

        public DateTime WhenUtc
        {
            get
            {
                DateTime d;
                if (DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                                      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                    return d;
                return DateTime.UtcNow;
            }
        }
    }

    /// <summary>Everything the server keeps for one character.</summary>
    internal sealed class VaultFile
    {
        public string playerId = "";
        /// <summary>Last character name seen. Cosmetic only - the id is the key, so a rename is free.</summary>
        public string name = "";
        /// <summary>Newest FIRST. Index 1 in the console command is versions[0].</summary>
        public readonly List<VaultVersion> versions = new List<VaultVersion>();
    }

    /// <summary>
    /// **The server vault** - the second copy of every player's extra-slot items, held by the
    /// server so that losing a local <c>.fch</c> is not the end of the story.
    ///
    /// One file per CHARACTER, under <c>&lt;BepInEx config dir&gt;/nvlb/vault/&lt;playerId&gt;.json</c> -
    /// the same place and the same "full write to .tmp, then replace" discipline OreRegrowth uses
    /// for <c>regrowth.json</c>, so a server operator has exactly one directory to back up.
    ///
    /// **The key is <c>PlayerProfile.GetPlayerID()</c>** (the profile's own <c>m_playerID</c>,
    /// generated once by <c>Utils.GenerateUID()</c> when the character is created and written into
    /// the <c>.fch</c> ever after). It is per CHARACTER, not per account, so a player with three
    /// vikings gets three vaults; and it is stored separately from <c>m_playerName</c>, so renaming
    /// a character in the "Manage saves" menu cannot orphan its vault. The two alternatives were
    /// both wrong for this: a <c>ZDOID</c>'s <c>UserID</c> and the Steam host id are per ACCOUNT,
    /// so a player's second character would overwrite the first's vault.
    ///
    /// Nothing here knows about Valheim beyond the file system, which is what makes the whole
    /// store provable headlessly - see <see cref="SafeSlotsSelfTest"/>.
    /// </summary>
    internal static class SlotVault
    {
        /// <summary>Hard ceiling on one blob, before it ever reaches a file. 64 KB.</summary>
        public const int MaxBlobBytes = 64 * 1024;

        public static string Dir
        {
            get { return Path.Combine(Path.Combine(Paths.ConfigPath, "nvlb"), "vault"); }
        }

        public static string PathFor(string playerId)
        {
            return Path.Combine(Dir, SafeName(playerId) + ".json");
        }

        /// <summary>
        /// A player id is a signed long, so the only characters it can contain are digits and a
        /// leading minus - but it arrives over an RPC, so it is sanitised anyway. Anything else is
        /// replaced, and the result can never escape the vault directory.
        /// </summary>
        internal static string SafeName(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return "unknown";
            var sb = new StringBuilder(playerId.Length);
            for (int i = 0; i < playerId.Length && sb.Length < 64; i++)
            {
                char c = playerId[i];
                if (c == '-' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                    sb.Append(c);
                else sb.Append('_');
            }
            return sb.Length == 0 ? "unknown" : sb.ToString();
        }

        // ---- hashing --------------------------------------------------------------------------

        /// <summary>
        /// FNV-1a 64 over the blob's UTF-8 bytes, lower-case hex. Deliberately not
        /// <c>string.GetHashCode</c> (not stable across runtimes) and not MD5 (needs
        /// System.Security.Cryptography for something that is only ever compared to itself).
        /// Both halves compute it the same way, so "the client's hash equals the newest stored
        /// hash" means "nothing changed".
        /// </summary>
        public static string Hash(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return "0000000000000000";
            ulong h = 14695981039346656037UL;
            var bytes = Encoding.UTF8.GetBytes(blob);
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 1099511628211UL;
            }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// How many items a blob claims, read out of its own <c>"1|&lt;count&gt;|..."</c> header.
        /// The server never decodes a blob (it has no ObjectDB and no business looking inside a
        /// player's items), so the header is the only count it has - and it is the one the encoder
        /// wrote after skipping unsaveable items. -1 when the string is not a blob at all.
        /// </summary>
        public static int CountIn(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return -1;
            int bar = blob.IndexOf('|');
            if (bar <= 0) return -1;
            int bar2 = blob.IndexOf('|', bar + 1);
            if (bar2 < 0) return -1;
            int n;
            if (!int.TryParse(blob.Substring(bar + 1, bar2 - bar - 1), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out n)) return -1;
            return n;
        }

        // ---- reading ---------------------------------------------------------------------------

        /// <summary>Load one character's vault. Never throws; a missing or broken file reads empty.</summary>
        public static VaultFile Read(string playerId)
        {
            var vf = new VaultFile { playerId = playerId ?? "" };
            try
            {
                var path = PathFor(playerId);
                if (!File.Exists(path)) return vf;
                VaultJson.Read(File.ReadAllText(path), vf);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[SafeSlots] could not read the vault for " +
                                                      playerId + ": " + e.Message);
                vf.versions.Clear();
            }
            return vf;
        }

        /// <summary>Every playerId that has a vault file, and how many versions each holds.</summary>
        public static List<KeyValuePair<string, int>> All()
        {
            var outp = new List<KeyValuePair<string, int>>();
            try
            {
                if (!Directory.Exists(Dir)) return outp;
                var files = Directory.GetFiles(Dir, "*.json");
                Array.Sort(files, StringComparer.Ordinal);
                for (int i = 0; i < files.Length; i++)
                {
                    var id = Path.GetFileNameWithoutExtension(files[i]);
                    outp.Add(new KeyValuePair<string, int>(id, Read(id).versions.Count));
                }
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[SafeSlots] could not list the vault: " + e.Message);
            }
            return outp;
        }

        /// <summary>One line for the server boot log and nvlb.status.</summary>
        public static string Summary()
        {
            var all = All();
            int versions = 0;
            for (int i = 0; i < all.Count; i++) versions += all[i].Value;
            return "vault: " + all.Count + " players, " + versions + " versions";
        }

        // ---- writing ----------------------------------------------------------------------------

        /// <summary>What <see cref="Put"/> did, so the caller can log one honest line.</summary>
        internal enum PutResult
        {
            /// <summary>Stored as a new newest version.</summary>
            Stored,
            /// <summary>The newest stored version already has this hash - nothing written.</summary>
            Unchanged,
            /// <summary>Over <see cref="MaxBlobBytes"/> - refused, nothing written.</summary>
            TooBig,
            /// <summary>Not a blob, or no player id - refused.</summary>
            Rejected,
            /// <summary>The file could not be written. The log line says why.</summary>
            Failed
        }

        /// <summary>
        /// Store one blob as the newest version for <paramref name="playerId"/>, keeping at most
        /// <paramref name="keep"/> versions. Newest first, so the cap simply trims the tail.
        /// </summary>
        public static PutResult Put(string playerId, string playerName, string blob, int keep, out VaultFile file)
        {
            file = null;
            if (string.IsNullOrEmpty(playerId) || playerId == "0") return PutResult.Rejected;

            int bytes = string.IsNullOrEmpty(blob) ? 0 : Encoding.UTF8.GetByteCount(blob);
            if (bytes > MaxBlobBytes)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[SafeSlots] REFUSED a " + bytes +
                    " byte blob from " + Describe(playerId, playerName) + " - the vault's limit is " +
                    MaxBlobBytes + " bytes. Nothing was stored; the player's own save is untouched.");
                return PutResult.TooBig;
            }

            int count = CountIn(blob);
            if (count < 0) return PutResult.Rejected;

            var vf = Read(playerId);
            vf.playerId = playerId;
            if (!string.IsNullOrEmpty(playerName)) vf.name = playerName;

            string hash = Hash(blob);
            if (vf.versions.Count > 0 && vf.versions[0].hash == hash)
            {
                file = vf;
                return PutResult.Unchanged;
            }

            vf.versions.Insert(0, new VaultVersion
            {
                utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                hash = hash,
                count = count,
                blob = blob ?? ""
            });
            if (keep < 1) keep = 1;
            while (vf.versions.Count > keep) vf.versions.RemoveAt(vf.versions.Count - 1);

            file = vf;
            return Write(vf) ? PutResult.Stored : PutResult.Failed;
        }

        /// <summary>Full write to .tmp then replace, exactly as OreRegrowth writes its store.</summary>
        public static bool Write(VaultFile vf)
        {
            try
            {
                var path = PathFor(vf.playerId);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, VaultJson.Write(vf));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[SafeSlots] could not write the vault for " +
                                                      vf.playerId + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Remove one character's vault file. Only the self test uses this.</summary>
        public static void Delete(string playerId)
        {
            try
            {
                var path = PathFor(playerId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[SafeSlots] could not delete " +
                                                        PathFor(playerId) + ": " + e.Message);
            }
        }

        internal static string Describe(string playerId, string playerName)
        {
            return string.IsNullOrEmpty(playerName) ? playerId : (playerName + " (" + playerId + ")");
        }

        // ---- how long ago ---------------------------------------------------------------------------

        /// <summary>"3 minutes ago" / "2 days ago". Plain words, never a raw timestamp.</summary>
        public static string Ago(DateTime utc)
        {
            var span = DateTime.UtcNow - utc;
            double s = span.TotalSeconds;
            if (s < 0) return "just now";
            if (s < 90) return "just now";
            if (span.TotalMinutes < 90) return Plural((int)Math.Round(span.TotalMinutes), "minute") + " ago";
            if (span.TotalHours < 36) return Plural((int)Math.Round(span.TotalHours), "hour") + " ago";
            return Plural((int)Math.Round(span.TotalDays), "day") + " ago";
        }

        private static string Plural(int n, string unit)
        {
            return n + " " + unit + (n == 1 ? "" : "s");
        }
    }

    /// <summary>
    /// The vault file's reader/writer. Same reasoning as <see cref="RegrowthJson"/>: one known
    /// shape, no JsonUtility (not referenced) and no Newtonsoft (not guaranteed present), so a
    /// purpose-built ~120 lines is smaller than either dependency and cannot surprise us.
    ///
    /// Shape:
    /// <code>
    /// { "playerId": "-8123...", "name": "Erik", "versions": [
    ///     { "utc": "2026-09-08T16:00:00Z", "hash": "9f1c...", "count": 6, "blob": "1|6|BASE64" },
    ///     ... newest first ...
    /// ] }
    /// </code>
    /// A malformed version object is skipped, not fatal.
    /// </summary>
    internal static class VaultJson
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Write(VaultFile vf)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"playerId\": \"").Append(Escape(vf.playerId)).Append("\",\n");
            sb.Append("  \"name\": \"").Append(Escape(vf.name)).Append("\",\n");
            sb.Append("  \"versions\": [\n");
            for (int i = 0; i < vf.versions.Count; i++)
            {
                var v = vf.versions[i];
                sb.Append("    {\"utc\":\"").Append(Escape(v.utc)).Append('"');
                sb.Append(",\"hash\":\"").Append(Escape(v.hash)).Append('"');
                sb.Append(",\"count\":").Append(v.count.ToString(Inv));
                sb.Append(",\"blob\":\"").Append(Escape(v.blob)).Append('"');
                sb.Append('}');
                if (i < vf.versions.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        public static void Read(string json, VaultFile into)
        {
            if (into == null || string.IsNullOrEmpty(json)) return;
            into.versions.Clear();

            // The wrapper's own two string fields are read from the head of the document, before
            // the first version object, so a "name" inside a blob can never be mistaken for them.
            int firstObj = IndexOfVersionsArray(json);
            var head = firstObj < 0 ? json : json.Substring(0, firstObj);
            var id = Str(head, "playerId");
            if (!string.IsNullOrEmpty(id)) into.playerId = id;
            into.name = Str(head, "name");

            int i = firstObj < 0 ? json.Length : firstObj;
            while (i < json.Length)
            {
                int open = json.IndexOf('{', i);
                if (open < 0) break;
                int close = MatchBrace(json, open);
                if (close < 0) break;
                var body = json.Substring(open + 1, close - open - 1);
                var v = ReadVersion(body);
                if (v != null) into.versions.Add(v);
                i = close + 1;
            }
        }

        private static int IndexOfVersionsArray(string json)
        {
            int k = json.IndexOf("\"versions\"", StringComparison.Ordinal);
            if (k < 0) return -1;
            int b = json.IndexOf('[', k);
            return b < 0 ? -1 : b;
        }

        private static VaultVersion ReadVersion(string body)
        {
            try
            {
                var v = new VaultVersion
                {
                    utc = Str(body, "utc"),
                    hash = Str(body, "hash"),
                    count = (int)Num(body, "count"),
                    blob = Str(body, "blob")
                };
                return string.IsNullOrEmpty(v.blob) ? null : v;
            }
            catch { return null; }
        }

        private static int MatchBrace(string s, int open)
        {
            int depth = 0; bool inStr = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        private static float Num(string body, string key)
        {
            int p = ValueStart(body, key);
            if (p < 0) return 0f;
            int end = p;
            while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '-' || body[end] == '+' ||
                                         body[end] == '.' || body[end] == 'e' || body[end] == 'E')) end++;
            float f;
            return float.TryParse(body.Substring(p, end - p), NumberStyles.Float, Inv, out f) ? f : 0f;
        }

        private static string Str(string body, string key)
        {
            int p = ValueStart(body, key);
            if (p < 0 || p >= body.Length || body[p] != '"') return "";
            var sb = new StringBuilder();
            for (int i = p + 1; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    char n = body[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else sb.Append(n);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static int ValueStart(string body, string key)
        {
            int k = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return -1;
            int c = body.IndexOf(':', k + key.Length + 2);
            if (c < 0) return -1;
            c++;
            while (c < body.Length && char.IsWhiteSpace(body[c])) c++;
            return c;
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
