using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// Util.Debug / WriteDebugLog / OpenLockFile (Game/Game/Util.cs): the log
    /// line format, the 1024-char flush threshold, the 768000-byte rotation,
    /// the client/server file names and the lock file. Util keeps a static
    /// buffer and IsServer flag and everything is relative to the cwd, so the
    /// class is serial.
    /// </summary>
    [Collection(ToolsConfigCollection.Name)]
    public class DebugLogTests
    {
        private static readonly Regex lineFormat = new Regex(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] (.*)$");

        /// <summary>Flushes whatever earlier tests left in the static buffer into <paramref name="dir"/> and removes it.</summary>
        private static void Drain(string dir)
        {
            Util.WriteDebugLog();
            foreach (string f in new string[] { Path.Combine(dir, "debug_client.log"), Path.Combine(dir, "Server", "debug_server.log") })
                if (File.Exists(f))
                    File.Delete(f);
        }

        private static string BufferText()
        {
            return ((StringBuilder)Reflect.GetStatic(typeof(Util), "debug")).ToString();
        }

        // TOOLS-11
        [Fact]
        public void Client_log_lines_are_timestamped_ascii_and_buffered_until_flushed()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                bool wasServer = Util.IsServer;
                Util.IsServer = false;
                try
                {
                    Drain(dir.Path);
                    string log = dir.File("debug_client.log");

                    Util.Debug("hello");
                    Assert.False(File.Exists(log), "nothing is written before the buffer exceeds 1024 chars or WriteDebugLog");
                    Assert.EndsWith("] hello\n", BufferText());

                    Util.WriteDebugLog();
                    Assert.True(File.Exists(log));
                    Assert.Equal("", BufferText());
                    byte[] raw = File.ReadAllBytes(log);
                    Assert.Equal((byte)'\n', raw[raw.Length - 1]);
                    string text = Encoding.ASCII.GetString(raw);
                    string[] lines = text.TrimEnd('\n').Split('\n');
                    Assert.Single(lines);
                    Match m = lineFormat.Match(lines[0]);
                    Assert.True(m.Success, "line: " + lines[0]);
                    Assert.Equal("hello", m.Groups[1].Value);
                    Assert.Equal(Path.Combine(".", "debug_client.log"), Util.debugFileName);

                    // Non-ASCII is not preserved (Encoding.ASCII writes '?').
                    Util.Debug("café");
                    Util.WriteDebugLog();
                    Assert.Contains("] caf?\n", Encoding.ASCII.GetString(File.ReadAllBytes(log)));
                }
                finally
                {
                    Util.IsServer = wasServer;
                }
            }
        }

        // TOOLS-11
        [Fact]
        public void Buffer_over_1024_chars_flushes_by_itself()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                bool wasServer = Util.IsServer;
                Util.IsServer = false;
                try
                {
                    Drain(dir.Path);
                    string log = dir.File("debug_client.log");
                    // Each line is "[yyyy-MM-dd HH:mm:ss] " (22 chars) + 40 + "\n" = 63 chars.
                    string payload = new string('x', 40);
                    int n = 0;
                    while (BufferText().Length + 63 <= 1024)
                    {
                        Util.Debug(payload);
                        n++;
                        Assert.False(File.Exists(log), "flushed early after " + n + " lines (" + BufferText().Length + " chars)");
                    }
                    Util.Debug(payload);
                    n++;
                    Assert.True(File.Exists(log), "the line that pushes the buffer past 1024 chars writes the file");
                    Assert.Equal("", BufferText());
                    Assert.Equal(n, File.ReadAllText(log).TrimEnd('\n').Split('\n').Length);
                }
                finally
                {
                    Util.IsServer = wasServer;
                }
            }
        }

        // TOOLS-11
        [Fact]
        public void Log_larger_than_768000_bytes_is_deleted_before_appending()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                bool wasServer = Util.IsServer;
                Util.IsServer = false;
                try
                {
                    Drain(dir.Path);
                    string log = dir.File("debug_client.log");
                    File.WriteAllBytes(log, new byte[768001]);
                    Util.Debug("after rotation");
                    Util.WriteDebugLog();
                    string text = File.ReadAllText(log);
                    string[] lines = text.TrimEnd('\n').Split('\n');
                    Assert.Single(lines);
                    Assert.EndsWith("] after rotation", lines[0]);
                    Assert.True(new FileInfo(log).Length < 100);

                    // Exactly 768000 bytes is kept (the check is strictly greater).
                    File.WriteAllBytes(log, new byte[768000]);
                    Util.Debug("kept");
                    Util.WriteDebugLog();
                    Assert.True(new FileInfo(log).Length > 768000);
                }
                finally
                {
                    Util.IsServer = wasServer;
                }
            }
        }

        // TOOLS-11
        [Fact]
        public void Server_mode_writes_Server_debug_server_log_and_creates_the_folder()
        {
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                bool wasServer = Util.IsServer;
                Util.IsServer = false;
                try
                {
                    Drain(dir.Path);
                    Util.IsServer = true;
                    Assert.False(Directory.Exists(dir.File("Server")));
                    string output;
                    using (ConsoleCapture c = new ConsoleCapture())
                    {
                        Util.Debug("server hello");
                        output = c.Text;
                    }
                    Assert.Contains("] server hello", output);   // the server also echoes to the console
                    Util.WriteDebugLog();
                    string log = Path.Combine(dir.Path, "Server", "debug_server.log");
                    Assert.True(File.Exists(log));
                    Assert.False(File.Exists(dir.File("debug_client.log")));
                    Assert.EndsWith("] server hello\n", File.ReadAllText(log));
                    Assert.Equal(Path.Combine(".", "Server", "debug_server.log"), Util.debugFileName);
                }
                finally
                {
                    Util.IsServer = wasServer;
                }
            }
        }

        // TOOLS-11: the lock file is exclusive across processes and its failure is swallowed
        [Fact]
        public void Lock_file_held_by_a_running_server_makes_OpenLockFile_a_silent_no_op()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                string lockPath = Path.Combine(rt.Root, "lock");
                using (ServerProcess s = new ServerProcess(rt))
                {
                    Assert.True(File.Exists(lockPath), "Program.Main calls Util.OpenLockFile in the cwd");
                    using (new CwdScope(rt.Root))
                    {
                        Util.OpenLockFile();
                        Assert.Null(Reflect.GetStatic(typeof(Util), "lockFile"));   // FileShare.None held by the server: swallowed
                        Util.CloseLockFile();                                       // null lockFile: also swallowed
                    }
                }
                // Once the holder is gone the file is ours.
                Assert.True(Poll.Until(() => { try { File.Delete(lockPath); return !File.Exists(lockPath); } catch (Exception) { return false; } }, TimeSpan.FromSeconds(5)));

                using (new CwdScope(rt.Root))
                {
                    Util.OpenLockFile();
                    FileStream held = (FileStream)Reflect.GetStatic(typeof(Util), "lockFile");
                    Assert.NotNull(held);
                    Assert.True(File.Exists(lockPath));
                    // A second opener in this process is refused the same way (FileShare.None).
                    Assert.ThrowsAny<IOException>(() => File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None));
                    Util.CloseLockFile();
                    Reflect.SetStatic(typeof(Util), "lockFile", null);
                }
            }
        }
    }
}
