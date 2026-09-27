using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Vexillum.Port.Master;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// The port's launchers (Server/PortProgram.cs, ZombieSurvival/PortProgram.cs):
    /// argument parsing, the Server/settings.txt rewrite, and the packet-255
    /// status probe (Shims/MasterServer ServerProbe) against a real server.
    /// One server per class (ServerFixture); the extra servers below live in
    /// their own scratch runtimes.
    /// </summary>
    public class ServerLauncherTests : IClassFixture<ServerFixture>
    {
        private readonly ServerFixture fx;

        public ServerLauncherTests(ServerFixture fx)
        {
            this.fx = fx;
        }

        /// <summary>Runs an executable with arguments, captures stdout/stderr and the exit code (kills after the timeout).</summary>
        private static int Run(string exe, string[] args, string cwd, out string stdout, out string stderr, int timeoutMs = 30000)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            foreach (string a in args)
                psi.ArgumentList.Add(a);
            psi.WorkingDirectory = cwd;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.Environment["VEXILLUM_MASTER"] = "off";
            psi.Environment["VEXILLUM_LAN"] = "off";
            using (Process p = Process.Start(psi))
            {
                System.Threading.Tasks.Task<string> o = p.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch (Exception) { }
                    p.WaitForExit(5000);
                    stdout = o.Result;
                    stderr = e.Result;
                    throw new TimeoutException(Path.GetFileName(exe) + " " + string.Join(" ", args) + " did not exit within " + timeoutMs + " ms\n" + stdout + stderr);
                }
                stdout = o.Result;
                stderr = e.Result;
                return p.ExitCode;
            }
        }

        // TOOLS-05: the fixture's server was started with --port <free>; only the port line changed
        [Fact]
        public void Server_port_option_rewrites_only_the_port_line_of_settings_txt()
        {
            string shipped = File.ReadAllText(Path.Combine(Repo.RuntimeDir, "Server", "settings.txt"));
            string rewritten = File.ReadAllText(fx.Runtime.ServerSettingsPath);

            Assert.NotEqual(24224, fx.Server.Port);
            Assert.Equal(shipped.Replace("port 24224", "port " + fx.Server.Port), rewritten);
            Assert.Equal(fx.Server.Port.ToString(), fx.Runtime.GetServerSetting("port"));
            Assert.Contains("port=" + fx.Server.Port, fx.Server.Output);
            Assert.Contains("PortProgram: setting port " + fx.Server.Port + " in ", fx.Server.Output);
            Assert.True(ServerProbe.IsReady("127.0.0.1", fx.Server.Port, TimeSpan.FromSeconds(5)), "status probe after 'Ready for connections'");
        }

        // TOOLS-05: when the file already names the requested port nothing is written
        [Fact]
        public void Server_port_option_leaves_a_matching_settings_txt_untouched()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                int port = FreePort.Tcp();
                rt.SetServerSetting("port", port.ToString());
                byte[] before = File.ReadAllBytes(rt.ServerSettingsPath);
                DateTime mtime = File.GetLastWriteTimeUtc(rt.ServerSettingsPath);
                ServerProcess.Options o = new ServerProcess.Options();
                o.Port = port;
                using (ServerProcess s = new ServerProcess(rt, o))
                {
                    Assert.Equal(before, File.ReadAllBytes(rt.ServerSettingsPath));
                    Assert.Equal(mtime, File.GetLastWriteTimeUtc(rt.ServerSettingsPath));
                    Assert.DoesNotContain("PortProgram: setting port", s.Output);
                    Assert.Equal(port, s.Port);
                }
            }
        }

        // TOOLS-05: a settings.txt without a port line gets one appended
        [Fact]
        public void Server_port_option_appends_a_port_line_when_the_file_has_none()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                List<string> lines = new List<string>(File.ReadAllText(rt.ServerSettingsPath).Split('\n'));
                lines.RemoveAll(l => l.StartsWith("port ") || l.StartsWith("#The port number"));
                string withoutPort = string.Join("\n", lines);
                File.WriteAllText(rt.ServerSettingsPath, withoutPort, Encoding.ASCII);
                using (ServerProcess s = new ServerProcess(rt))
                {
                    string text = File.ReadAllText(rt.ServerSettingsPath);
                    Assert.Equal(withoutPort.TrimEnd('\r', '\n')
                        + "\n#The port number that the server should run on (must be accessible from the Internet.)\nport " + s.Port + "\n", text);
                    Assert.Equal(s.Port, Util.ParseServerConfig(text).port);
                    Assert.True(ServerProbe.IsReady("127.0.0.1", s.Port, TimeSpan.FromSeconds(5)));
                }
            }
        }

        // TOOLS-05: no Server/settings.txt at all -> the launcher's default text (HostServerForm.defaultConfig)
        [Fact]
        public void Server_creates_a_default_settings_txt_when_missing()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                File.Delete(rt.ServerSettingsPath);
                using (ServerProcess s = new ServerProcess(rt))
                {
                    Assert.Contains("PortProgram: creating " + Path.Combine(".", "Server", "settings.txt") + " with the default configuration", s.Output);
                    Assert.True(File.Exists(rt.ServerSettingsPath));
                    string text = File.ReadAllText(rt.ServerSettingsPath);
                    // The shipped file is that default text (the WinForms launcher wrote it); only the port differs.
                    string shipped = File.ReadAllText(Path.Combine(Repo.RuntimeDir, "Server", "settings.txt")).TrimEnd('\r', '\n');
                    Assert.Equal(shipped.Replace("port 24224", "port " + s.Port), text.TrimEnd('\r', '\n'));

                    Util.ServerConfig sc = Util.ParseServerConfig(text);
                    Util.ServerConfig def = new Util.ServerConfig();
                    Assert.Equal(s.Port, sc.port);
                    Assert.Equal(def.name, sc.name);
                    Assert.Equal(def.maxPlayers, sc.maxPlayers);
                    Assert.Equal(def.weapons, sc.weapons);
                    Assert.Equal(def.maxCaptures, sc.maxCaptures);
                    Assert.Equal(def.respawnTime, sc.respawnTime);
                    Assert.Equal(def.verifyNames, sc.verifyNames);
                    Assert.Equal(def.isPublic, sc.isPublic);
                    Assert.Equal(def.maxBots, sc.maxBots);
                    Assert.Equal(new string[] { "bases", "complex" }, sc.maps);
                }
            }
        }

        // TOOLS-05 / PortProgram argument parsing
        [Fact]
        public void Server_help_prints_usage_and_exits_zero_without_touching_the_runtime()
        {
            using (TempDir dir = new TempDir())
            {
                string stdout, stderr;
                int code = Run(Repo.ServerExe, new string[] { "--help" }, dir.Path, out stdout, out stderr);
                Assert.Equal(0, code);
                Assert.Contains("VexillumServer [--root <dir>] [--port <n>] [--help]", stdout);
                Assert.Contains("--port <n>", stdout);
                Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
            }
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("70000")]
        [InlineData("-5")]
        public void Server_rejects_an_invalid_port_value_with_exit_code_2(string value)
        {
            using (TempDir dir = new TempDir())
            {
                string stdout, stderr;
                int code = Run(Repo.ServerExe, new string[] { "--root", dir.Path, "--port", value }, dir.Path, out stdout, out stderr);
                Assert.Equal(2, code);
                Assert.Contains("Invalid --port value: " + value, stderr);
                Assert.False(File.Exists(Path.Combine(dir.Path, "Server", "settings.txt")), "settings are prepared only after the arguments parse");
            }
        }

        [Fact]
        public void Client_help_prints_usage_and_exits_zero_without_a_window()
        {
            using (TempDir dir = new TempDir())
            {
                string stdout, stderr;
                int code = Run(Repo.ClientExe, new string[] { "--help" }, dir.Path, out stdout, out stderr);
                Assert.Equal(0, code);
                Assert.Contains("VexillumGame [--root <dir>] [--connect <host>:<port>] [--help]", stdout);
                Assert.Contains("--connect <host>:<port>", stdout);
                Assert.False(File.Exists(Path.Combine(dir.Path, "debug_client.log")), "no game started");
            }
        }

        // TOOLS-18
        [Fact]
        public void Status_probe_reflects_the_server_ready_flag()
        {
            Assert.True(fx.Server.Console.ServerReady());
            Assert.True(ServerProbe.IsReady("127.0.0.1", fx.Server.Port, TimeSpan.FromMilliseconds(1500)));

            // The server answers packet 255 with Server.ready; clear it and the probe says no.
            fx.Server.Console.Sync("Server.ready = false;");
            try
            {
                Assert.False(fx.Server.Console.ServerReady());
                Assert.False(ServerProbe.IsReady("127.0.0.1", fx.Server.Port, TimeSpan.FromMilliseconds(1500)));
            }
            finally
            {
                fx.Server.Console.Sync("Server.ready = true;");
            }
            Assert.True(ServerProbe.IsReady("127.0.0.1", fx.Server.Port, TimeSpan.FromMilliseconds(1500)));
        }

        // TOOLS-18
        [Fact]
        public void Status_probe_is_false_for_a_closed_port_within_the_timeout()
        {
            int closed = FreePort.Tcp();
            Stopwatch sw = Stopwatch.StartNew();
            bool ready = ServerProbe.IsReady("127.0.0.1", closed, TimeSpan.FromMilliseconds(1500));
            sw.Stop();
            Assert.False(ready);
            Assert.True(sw.ElapsedMilliseconds < 1500 + 1000, "took " + sw.ElapsedMilliseconds + " ms");
        }

        // TOOLS-18: the byte sequence Vexillum.CheckStatus / Client.RequestStatus use before connecting
        [Fact]
        public void Server_answers_byte_255_with_one_bool_and_closes()
        {
            using (TcpClient c = new TcpClient("127.0.0.1", fx.Server.Port))
            {
                NetworkStream s = c.GetStream();
                s.ReadTimeout = 5000;
                s.WriteByte(255);
                s.Flush();
                int first = s.ReadByte();
                Assert.Equal(1, first);
                int second = s.ReadByte();
                Assert.Equal(-1, second);
            }
        }
    }
}
