using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// A private copy of the shipped runtime directory (<c>Test/</c>) in a
    /// unique temp folder. Every test that starts a server, loads a map or
    /// writes settings works inside one of these so <c>Test/</c> itself is
    /// never touched. Copies Content/, Maps/, Server/ (settings.txt, ops.txt,
    /// banned.txt), settings.xml, controls.xml and steam_appid.txt; never the
    /// shipped .exe files, logs or the lock file.
    /// Dispose deletes the folder.
    /// </summary>
    public sealed class ScratchRuntime : IDisposable
    {
        private static readonly HashSet<string> skipExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".log", ".pdb", ".dll" };
        private static readonly HashSet<string> skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "lock", "debug_client.log", "debug_server.log" };

        /// <summary>Absolute path of the scratch runtime directory.</summary>
        public string Root { get; private set; }

        public string MapsDir { get { return Path.Combine(Root, "Maps"); } }
        public string ServerDir { get { return Path.Combine(Root, "Server"); } }
        public string ContentDir { get { return Path.Combine(Root, "Content"); } }
        public string ServerSettingsPath { get { return Path.Combine(ServerDir, "settings.txt"); } }
        public string ServerLogPath { get { return Path.Combine(ServerDir, "debug_server.log"); } }
        public string ClientLogPath { get { return Path.Combine(Root, "debug_client.log"); } }

        /// <summary>Copies Test/ (from <see cref="Repo.RuntimeDir"/>) into a fresh temp directory.</summary>
        public ScratchRuntime() : this(Repo.RuntimeDir)
        {
        }

        /// <summary>Copies <paramref name="source"/> (a directory shaped like Test/) into a fresh temp directory.</summary>
        public ScratchRuntime(string source)
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "vexillum-acceptance");
            Directory.CreateDirectory(baseDir);
            Root = Path.Combine(baseDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Root);
            CopyTree(source, Root);
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                string name = Path.GetFileName(file);
                if (skipNames.Contains(name) || skipExtensions.Contains(Path.GetExtension(name)))
                    continue;
                File.Copy(file, Path.Combine(to, name), true);
            }
            foreach (string dir in Directory.GetDirectories(from))
            {
                string name = Path.GetFileName(dir);
                if (name == "bin" || name == "obj")
                    continue;
                CopyTree(dir, Path.Combine(to, name));
            }
        }

        /// <summary>Path of Maps/&lt;name&gt;.map in this runtime.</summary>
        public string MapPath(string mapName)
        {
            return Path.Combine(MapsDir, mapName + ".map");
        }

        /// <summary>Names of the maps present in Maps/ (file stems, sorted).</summary>
        public List<string> MapNames()
        {
            List<string> names = new List<string>();
            foreach (string f in Directory.GetFiles(MapsDir, "*.map"))
                names.Add(Path.GetFileNameWithoutExtension(f));
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        /// <summary>Deletes Maps/&lt;name&gt;.map so a client has to download it (packets 220-222).</summary>
        public void RemoveMap(string mapName)
        {
            string p = MapPath(mapName);
            if (File.Exists(p))
                File.Delete(p);
        }

        /// <summary>
        /// Sets one key of Server/settings.txt ("maps", "maxbots", "maxplayers",
        /// "maxcaptures", "respawntime", "verifynames", "public", "name",
        /// "weapons", "port"). The value is written verbatim after the key
        /// (space separated, as ParseServerConfig reads it); a missing key is
        /// appended. Comment lines are preserved.
        /// </summary>
        public void SetServerSetting(string key, string value)
        {
            List<string> lines = new List<string>(File.Exists(ServerSettingsPath)
                ? File.ReadAllText(ServerSettingsPath).Split('\n') : new string[0]);
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.StartsWith("#") || line.Trim().Length == 0)
                    continue;
                if (line.Split(' ')[0] == key)
                {
                    lines[i] = key + " " + value;
                    found = true;
                }
            }
            if (!found)
            {
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                    lines.RemoveAt(lines.Count - 1);
                lines.Add(key + " " + value);
            }
            File.WriteAllText(ServerSettingsPath, string.Join("\n", lines), Encoding.ASCII);
        }

        /// <summary>Reads one key of Server/settings.txt (the text after the key), or null.</summary>
        public string GetServerSetting(string key)
        {
            if (!File.Exists(ServerSettingsPath))
                return null;
            foreach (string raw in File.ReadAllText(ServerSettingsPath).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith("#"))
                    continue;
                int sp = line.IndexOf(' ');
                string k = sp < 0 ? line : line.Substring(0, sp);
                if (k == key)
                    return sp < 0 ? "" : line.Substring(sp + 1);
            }
            return null;
        }

        /// <summary>Overwrites Server/ops.txt or Server/banned.txt (one name per line).</summary>
        public void SetPlayerList(string list, params string[] names)
        {
            File.WriteAllText(Path.Combine(ServerDir, list + ".txt"), string.Join("\n", names) + (names.Length > 0 ? "\n" : ""), Encoding.ASCII);
        }

        /// <summary>Contents of Server/debug_server.log, or "" when the server has not flushed it yet.</summary>
        public string ReadServerLog()
        {
            return ReadIfExists(ServerLogPath);
        }

        /// <summary>Contents of debug_client.log, or "".</summary>
        public string ReadClientLog()
        {
            return ReadIfExists(ClientLogPath);
        }

        private static string ReadIfExists(string path)
        {
            if (!File.Exists(path))
                return "";
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader sr = new StreamReader(fs))
                return sr.ReadToEnd();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, true);
            }
            catch (Exception)
            {
                // A process still holding a log open: leave the folder for the OS temp cleaner.
            }
        }

        public override string ToString()
        {
            return Root;
        }
    }
}
