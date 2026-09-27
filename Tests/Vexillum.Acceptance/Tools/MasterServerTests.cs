using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Vexillum.Port.Master;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// Shims/MasterServer (the stand-in for playvexillum.com): routing of the
    /// three original scripts, the LAN beacon and discovery, the ntfy-style
    /// registry (read and publish) against a local HttpListener, and the
    /// local bug-report file. Everything is process-global (env vars, the
    /// beacon thread, the publish throttle), so the class is serial and
    /// every test scopes its environment. VEXILLUM_MASTER is "off" unless a
    /// fake registry URL is in place; the public ntfy.sh is never contacted.
    /// </summary>
    [Collection(ToolsConfigCollection.Name)]
    public class MasterServerTests
    {
        private static EnvScope Offline()
        {
            return new EnvScope("VEXILLUM_MASTER", "off", "VEXILLUM_LAN", "off", "VEXILLUM_MASTER_URL", null, "VEXILLUM_MASTER_TOPIC", null);
        }

        /// <summary>Resets the publish throttle and pins the "public IP" so GetPublicIp never goes online.</summary>
        private static void SeedPublicIp(string ip)
        {
            Reflect.SetStatic(typeof(MasterServer), "lastPublish", DateTime.MinValue);
            Reflect.SetStatic(typeof(MasterServer), "publicIp", ip);
            Reflect.SetStatic(typeof(MasterServer), "publicIpTime", DateTime.UtcNow);
        }

        private static string Handle(string path, string post, out bool handled)
        {
            string r;
            handled = MasterServer.TryHandle(path, post, out r);
            return r;
        }

        private static readonly string heartbeatQuery =
            "name=My%20Server&key=123&map=bases&players=2&maxplayers=12&port=25000&public=True";

        /// <summary>Server.Heartbeat.Send from the real server assembly (not referenced by this project; loaded from its build output).</summary>
        private static void ServerHeartbeatSend(bool isPublic, string name, int key, string map, int players, int maxPlayers, int port)
        {
            string dll = Path.ChangeExtension(Repo.ServerExe, ".dll");
            Assembly server = Assembly.LoadFrom(dll);
            Type heartbeat = server.GetType("Server.Heartbeat", true);
            Reflect.CallStatic(heartbeat, "Send", isPublic, name, key, map, players, maxPlayers, port);
            Assert.True((bool)Reflect.GetStatic(heartbeat, "success"), "Heartbeat.success after Send");
        }

        // ------------------------------------------------------------------
        // TOOLS-13: routing
        // ------------------------------------------------------------------

        [Fact]
        public void TryHandle_answers_the_three_original_scripts_and_declines_the_rest()
        {
            using (Offline())
            using (LogCapture log = new LogCapture())
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                bool handled;
                Assert.Equal("", Handle("servers.php", null, out handled));
                Assert.True(handled);
                Assert.Equal("", Handle("/servers.php?x=1", null, out handled));
                Assert.True(handled);
                Assert.Equal("OK", Handle("ping.php?name=A&public=false", null, out handled));
                Assert.True(handled);
                string id = Handle("reportBug.php", "version=1&data=x", out handled);
                Assert.True(handled);
                Assert.Matches(@"^local-\d{8}-\d{6}$", id);

                Assert.Equal("", Handle("login.php?user=a&pass=b", null, out handled));
                Assert.False(handled);
                Assert.Equal("", Handle(null, null, out handled));
                Assert.False(handled);
                Assert.Equal("", Handle("", null, out handled));
                Assert.False(handled);
                Assert.True(log.Any("MasterServer: server list = 0 LAN + 0 internet"));
            }
        }

        // TOOLS-13: an unhandled script goes to the real (dead) host and fails quietly
        [Fact]
        public void Unhandled_script_falls_through_to_playvexillum_and_returns_empty()
        {
            using (Offline())
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                bool wasServer = Util.IsServer;
                Util.IsServer = false;
                try
                {
                    Util.WriteDebugLog();
                    string log = dir.File("debug_client.log");
                    if (File.Exists(log)) File.Delete(log);

                    Stopwatch sw = Stopwatch.StartNew();
                    string result = Util.HttpGet("login.php?user=a&pass=b");
                    sw.Stop();
                    Assert.Equal("", result);
                    Assert.True(sw.ElapsedMilliseconds < 15000, "the request has a 10 s timeout, took " + sw.ElapsedMilliseconds + " ms");

                    Util.WriteDebugLog();
                    string debug = File.Exists(log) ? File.ReadAllText(log) : "";
                    Assert.Contains("Exception", debug);      // the exception text lands in the debug log, nothing is thrown

                    Assert.Empty(Util.HttpGetArray("login.php?user=a"));
                }
                finally
                {
                    Util.IsServer = wasServer;
                }
            }
        }

        // TOOLS-13: an exception inside a handled script is logged and answered with ""
        [Fact]
        public void Exception_inside_a_handled_script_yields_handled_and_empty()
        {
            using (Offline())
            using (LogCapture log = new LogCapture())
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                File.WriteAllText(dir.File("bugreports"), "not a directory");   // Directory.CreateDirectory throws
                bool handled;
                string result = Handle("reportBug.php", "version=1", out handled);
                Assert.True(handled);
                Assert.Equal("", result);
                Assert.True(log.Any("MasterServer: reportBug.php failed:"), log.Text);
                Assert.Equal("", Util.HttpPost("reportBug.php", "version=1"));
            }
        }

        // ------------------------------------------------------------------
        // TOOLS-14 / TOOLS-15: LAN beacon and discovery
        // ------------------------------------------------------------------

        [Fact]
        public void Heartbeat_offline_starts_the_LAN_beacon_once_and_answers_OK_without_http()
        {
            int port = BeaconPort.Value;
            using (FakeRegistry registry = new FakeRegistry())
            using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "off", "VEXILLUM_LAN", "on", "VEXILLUM_LAN_PORT", port.ToString(),
                                               "VEXILLUM_MASTER_URL", registry.Url, "VEXILLUM_MASTER_TOPIC", "test-topic"))
            using (LogCapture log = new LogCapture())
            {
                SeedPublicIp("203.0.113.9");
                bool handled;
                Assert.Equal("OK", Handle("ping.php?" + heartbeatQuery, null, out handled));
                Assert.True(handled);
                int started = log.Count("MasterServer: LAN beacon started on UDP " + port);
                Assert.True(started <= 1, log.Text);
                if (BeaconWasRunning)
                    Assert.Equal(0, started);   // the thread exists once per process
                BeaconWasRunning = true;

                // Repeated heartbeats replace the payload, never start another thread.
                Assert.Equal("OK", Handle("ping.php?" + heartbeatQuery, null, out handled));
                Assert.Equal("OK", Handle("ping.php?" + heartbeatQuery.Replace("public=True", "public=false"), null, out handled));
                Assert.Equal(started, log.Count("MasterServer: LAN beacon started"));

                // The exact query Server/Heartbeat.cs builds (bool.ToString() => "True", Uri.EscapeDataString on the name).
                ServerHeartbeatSend(true, "My Server", 123, "bases", 2, 12, 25000);
                Assert.Equal(started, log.Count("MasterServer: LAN beacon started"));

                Assert.Empty(registry.Requests);   // VEXILLUM_MASTER=off: no HTTP at all
                Assert.False(log.Any("published heartbeat"));
            }
        }

        private static bool BeaconWasRunning;

        [Fact]
        public void LAN_listen_hears_the_beacon_and_servers_php_formats_it_as_six_columns()
        {
            int port = BeaconPort.Value;
            using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "off", "VEXILLUM_LAN", "on", "VEXILLUM_LAN_PORT", port.ToString()))
            using (LogCapture log = new LogCapture())
            {
                bool handled;
                Assert.Equal("OK", Handle("ping.php?" + heartbeatQuery, null, out handled));
                BeaconWasRunning = true;

                Stopwatch sw = Stopwatch.StartNew();
                List<MasterServer.Entry> found = LanDiscovery.Listen(port, TimeSpan.FromMilliseconds(1500));
                sw.Stop();
                Assert.True(sw.ElapsedMilliseconds < 3000, "listen took " + sw.ElapsedMilliseconds + " ms");
                // One entry per address the beacon was heard from (loopback is dropped
                // when a LAN address answered; a machine with several interfaces may
                // yield one entry per interface), all describing the same server.
                Assert.NotEmpty(found);
                HashSet<string> local = Net.LocalAddresses();
                foreach (MasterServer.Entry x in found)
                {
                    Assert.Equal("My Server", x.name);   // %20 decoded by ParseQuery, carried in the beacon JSON
                    Assert.Equal(25000, x.port);
                    Assert.Equal(2, x.players);
                    Assert.Equal(12, x.maxPlayers);
                    Assert.Equal("bases", x.map);
                    Assert.True(x.lan);
                    Assert.Contains(x.ip, local);
                    Assert.Equal("[LAN] My Server]|||]" + x.ip + "]|||]25000]|||]2]|||]12]|||]bases", x.ToLine());
                }
                Assert.Equal(found.Count, new HashSet<string>(found.ConvertAll(x => x.ip)).Count);
                if (found.Count > 1)
                    Assert.DoesNotContain("127.0.0.1", found.ConvertAll(x => x.ip));

                string[][] rows = Util.HttpGetArray("servers.php");
                Assert.NotEmpty(rows);
                foreach (string[] row in rows)
                {
                    Assert.Equal(6, row.Length);
                    Assert.Equal("[LAN] My Server", row[0]);
                    Assert.Contains(row[1], local);
                    Assert.Equal(new string[] { "25000", "2", "12", "bases" }, new string[] { row[2], row[3], row[4], row[5] });
                }
                Assert.True(log.Any("MasterServer: server list = " + rows.Length + " LAN + 0 internet"), log.Text);
            }
        }

        // TOOLS-15: parsing of what arrives on the discovery port. Listens on a
        // port of its own: unicast to a port shared with the in-process beacon
        // (SO_REUSEPORT) may be delivered to the beacon socket instead.
        [Fact]
        public void LAN_listen_ignores_queries_junk_and_other_versions_and_prefers_LAN_over_loopback()
        {
            int port = FreePort.Udp();
            string lanIp = Net.LanAddress();
            string[] packets =
            {
                "{\"v\":1,\"name\":\"Fake One\",\"port\":31000,\"players\":1,\"maxplayers\":8,\"map\":\"complex\",\"key\":\"7\"}",
                "{\"v\":1,\"q\":\"vexillum\"}",                                            // a query (possibly our own)
                "{\"v\":1,\"q\":\"x\",\"name\":\"Has Q\",\"port\":31001}",                  // 'q' present: ignored
                "{\"v\":2,\"name\":\"Version Two\",\"port\":31002}",                        // wrong version
                "{\"v\":1,\"port\":31003}",                                                 // no name
                "not json at all",
                "{\"v\":1,\"name\":\"Loop Only\",\"port\":31004}",
            };
            using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "off", "VEXILLUM_LAN", "on", "VEXILLUM_LAN_PORT", port.ToString()))
            using (LogCapture log = new LogCapture())
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                Thread sender = new Thread(delegate()
                {
                    using (UdpClient udp = new UdpClient())
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            foreach (string p in packets)
                            {
                                byte[] b = Encoding.UTF8.GetBytes(p);
                                try { udp.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, port)); } catch (Exception) { }
                                // The same server seen from a LAN address: only that entry should survive.
                                if (lanIp != null && p.Contains("Fake One"))
                                    try { udp.Send(b, b.Length, new IPEndPoint(IPAddress.Parse(lanIp), port)); } catch (Exception) { }
                            }
                            Thread.Sleep(50);
                        }
                    }
                });
                sender.IsBackground = true;
                sender.Start();
                List<MasterServer.Entry> found;
                try
                {
                    found = LanDiscovery.Listen(port, TimeSpan.FromMilliseconds(1500));
                }
                finally
                {
                    stop.Cancel();
                    sender.Join(2000);
                }

                List<string> names = found.ConvertAll(x => x.name);
                Assert.Contains("Fake One", names);
                Assert.Contains("Loop Only", names);
                Assert.DoesNotContain("Has Q", names);
                Assert.DoesNotContain("Version Two", names);
                Assert.DoesNotContain("Vexillum Server", names);   // the nameless packet gets no default
                foreach (MasterServer.Entry x in found)
                    Assert.NotEqual(31003, x.port);

                MasterServer.Entry one = found.Find(x => x.name == "Fake One");
                Assert.Equal(31000, one.port);
                Assert.Equal(1, one.players);
                Assert.Equal(8, one.maxPlayers);
                Assert.Equal("complex", one.map);
                if (lanIp != null)
                {
                    Assert.Equal(lanIp, one.ip);
                    Assert.Single(found.FindAll(x => x.port == 31000));
                }
                else
                {
                    Assert.Equal("127.0.0.1", one.ip);
                }
                MasterServer.Entry loop = found.Find(x => x.name == "Loop Only");
                Assert.Equal("127.0.0.1", loop.ip);
                Assert.Equal("", loop.map);
                Assert.Equal(0, loop.players);
                // Sorted by name, case-insensitive.
                List<string> sorted = new List<string>(names);
                sorted.Sort(StringComparer.OrdinalIgnoreCase);
                Assert.Equal(sorted, names);
            }
        }

        // ------------------------------------------------------------------
        // TOOLS-16: everything off
        // ------------------------------------------------------------------

        [Fact]
        public void LAN_off_and_master_off_give_an_empty_list_even_with_a_beacon_running()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            using (ServerProcess s = new ServerProcess(rt))
            {
                Assert.NotNull(s.WaitFor("MasterServer: LAN beacon started on UDP " + s.LanPort, 30));
                using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "off", "VEXILLUM_LAN", "OFF", "VEXILLUM_LAN_PORT", s.LanPort.ToString(),
                                                   "VEXILLUM_MASTER_URL", "http://127.0.0.1:1", "VEXILLUM_MASTER_TOPIC", null))
                using (LogCapture log = new LogCapture())
                {
                    Assert.False(MasterServer.LanEnabled);
                    Assert.False(MasterServer.InternetEnabled);
                    Assert.Equal(s.LanPort, MasterServer.LanPort);

                    Assert.Equal("", MasterServer.ListServers());
                    Assert.True(log.Any("MasterServer: server list = 0 LAN + 0 internet"), log.Text);
                    Assert.False(log.Any("could not read the registry"), "no HTTP attempt: " + log.Text);
                    Assert.Empty(Util.HttpGetArray("servers.php"));
                    AssertNoUdpSocketOn(s.LanPort);

                    // The beacon really is there: switching LAN on finds the server.
                    env.Set("VEXILLUM_LAN", "on");
                    string[][] rows = Util.HttpGetArray("servers.php");
                    Assert.Contains(rows, r => r[0] == "[LAN] Vexillum Server" && r[2] == s.Port.ToString());
                }
            }
        }

        /// <summary>lsof (macOS/Linux): no UDP socket of this process on the port. Skipped silently when lsof is unavailable.</summary>
        private static void AssertNoUdpSocketOn(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("lsof", "-nP -a -iUDP:" + port + " -p " + Environment.ProcessId);
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    Assert.DoesNotContain(":" + port, output);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // no lsof on this platform
            }
        }

        [Fact]
        public void Environment_switches_are_case_insensitive_and_LanPort_falls_back_to_24224()
        {
            using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "OFF", "VEXILLUM_LAN", "Off", "VEXILLUM_LAN_PORT", null))
            {
                Assert.False(MasterServer.InternetEnabled);
                Assert.False(MasterServer.LanEnabled);
                Assert.Equal(24224, MasterServer.LanPort);
                env.Set("VEXILLUM_LAN_PORT", "0");
                Assert.Equal(24224, MasterServer.LanPort);
                env.Set("VEXILLUM_LAN_PORT", "70000");
                Assert.Equal(24224, MasterServer.LanPort);
                env.Set("VEXILLUM_LAN_PORT", "abc");
                Assert.Equal(24224, MasterServer.LanPort);
                env.Set("VEXILLUM_LAN_PORT", "5555");
                Assert.Equal(5555, MasterServer.LanPort);
                env.Set("VEXILLUM_MASTER", "ntfy");
                env.Set("VEXILLUM_LAN", "anything-else");
                Assert.True(MasterServer.InternetEnabled);
                Assert.True(MasterServer.LanEnabled);
                env.Set("VEXILLUM_MASTER_URL", "http://127.0.0.1:9/");
                Assert.Equal("http://127.0.0.1:9", MasterServer.MasterUrl);
                env.Set("VEXILLUM_MASTER_TOPIC", null);
                Assert.Equal("vexillum-servers-v1", MasterServer.Topic);
            }
        }

        // ------------------------------------------------------------------
        // TOOLS-17: internet registry parsing
        // ------------------------------------------------------------------

        private static string Feed(params string[] entries)
        {
            StringBuilder sb = new StringBuilder();
            long t = 1700000000;
            foreach (string e in entries)
            {
                // ntfy json feed: one event per line; "time" ascending unless the entry carries "@time=N"
                long time = t++;
                string hb = e;
                Match m = Regex.Match(e, @"@time=(\d+)$");
                if (m.Success)
                {
                    time = long.Parse(m.Groups[1].Value);
                    hb = e.Substring(0, m.Index);
                }
                sb.Append(JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString("N"), time = time, @event = "message", topic = "t", message = hb })).Append('\n');
            }
            sb.Append(JsonSerializer.Serialize(new { id = "k", time = t, @event = "keepalive", topic = "t" })).Append('\n');
            return sb.ToString();
        }

        private static string Hb(string name, string ip, int port, string extra = "")
        {
            return "{\"v\":1,\"name\":\"" + name + "\",\"ip\":\"" + ip + "\",\"port\":" + port + ",\"players\":3,\"maxplayers\":12,\"map\":\"bases\",\"key\":\"9\",\"game\":\"vexillum\",\"protocol\":3" + extra + "}";
        }

        [Fact]
        public void Registry_entries_are_validated_probed_deduplicated_and_sorted()
        {
            TimeSpan probeTimeout = MasterServer.ProbeTimeout;
            MasterServer.ProbeTimeout = TimeSpan.FromMilliseconds(300);
            try
            {
                using (FakeRegistry registry = new FakeRegistry())
                using (FakeStatusResponder zeta = new FakeStatusResponder())
                using (FakeStatusResponder alpha = new FakeStatusResponder())
                using (FakeStatusResponder notReady = new FakeStatusResponder())
                using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "ntfy", "VEXILLUM_LAN", "off", "VEXILLUM_MASTER_URL", registry.Url, "VEXILLUM_MASTER_TOPIC", "test-topic"))
                using (LogCapture log = new LogCapture())
                {
                    SeedPublicIp("203.0.113.9");
                    notReady.Answer = 0;
                    int unreachable = FreePort.Tcp();
                    string longMap = new string('m', 100);
                    registry.FeedBody = Feed(
                        Hb("Zeta Newest", "127.0.0.1", zeta.Port) + "@time=1700000500",
                        Hb("Zeta Older", "127.0.0.1", zeta.Port) + "@time=1700000400",       // same key, older: dropped
                        Hb("alpha", "127.0.0.1", alpha.Port).Replace("\"map\":\"bases\"", "\"map\":\"" + longMap + "\""),
                        Hb("Version Two", "127.0.0.1", alpha.Port + 0).Replace("\"v\":1", "\"v\":2"),
                        Hb("", "127.0.0.1", 40001),
                        Hb(new string('n', 65), "127.0.0.1", 40002),
                        Hb("Bad Ip", "nope", 40003),
                        Hb("Bad Port", "127.0.0.1", 70000),
                        Hb("Private", "127.0.0.1", zeta.Port, ",\"public\":false"),
                        Hb("Not Ready", "127.0.0.1", notReady.Port),
                        Hb("Gone", "127.0.0.1", unreachable),
                        "{\"junk\":true}",
                        "not json");

                    Stopwatch sw = Stopwatch.StartNew();
                    string list = MasterServer.ListServers();
                    sw.Stop();

                    string[] lines = list.Split('\n');
                    Assert.Equal(2, lines.Length);
                    string[] a = lines[0].Split(new string[] { MasterServer.Delimiter }, StringSplitOptions.None);
                    string[] z = lines[1].Split(new string[] { MasterServer.Delimiter }, StringSplitOptions.None);
                    Assert.Equal(new string[] { "alpha", "127.0.0.1", alpha.Port.ToString(), "3", "12", new string('m', 64) }, a);
                    Assert.Equal(new string[] { "Zeta Newest", "127.0.0.1", zeta.Port.ToString(), "3", "12", "bases" }, z);
                    Assert.True(zeta.Hits >= 1);
                    Assert.True(alpha.Hits >= 1);
                    Assert.True(notReady.Hits >= 1);
                    Assert.True(sw.ElapsedMilliseconds < 300 + 2000 + 3000, "took " + sw.ElapsedMilliseconds + " ms");
                    Assert.True(log.Any("MasterServer: server list = 0 LAN + 2 internet"), log.Text);

                    RecordedRequest req = Assert.Single(registry.Requests);
                    Assert.Equal("GET", req.Method);
                    Assert.Equal("/test-topic/json?poll=1&since=22m", req.PathAndQuery);
                    Assert.StartsWith("Vexillum/1.0", req.Headers["User-Agent"]);

                    // Through the author's code path: HttpGetArray splits the same lines.
                    string[][] rows = Util.HttpGetArray("servers.php");
                    Assert.Equal(2, rows.Length);
                    Assert.Equal("alpha", rows[0][0]);
                }
            }
            finally
            {
                MasterServer.ProbeTimeout = probeTimeout;
            }
        }

        [Fact]
        public void Unreachable_registry_gives_an_empty_list_and_a_log_line()
        {
            int closed = FreePort.Tcp();
            using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "ntfy", "VEXILLUM_LAN", "off", "VEXILLUM_MASTER_URL", "http://127.0.0.1:" + closed, "VEXILLUM_MASTER_TOPIC", "test-topic"))
            using (LogCapture log = new LogCapture())
            {
                SeedPublicIp("203.0.113.9");
                Assert.Equal("", MasterServer.ListServers());
                Assert.True(log.Any("MasterServer: could not read the registry at http://127.0.0.1:" + closed), log.Text);
                Assert.True(log.Any("MasterServer: server list = 0 LAN + 0 internet"), log.Text);
            }
        }

        // ------------------------------------------------------------------
        // TOOLS-19: publishing
        // ------------------------------------------------------------------

        [Fact]
        public void Public_heartbeat_is_posted_once_per_interval_with_the_documented_json()
        {
            using (FakeRegistry registry = new FakeRegistry())
            using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "ntfy", "VEXILLUM_LAN", "off", "VEXILLUM_MASTER_URL", registry.Url, "VEXILLUM_MASTER_TOPIC", "test-topic"))
            using (LogCapture log = new LogCapture())
            {
                SeedPublicIp("203.0.113.9");
                string query = "ping.php?name=S&key=1&map=bases&players=0&maxplayers=12&port=24224&public=True";
                bool handled;
                Assert.Equal("OK", Handle(query, null, out handled));
                Assert.Equal("OK", Handle(query, null, out handled));     // inside PublishInterval: throttled
                Assert.Equal("OK", Handle(query.Replace("public=True", "public=false"), null, out handled));

                RecordedRequest post = Assert.Single(registry.Requests);
                Assert.Equal("POST", post.Method);
                Assert.Equal("/test-topic", post.PathAndQuery);
                Assert.StartsWith("text/plain", post.ContentType);
                Assert.Equal("min", post.Headers["Priority"]);
                Assert.Equal("vexillum", post.Headers["Tags"]);
                Assert.Equal("yes", post.Headers["Cache"]);
                Assert.StartsWith("Vexillum/1.0", post.Headers["User-Agent"]);
                using (JsonDocument doc = JsonDocument.Parse(post.Body))
                {
                    JsonElement r = doc.RootElement;
                    Assert.Equal(1, r.GetProperty("v").GetInt32());
                    Assert.Equal("S", r.GetProperty("name").GetString());
                    Assert.Equal("203.0.113.9", r.GetProperty("ip").GetString());
                    Assert.Equal(24224, r.GetProperty("port").GetInt32());
                    Assert.Equal(0, r.GetProperty("players").GetInt32());
                    Assert.Equal(12, r.GetProperty("maxplayers").GetInt32());
                    Assert.Equal("bases", r.GetProperty("map").GetString());
                    Assert.Equal("1", r.GetProperty("key").GetString());
                    Assert.True(r.GetProperty("public").GetBoolean());
                    Assert.Equal("vexillum", r.GetProperty("game").GetString());
                    Assert.Equal(3, r.GetProperty("protocol").GetInt32());
                }
                Assert.Equal(1, log.Count("MasterServer: published heartbeat to " + registry.Url + "/test-topic as 203.0.113.9:24224 (S, map bases)"));

                // A private server never publishes, even with the throttle reset.
                SeedPublicIp("203.0.113.9");
                Assert.Equal("OK", Handle(query.Replace("public=True", "public=false"), null, out handled));
                Assert.Single(registry.Requests);
            }
        }

        [Fact]
        public void Registry_refusal_is_logged_and_the_heartbeat_still_answers_OK()
        {
            using (FakeRegistry registry = new FakeRegistry())
            using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "ntfy", "VEXILLUM_LAN", "off", "VEXILLUM_MASTER_URL", registry.Url, "VEXILLUM_MASTER_TOPIC", "test-topic"))
            using (LogCapture log = new LogCapture())
            {
                SeedPublicIp("203.0.113.9");
                registry.PostStatus = 403;
                bool handled;
                Assert.Equal("OK", Handle("ping.php?name=S&key=1&map=bases&players=0&maxplayers=12&port=24224&public=True", null, out handled));
                Assert.Single(registry.Requests);
                Assert.True(log.Any("MasterServer: registry refused the heartbeat: 403"), log.Text);
                Assert.False(log.Any("published heartbeat"));
                // Not throttled after a refusal: the next heartbeat tries again.
                Assert.Equal("OK", Handle("ping.php?name=S&key=1&map=bases&players=0&maxplayers=12&port=24224&public=True", null, out handled));
                Assert.Equal(2, registry.Requests.Count);
            }
        }

        [Fact]
        public void Heartbeat_without_a_public_ip_stays_LAN_only()
        {
            using (FakeRegistry registry = new FakeRegistry())
            using (EnvScope env = new EnvScope("VEXILLUM_MASTER", "ntfy", "VEXILLUM_LAN", "off", "VEXILLUM_MASTER_URL", registry.Url, "VEXILLUM_MASTER_TOPIC", "test-topic"))
            using (LogCapture log = new LogCapture())
            {
                // GetPublicIp would go to api.ipify.org etc.; a cached null is returned only while
                // the cache is fresh, so pin the lookup off by pointing the cache at "no address".
                Reflect.SetStatic(typeof(MasterServer), "lastPublish", DateTime.MinValue);
                Reflect.SetStatic(typeof(MasterServer), "publicIp", null);
                Reflect.SetStatic(typeof(MasterServer), "publicIpTime", DateTime.MinValue);
                HttpClientOffline.Install();
                try
                {
                    bool handled;
                    Assert.Equal("OK", Handle("ping.php?name=S&key=1&map=bases&players=0&maxplayers=12&port=24224&public=True", null, out handled));
                    Assert.True(log.Any("MasterServer: no public IP available; the server is discoverable on the LAN only"), log.Text);
                    Assert.Empty(registry.Requests);
                }
                finally
                {
                    HttpClientOffline.Restore();
                }
            }
        }

        /// <summary>
        /// Swaps MasterServer's shared HttpClient for one whose handler fails
        /// every request, so GetPublicIp cannot reach the internet during a
        /// test and the run stays hermetic.
        /// </summary>
        private static class HttpClientOffline
        {
            private static object previous;

            private sealed class FailingHandler : System.Net.Http.HttpMessageHandler
            {
                protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    throw new System.Net.Http.HttpRequestException("offline (test)");
                }
            }

            public static void Install()
            {
                previous = Reflect.GetStatic(typeof(MasterServer), "http");
                System.Net.Http.HttpClient c = new System.Net.Http.HttpClient(new FailingHandler());
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Vexillum/1.0 (port)");
                Reflect.SetStatic(typeof(MasterServer), "http", c);
            }

            public static void Restore()
            {
                Reflect.SetStatic(typeof(MasterServer), "http", previous);
            }
        }

        // ------------------------------------------------------------------
        // TOOLS-20 / TOOLS-10: bug reports
        // ------------------------------------------------------------------

        [Fact]
        public void ReportBug_writes_a_local_file_with_unescaped_fields()
        {
            using (Offline())
            using (LogCapture log = new LogCapture())
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                Assert.False(Directory.Exists(dir.File("bugreports")));
                bool handled;
                string id = Handle("reportBug.php", "version=1&os=Unix%2013.0&data=line1%0Aline2&info=hello+world", out handled);
                Assert.True(handled);
                Assert.Matches(@"^local-\d{8}-\d{6}$", id);
                string file = Path.Combine(dir.Path, "bugreports", id + ".txt");
                Assert.True(File.Exists(file));
                Assert.Equal("version: 1\nos: Unix 13.0\ninfo: hello world\n\nline1\nline2", File.ReadAllText(file));
                // The path is built from Directory.GetCurrentDirectory() (symlinks resolved, e.g. /private/var on macOS).
                Assert.True(log.Any("MasterServer: bug report saved to " + Path.Combine(Directory.GetCurrentDirectory(), "bugreports", id + ".txt")), log.Text);
            }
        }

        [Fact]
        public void ReportBug_defaults_missing_keys_to_empty()
        {
            using (Offline())
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                bool handled;
                string id = Handle("reportBug.php", "", out handled);
                Assert.True(handled);
                Assert.Equal("version: \nos: \ninfo: \n\n", File.ReadAllText(Path.Combine(dir.Path, "bugreports", id + ".txt")));
                string id2 = Handle("reportBug.php", null, out handled);
                Assert.True(handled);
                Assert.Matches(@"^local-", id2);
            }
        }

        // TOOLS-10: pins the known bug (the report body is not escaped, so '&' inside the log splits it)
        [Fact]
        public void EscapeUriString_returns_its_input_unescaped_and_the_bug_report_splits_on_ampersands()
        {
            Assert.Equal("a&b=c d", Util.EscapeUriString("a&b=c d"));
            Assert.Equal("", Util.EscapeUriString(""));
            Assert.Equal(new string('x', 4500), Util.EscapeUriString(new string('x', 4500)));

            using (Offline())
            using (TempDir dir = new TempDir())
            using (new CwdScope(dir.Path))
            {
                bool handled;
                // ReportBugDialog: "version=1&os=...&data=" + Util.EscapeUriString(log) + "&info=..."
                string id = Handle("reportBug.php", "version=1&os=Mac&data=" + Util.EscapeUriString("log&more=1") + "&info=x", out handled);
                Assert.True(handled);
                string text = File.ReadAllText(Path.Combine(dir.Path, "bugreports", id + ".txt"));
                Assert.Equal("version: 1\nos: Mac\ninfo: x\n\nlog", text);   // "&more=1" became a separate key
            }
        }

        [Fact(Skip = "Known original bug: Util.EscapeUriString builds the escaped StringBuilder and returns the unescaped input, docs/PORTING.md")]
        public void EscapeUriString_escapes_reserved_characters()
        {
            Assert.Equal("a%26b%3Dc%20d", Util.EscapeUriString("a&b=c d"));
        }
    }
}
