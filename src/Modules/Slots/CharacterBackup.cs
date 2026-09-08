using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NoVikingLeftBehind
{
    /// <summary>
    /// **The local safety net** - a plain copy of the character's own <c>.fch</c> file, taken at
    /// the two moments where the file is about to stop being the only copy of something.
    ///
    /// WHERE THE FILE IS. Vanilla resolves it in exactly one place:
    /// <c>PlayerProfile.GetPath()</c> -> <c>GetCharacterFolderPath(m_fileSource) + m_filename +
    /// ".fch"</c>, where <c>GetCharacterFolderPath</c> is <c>Utils.GetSaveDataPath(source)</c> plus
    /// <c>"/characters_local/"</c> for <see cref="FileHelpers.FileSource.Local"/> and
    /// <c>"/characters/"</c> for anything else (PlayerProfile.cs:641-654 in the 0.221.12 decompile).
    /// We ask the live profile for that same string rather than rebuilding it, so a game update
    /// that moves the folder moves our backups with it.
    ///
    /// CLOUD SAVES. When the profile's <c>m_fileSource</c> is not <c>Local</c>,
    /// <c>Utils.GetSaveDataPath</c> returns the empty string and the "path" is a cloud object name
    /// (<c>/characters/&lt;name&gt;.fch</c>) that no <c>System.IO</c> call can open. There is
    /// nothing to copy and nowhere sensible to put it, so we log one line and skip - never throw,
    /// never invent a path. (On PC 0.221.12 this cannot currently happen:
    /// <c>FileHelpers.CloudStorageSupported</c> is a hard <c>false</c>. The guard is for the
    /// consoles and for whatever a future build turns that into.) The same skip covers a profile
    /// whose file simply is not on disk yet - a brand-new character that has never been saved.
    ///
    /// The backups go in <c>&lt;characters_local&gt;\nvlb-backups\</c>, beside the characters
    /// rather than inside them, so Valheim's own "Manage saves" screen never lists them as
    /// playable characters and its auto-backup logic never touches them. Restoring one is a
    /// two-step a player can do with a file manager: rename it back to <c>&lt;name&gt;.fch</c> and
    /// drop it in <c>characters_local</c>.
    /// </summary>
    internal static class CharacterBackup
    {
        public const string FolderName = "nvlb-backups";

        /// <summary>The folder the backups live in, or null when it cannot be worked out.</summary>
        public static string Folder
        {
            get
            {
                try
                {
                    var chars = PlayerProfile.GetCharacterFolderPath(FileHelpers.FileSource.Local);
                    if (string.IsNullOrEmpty(chars)) return null;
                    return Path.Combine(chars, FolderName);
                }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[SafeSlots] could not resolve the " +
                                                            "characters folder: " + e.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// True when this character already has at least one NVLB backup on this machine - which
        /// is how "the first NVLB login of this character" is decided, with no extra state file to
        /// keep in step with the saves.
        /// </summary>
        public static bool HasAny(string profileFilename)
        {
            return Existing(profileFilename).Count > 0;
        }

        /// <summary>
        /// Copy the character's .fch. Returns the backup's full path, or null when there was
        /// nothing to copy (cloud save, no file yet) or the copy failed - every one of which is
        /// logged. NEVER throws: this runs inside a Harmony prefix on the load path.
        /// </summary>
        public static string Take(PlayerProfile profile, int keep, string why)
        {
            if (profile == null) return null;
            try
            {
                var name = profile.GetFilename();
                if (string.IsNullOrEmpty(name))
                {
                    NoVikingLeftBehindPlugin.Log.LogInfo("[SafeSlots] no character file name yet - " +
                                                          "no backup taken (" + why + ")");
                    return null;
                }

                if (profile.m_fileSource != FileHelpers.FileSource.Local)
                {
                    NoVikingLeftBehindPlugin.Log.LogInfo("[SafeSlots] '" + name + "' is a " +
                        profile.m_fileSource + " (cloud) save, so there is no local .fch to copy - " +
                        "skipping the character backup (" + why + "). Your extra-slot items are " +
                        "still kept in the save itself and in the server vault.");
                    return null;
                }

                var source = profile.GetPath();
                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                {
                    NoVikingLeftBehindPlugin.Log.LogInfo("[SafeSlots] '" + name + "' has no file on " +
                        "disk yet (" + (source ?? "null") + ") - no backup taken (" + why + ")");
                    return null;
                }

                var folder = Folder;
                if (string.IsNullOrEmpty(folder)) return null;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var dest = Path.Combine(folder, name + "-" + stamp + ".fch");
                if (File.Exists(dest)) return dest;   // same second, same character: already done

                File.Copy(source, dest);
                var size = new FileInfo(dest).Length;
                NoVikingLeftBehindPlugin.Log.LogWarning("[SafeSlots] backed up your character file (" +
                    why + "): " + dest + " (" + size + " bytes)");

                Prune(name, keep);
                return dest;
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogError("[SafeSlots] character backup failed (" + why +
                                                      "): " + e.Message);
                return null;
            }
        }

        /// <summary>This character's backups, newest first.</summary>
        public static List<string> Existing(string profileFilename)
        {
            var outp = new List<string>();
            try
            {
                var folder = Folder;
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return outp;
                if (string.IsNullOrEmpty(profileFilename)) return outp;

                // Matched by prefix rather than by a glob: a character name may contain characters
                // that mean something to a search pattern, and "Erik-2" must not match "Erik".
                var prefix = profileFilename + "-";
                var files = Directory.GetFiles(folder, "*.fch");
                for (int i = 0; i < files.Length; i++)
                {
                    var b = Path.GetFileName(files[i]);
                    if (b != null && b.StartsWith(prefix, StringComparison.Ordinal)) outp.Add(files[i]);
                }
                outp.Sort(StringComparer.Ordinal);
                outp.Reverse();   // the stamp sorts lexicographically, so this is newest first
            }
            catch (Exception e)
            {
                NoVikingLeftBehindPlugin.Log.LogWarning("[SafeSlots] could not list the backups: " + e.Message);
            }
            return outp;
        }

        private static void Prune(string profileFilename, int keep)
        {
            if (keep < 1) keep = 1;
            var have = Existing(profileFilename);
            for (int i = keep; i < have.Count; i++)
            {
                try
                {
                    File.Delete(have[i]);
                    NoVikingLeftBehindPlugin.Log.LogInfo("[SafeSlots] removed an old character " +
                                                          "backup: " + Path.GetFileName(have[i]));
                }
                catch (Exception e)
                {
                    NoVikingLeftBehindPlugin.Log.LogWarning("[SafeSlots] could not remove " +
                                                            have[i] + ": " + e.Message);
                }
            }
        }
    }
}
