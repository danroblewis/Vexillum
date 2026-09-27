using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Vexillum.Port.Master
{
    /// <summary>
    /// Stand-in for the original master server at playvexillum.com, which no
    /// longer exists. The historical code keeps calling
    /// <c>Util.HttpGet("servers.php")</c>, <c>Util.HttpGet("ping.php?...")</c>
    /// and <c>Util.HttpPost("reportBug.php", ...)</c>; those two methods hand
    /// the request to <see cref="TryHandle"/> first, which answers in the
    /// exact text format the 2013 PHP scripts produced.
    ///
    /// Server list = LAN servers found by UDP beacon (~1 s listen) plus
    /// internet servers that posted a heartbeat to a public ntfy.sh topic
    /// recently and answer a TCP status probe.
    ///
    /// Environment variables:
    ///   VEXILLUM_MASTER        "ntfy" (default) or "off"  (internet registry)
    ///   VEXILLUM_MASTER_URL    https://ntfy.sh (default; any ntfy server)
    ///   VEXILLUM_MASTER_TOPIC  vexillum-servers-v1 (default)
    ///   VEXILLUM_LAN           "on" (default) or "off"    (LAN beacons/discovery)
    ///   VEXILLUM_LAN_PORT      24224 (UDP; default = the game's TCP port number)
    /// </summary>
    public static class MasterServer
    {
        /// <summary>Field separator used by the original servers.php output (Util.delimiter).</summary>
        public const string Delimiter = "]|||]";

        /// <summary>How often a public server re-publishes its heartbeat to ntfy (the game asks every 45 s).</summary>
        public static TimeSpan PublishInterval = TimeSpan.FromMinutes(10);
        /// <summary>How far back the client looks for heartbeats when listing.</summary>
        public static TimeSpan ListingWindow = TimeSpan.FromMinutes(22);
        /// <summary>How long the client listens for LAN beacons.</summary>
        public static TimeSpan LanListenTime = TimeSpan.FromMilliseconds(1500);
        /// <summary>TCP status-probe timeout per internet server.</summary>
        public static TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

        private static readonly object gate = new object();
        private static DateTime lastPublish = DateTime.MinValue;
        private static string publicIp;
        private static DateTime publicIpTime = DateTime.MinValue;
        private static HttpClient http;

        public static string MasterUrl
        {
            get { return (Environment.GetEnvironmentVariable("VEXILLUM_MASTER_URL") ?? "https://ntfy.sh").TrimEnd('/'); }
        }

        public static string Topic
        {
            get { return Environment.GetEnvironmentVariable("VEXILLUM_MASTER_TOPIC") ?? "vexillum-servers-v1"; }
        }

        public static bool InternetEnabled
        {
            get { return !string.Equals(Environment.GetEnvironmentVariable("VEXILLUM_MASTER"), "off", StringComparison.OrdinalIgnoreCase); }
        }

        public static bool LanEnabled
        {
            get { return !string.Equals(Environment.GetEnvironmentVariable("VEXILLUM_LAN"), "off", StringComparison.OrdinalIgnoreCase); }
        }

        public static int LanPort
        {
            get
            {
                int p;
                return int.TryParse(Environment.GetEnvironmentVariable("VEXILLUM_LAN_PORT"), out p) && p > 0 && p < 65536 ? p : 24224;
            }
        }

        /// <summary>
        /// Entry point called by Util.HttpGet/HttpPost. Returns true when the
        /// request was one of the original master-server scripts and
        /// <paramref name="result"/> holds the reply text; false to let the
        /// caller do a normal HTTP request.
        /// </summary>
        public static bool TryHandle(string path, string postData, out string result)
        {
            result = "";
            if (path == null)
                return false;
            string script = path;
            string query = "";
            int q = path.IndexOf('?');
            if (q >= 0)
            {
                script = path.Substring(0, q);
                query = path.Substring(q + 1);
            }
            script = script.Trim('/');
            try
            {
                switch (script)
                {
                    case "servers.php":
                        result = ListServers();
                        return true;
                    case "ping.php":
                        result = Heartbeat(ParseQuery(query));
                        return true;
                    case "reportBug.php":
                        result = SaveBugReport(ParseQuery(postData ?? ""));
                        return true;
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                Log("MasterServer: " + script + " failed: " + ex.Message);
                result = "";
                return true;
            }
        }

        // ------------------------------------------------------------------
        // servers.php
        // ------------------------------------------------------------------

        public sealed class Entry
        {
            public string name;
            public string ip;
            public int port;
            public int players;
            public int maxPlayers;
            public string map;
            public bool lan;
            public long time;

            public string Key { get { return ip + ":" + port; } }

            public string ToLine()
            {
                string n = lan ? "[LAN] " + name : name;
                return string.Join(Delimiter, new string[] { n, ip, port.ToString(), players.ToString(), maxPlayers.ToString(), map ?? "" });
            }
        }

        /// <summary>Produces the servers.php text: one server per line, fields joined by the delimiter.</summary>
        public static string ListServers()
        {
            List<Entry> lan = LanEnabled ? LanDiscovery.Listen(LanPort, LanListenTime) : new List<Entry>();
            List<Entry> net = InternetEnabled ? FetchInternetServers() : new List<Entry>();

            List<Entry> merged = new List<Entry>(lan);
            HashSet<string> seen = new HashSet<string>(lan.Select(e => e.Key));
            HashSet<int> lanPorts = new HashSet<int>(lan.Select(e => e.port));
            string myIp = InternetEnabled ? GetPublicIp() : null;
            foreach (Entry e in net)
            {
                if (seen.Contains(e.Key))
                    continue;
                // Our own public address is not reachable from inside the LAN on
                // most home routers; the LAN beacon already lists that server.
                if (myIp != null && e.ip == myIp && lanPorts.Contains(e.port))
                    continue;
                seen.Add(e.Key);
                merged.Add(e);
            }
            Log("MasterServer: server list = " + lan.Count + " LAN + " + (merged.Count - lan.Count) + " internet");
            return string.Join("\n", merged.Select(e => e.ToLine()));
        }

        private static List<Entry> FetchInternetServers()
        {
            List<Entry> result = new List<Entry>();
            string body;
            try
            {
                string since = ((int)ListingWindow.TotalMinutes) + "m";
                body = Http().GetStringAsync(MasterUrl + "/" + Topic + "/json?poll=1&since=" + since).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log("MasterServer: could not read the registry at " + MasterUrl + ": " + ex.Message);
                return result;
            }
            Dictionary<string, Entry> latest = new Dictionary<string, Entry>();
            foreach (string line in body.Split('\n'))
            {
                if (line.Trim().Length == 0)
                    continue;
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(line))
                    {
                        JsonElement root = doc.RootElement;
                        JsonElement ev;
                        if (root.TryGetProperty("event", out ev) && ev.GetString() != "message")
                            continue;
                        JsonElement msg;
                        if (!root.TryGetProperty("message", out msg))
                            continue;
                        Entry e = ParseHeartbeat(msg.GetString());
                        if (e == null)
                            continue;
                        JsonElement t;
                        if (root.TryGetProperty("time", out t) && t.ValueKind == JsonValueKind.Number)
                            e.time = t.GetInt64();
                        Entry old;
                        if (!latest.TryGetValue(e.Key, out old) || old.time <= e.time)
                            latest[e.Key] = e;
                    }
                }
                catch (Exception)
                {
                    // junk on a public topic: ignore
                }
            }
            // Only list servers that actually answer; this also drops stale
            // heartbeats from servers that went away and anything bogus.
            List<Task<Entry>> probes = latest.Values.Select(e => Task.Run(delegate() { return ServerProbe.IsReady(e.ip, e.port, ProbeTimeout) ? e : null; })).ToList();
            try
            {
                Task.WaitAll(probes.ToArray(), (int)ProbeTimeout.TotalMilliseconds + 2000);
            }
            catch (Exception) { }
            foreach (Task<Entry> t in probes)
            {
                if (t.IsCompletedSuccessfully && t.Result != null)
                    result.Add(t.Result);
            }
            result.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static Entry ParseHeartbeat(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object)
                    return null;
                JsonElement v;
                if (!r.TryGetProperty("v", out v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
                    return null;
                Entry e = new Entry();
                e.name = Str(r, "name");
                e.ip = Str(r, "ip");
                e.map = Str(r, "map");
                e.port = Int(r, "port");
                e.players = Int(r, "players");
                e.maxPlayers = Int(r, "maxplayers");
                JsonElement pub;
                if (r.TryGetProperty("public", out pub) && pub.ValueKind == JsonValueKind.False)
                    return null;
                IPAddress addr;
                if (string.IsNullOrEmpty(e.name) || e.name.Length > 64 || e.port <= 0 || e.port > 65535 || !IPAddress.TryParse(e.ip, out addr))
                    return null;
                if (e.map != null && e.map.Length > 64)
                    e.map = e.map.Substring(0, 64);
                return e;
            }
        }

        private static string Str(JsonElement r, string name)
        {
            JsonElement v;
            return r.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static int Int(JsonElement r, string name)
        {
            JsonElement v;
            int i;
            if (!r.TryGetProperty(name, out v))
                return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out i))
                return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i))
                return i;
            return 0;
        }

        // ------------------------------------------------------------------
        // ping.php (server heartbeat)
        // ------------------------------------------------------------------

        private static string Heartbeat(Dictionary<string, string> q)
        {
            string name = Get(q, "name", "Vexillum Server");
            string map = Get(q, "map", "");
            int players = ToInt(Get(q, "players", "0"));
            int maxPlayers = ToInt(Get(q, "maxplayers", "0"));
            int port = ToInt(Get(q, "port", "24224"));
            string key = Get(q, "key", "0");
            bool isPublic = string.Equals(Get(q, "public", "true"), "true", StringComparison.OrdinalIgnoreCase);

            if (LanEnabled)
                LanDiscovery.Beacon(LanPort, name, port, players, maxPlayers, map, key);

            if (!isPublic || !InternetEnabled)
                return "OK";

            lock (gate)
            {
                if (DateTime.UtcNow - lastPublish < PublishInterval)
                    return "OK";
                string ip = GetPublicIp();
                if (ip == null)
                {
                    Log("MasterServer: no public IP available; the server is discoverable on the LAN only");
                    return "OK";
                }
                string json = JsonSerializer.Serialize(new
                {
                    v = 1, name = name, ip = ip, port = port, players = players, maxplayers = maxPlayers, map = map, key = key,
                    @public = true, game = "vexillum", protocol = 3,
                });
                try
                {
                    HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, MasterUrl + "/" + Topic);
                    req.Content = new StringContent(json, Encoding.UTF8, "text/plain");
                    req.Headers.TryAddWithoutValidation("Priority", "min");
                    req.Headers.TryAddWithoutValidation("Tags", "vexillum");
                    req.Headers.TryAddWithoutValidation("Cache", "yes");
                    HttpResponseMessage resp = Http().SendAsync(req).GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                    {
                        Log("MasterServer: registry refused the heartbeat: " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                        return "OK";
                    }
                    lastPublish = DateTime.UtcNow;
                    Log("MasterServer: published heartbeat to " + MasterUrl + "/" + Topic + " as " + ip + ":" + port + " (" + name + ", map " + map + ")");
                }
                catch (Exception ex)
                {
                    Log("MasterServer: could not publish heartbeat: " + ex.Message);
                }
            }
            return "OK";
        }

        private static string GetPublicIp()
        {
            lock (gate)
            {
                if (publicIp != null && DateTime.UtcNow - publicIpTime < TimeSpan.FromMinutes(30))
                    return publicIp;
                foreach (string url in new string[] { "https://api.ipify.org", "https://checkip.amazonaws.com", "https://ifconfig.me/ip" })
                {
                    try
                    {
                        string s = Http().GetStringAsync(url).GetAwaiter().GetResult().Trim();
                        IPAddress a;
                        if (IPAddress.TryParse(s, out a))
                        {
                            publicIp = s;
                            publicIpTime = DateTime.UtcNow;
                            return publicIp;
                        }
                    }
                    catch (Exception) { }
                }
                return publicIp;
            }
        }

        // ------------------------------------------------------------------
        // reportBug.php: keep the dialog working by saving the report locally
        // ------------------------------------------------------------------

        private static string SaveBugReport(Dictionary<string, string> form)
        {
            string dir = Path.Combine(Directory.GetCurrentDirectory(), "bugreports");
            Directory.CreateDirectory(dir);
            string id = "local-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string file = Path.Combine(dir, id + ".txt");
            StringBuilder sb = new StringBuilder();
            sb.Append("version: ").Append(Get(form, "version", "")).Append('\n');
            sb.Append("os: ").Append(Get(form, "os", "")).Append('\n');
            sb.Append("info: ").Append(Get(form, "info", "")).Append("\n\n");
            sb.Append(Get(form, "data", ""));
            File.WriteAllText(file, sb.ToString());
            Log("MasterServer: bug report saved to " + file);
            return id;
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static Dictionary<string, string> ParseQuery(string query)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query))
                return d;
            foreach (string part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? "" : part.Substring(eq + 1);
                try { d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace('+', ' ')); }
                catch (Exception) { d[k] = v; }
            }
            return d;
        }

        private static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) && v != null ? v : def;
        }

        private static int ToInt(string s)
        {
            int i;
            return int.TryParse(s, out i) ? i : 0;
        }

        private static HttpClient Http()
        {
            lock (gate)
            {
                if (http == null)
                {
                    http = new HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(8);
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Vexillum/1.0 (port)");
                }
                return http;
            }
        }

        /// <summary>Where diagnostics go; the launchers point this at Util.Debug.</summary>
        public static Action<string> Logger = delegate(string m) { Console.WriteLine(m); };

        internal static void Log(string message)
        {
            try
            {
                Logger(message);
            }
            catch (Exception)
            {
                Console.WriteLine(message);
            }
        }
    }

    /// <summary>Quick TCP status probe using the game's own protocol (byte 255 -> bool ready).</summary>
    public static class ServerProbe
    {
        public static bool IsReady(string host, int port, TimeSpan timeout)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    Task connect = c.ConnectAsync(host, port);
                    if (!connect.Wait(timeout) || !c.Connected)
                        return false;
                    c.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                    NetworkStream s = c.GetStream();
                    s.WriteByte(255);
                    s.Flush();
                    int b = s.ReadByte();
                    return b == 1;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// LAN discovery: the server broadcasts a small JSON beacon over UDP every
    /// two seconds; a client listens for about a second when building the
    /// server list. Sender address = the address to connect to.
    /// </summary>
    public static class LanDiscovery
    {
        private static readonly object gate = new object();
        private static Thread beaconThread;
        private static string payload;
        private static int beaconPort;

        public static void Beacon(int udpPort, string name, int tcpPort, int players, int maxPlayers, string map, string key)
        {
            lock (gate)
            {
                payload = JsonSerializer.Serialize(new { v = 1, name = name, port = tcpPort, players = players, maxplayers = maxPlayers, map = map, key = key });
                beaconPort = udpPort;
                if (beaconThread != null)
                    return;
                beaconThread = new Thread(BeaconLoop);
                beaconThread.Name = "LanBeacon";
                beaconThread.IsBackground = true;
                beaconThread.Start();
                MasterServer.Log("MasterServer: LAN beacon started on UDP " + udpPort);
            }
        }

        /// <summary>Query packet a client broadcasts; every server answers it directly.</summary>
        private const string QueryPayload = "{\"v\":1,\"q\":\"vexillum\"}";

        private static UdpClient OpenSharedUdp(int port)
        {
            UdpClient udp = new UdpClient();
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            udp.EnableBroadcast = true;
            return udp;
        }

        private static void BeaconLoop()
        {
            int port;
            lock (gate) { port = beaconPort; }
            UdpClient udp = null;
            try
            {
                // Bound to the discovery port so client queries reach us; shared
                // (ReuseAddress) so a client on the same machine can bind it too.
                udp = OpenSharedUdp(port);
                udp.Client.ReceiveTimeout = 250;
            }
            catch (Exception ex)
            {
                MasterServer.Log("MasterServer: LAN beacon unavailable: " + ex.Message);
                return;
            }
            DateTime nextBeacon = DateTime.MinValue;
            while (true)
            {
                string p;
                lock (gate) { p = payload; }
                byte[] bytes = Encoding.UTF8.GetBytes(p);
                if (DateTime.UtcNow >= nextBeacon)
                {
                    foreach (IPAddress target in BroadcastTargets())
                    {
                        try { udp.Send(bytes, bytes.Length, new IPEndPoint(target, port)); }
                        catch (Exception) { }
                    }
                    nextBeacon = DateTime.UtcNow.AddSeconds(1);
                }
                // Answer discovery queries immediately, straight to the asker.
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = udp.Receive(ref from); }
                catch (SocketException) { continue; }
                catch (Exception) { Thread.Sleep(250); continue; }
                if (IsQuery(data))
                {
                    try { udp.Send(bytes, bytes.Length, from); }
                    catch (Exception) { }
                }
            }
        }

        private static bool IsQuery(byte[] data)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(data)))
                {
                    JsonElement q;
                    return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("q", out q);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IEnumerable<IPAddress> BroadcastTargets()
        {
            List<IPAddress> targets = new List<IPAddress>();
            targets.Add(IPAddress.Broadcast);
            targets.Add(IPAddress.Loopback);
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    foreach (UnicastIPAddressInformation u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null)
                            continue;
                        byte[] a = u.Address.GetAddressBytes();
                        byte[] m = u.IPv4Mask.GetAddressBytes();
                        byte[] b = new byte[4];
                        for (int i = 0; i < 4; i++)
                            b[i] = (byte)(a[i] | (byte)~m[i]);
                        IPAddress bc = new IPAddress(b);
                        if (!targets.Contains(bc))
                            targets.Add(bc);
                    }
                }
            }
            catch (Exception) { }
            return targets;
        }

        public static List<MasterServer.Entry> Listen(int udpPort, TimeSpan duration)
        {
            Dictionary<string, MasterServer.Entry> found = new Dictionary<string, MasterServer.Entry>();
            UdpClient udp = null;
            try
            {
                udp = OpenSharedUdp(udpPort);
                udp.Client.ReceiveTimeout = 250;
                // Ask; servers reply at once, and the periodic beacons also arrive.
                byte[] query = Encoding.UTF8.GetBytes(QueryPayload);
                foreach (IPAddress target in BroadcastTargets())
                {
                    try { udp.Send(query, query.Length, new IPEndPoint(target, udpPort)); }
                    catch (Exception) { }
                }
                DateTime end = DateTime.UtcNow + duration;
                while (DateTime.UtcNow < end)
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data;
                    try { data = udp.Receive(ref from); }
                    catch (SocketException) { continue; }
                    try
                    {
                        using (JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(data)))
                        {
                            JsonElement r = doc.RootElement;
                            JsonElement v;
                            if (!r.TryGetProperty("v", out v) || v.GetInt32() != 1)
                                continue;
                            JsonElement qq;
                            if (r.TryGetProperty("q", out qq) || !r.TryGetProperty("name", out qq))
                                continue; // a query (possibly our own) or junk
                            MasterServer.Entry e = new MasterServer.Entry();
                            JsonElement x;
                            e.name = r.TryGetProperty("name", out x) ? x.GetString() : "Vexillum Server";
                            e.map = r.TryGetProperty("map", out x) ? x.GetString() : "";
                            e.port = r.TryGetProperty("port", out x) ? x.GetInt32() : 24224;
                            e.players = r.TryGetProperty("players", out x) ? x.GetInt32() : 0;
                            e.maxPlayers = r.TryGetProperty("maxplayers", out x) ? x.GetInt32() : 0;
                            e.ip = from.Address.ToString();
                            e.lan = true;
                            found[e.Key] = e;
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                MasterServer.Log("MasterServer: LAN discovery unavailable: " + ex.Message);
            }
            finally
            {
                if (udp != null)
                    udp.Close();
            }
            // Prefer a LAN address over loopback when the same server was seen on both.
            List<MasterServer.Entry> list = found.Values.ToList();
            if (list.Count > 1)
            {
                HashSet<int> lanPorts = new HashSet<int>(list.Where(e => e.ip != "127.0.0.1").Select(e => e.port));
                list = list.Where(e => e.ip != "127.0.0.1" || !lanPorts.Contains(e.port)).ToList();
            }
            list.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }
}
