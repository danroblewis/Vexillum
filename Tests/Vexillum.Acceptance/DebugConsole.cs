using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Vexillum.Acceptance
{
    /// <summary>Raised when the in-process script fails to compile or throws.</summary>
    public sealed class DebugEvalException : Exception
    {
        public string Code { get; private set; }
        public string Log { get; private set; }

        public DebugEvalException(string code, string error, string log) : base(error)
        {
            Code = code;
            Log = log;
        }
    }

    /// <summary>
    /// Client for the in-process debug console (Shims/DebugHost) of a server or
    /// client started with VEXILLUM_DEBUG_PORT. Protocol: TCP 127.0.0.1:port,
    /// newline-delimited JSON, request <c>{"code":"...","timeout":ms}</c>,
    /// response <c>{"ok":bool,"result":"...","log":"...","error":"...","ms":n}</c>.
    /// Script state persists per process; the scripts see <c>Server</c> /
    /// <c>Game</c> (dynamic), <c>Sync(() =&gt; ...)</c> to run on the game thread,
    /// <c>Get/Set/Call</c> for private members and <c>Dump</c>. The probe helpers
    /// (<see cref="ServerPlayers"/>, <see cref="ServerFrame"/>, <see cref="ServerEntities"/>)
    /// use the same scripts as the PROBES table in .claude/mcp/vexillum_dev.py,
    /// formatted for parsing.
    /// </summary>
    public sealed class DebugConsole : IDisposable
    {
        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;
        private readonly object sync = new object();

        public int Port { get; private set; }
        /// <summary>Default per-request timeout (the script is aborted server-side after it).</summary>
        public TimeSpan Timeout { get; set; }

        public bool IsConnected { get { return client != null && client.Connected; } }

        /// <summary>Connects to 127.0.0.1:port, retrying for up to 10 s while the host is starting.</summary>
        public DebugConsole(int port) : this(port, TimeSpan.FromSeconds(15))
        {
        }

        public DebugConsole(int port, TimeSpan timeout)
        {
            Port = port;
            Timeout = timeout;
            Exception last = null;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    client = new TcpClient();
                    client.Connect("127.0.0.1", port);
                    client.NoDelay = true;
                    NetworkStream s = client.GetStream();
                    reader = new StreamReader(s, new UTF8Encoding(false));
                    writer = new StreamWriter(s, new UTF8Encoding(false));
                    writer.AutoFlush = true;
                    return;
                }
                catch (SocketException ex)
                {
                    last = ex;
                    try { client.Dispose(); } catch (Exception) { }
                    client = null;
                    Thread.Sleep(200);
                }
            }
            throw new InvalidOperationException("could not connect to the debug console on 127.0.0.1:" + port, last);
        }

        /// <summary>Raw round trip: returns the parsed response document.</summary>
        public JsonDocument Send(string code, TimeSpan timeout)
        {
            lock (sync)
            {
                string request = JsonSerializer.Serialize(new { code = code, timeout = (int)timeout.TotalMilliseconds });
                client.ReceiveTimeout = (int)timeout.TotalMilliseconds + 5000;
                writer.WriteLine(request);
                string line = reader.ReadLine();
                if (line == null)
                    throw new IOException("debug console closed the connection");
                return JsonDocument.Parse(line);
            }
        }

        /// <summary>Evaluates a C# script and returns the formatted result; throws <see cref="DebugEvalException"/> on error.</summary>
        public string Eval(string code)
        {
            return Eval(code, Timeout);
        }

        public string Eval(string code, TimeSpan timeout)
        {
            using (JsonDocument doc = Send(code, timeout))
            {
                JsonElement root = doc.RootElement;
                string log = Prop(root, "log");
                bool ok = root.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
                if (!ok)
                    throw new DebugEvalException(code, Prop(root, "error") ?? "unknown error", log);
                return Prop(root, "result") ?? "";
            }
        }

        /// <summary>The log lines (Log(...) calls) of the last script, alongside its result.</summary>
        public string EvalWithLog(string code, out string log)
        {
            using (JsonDocument doc = Send(code, Timeout))
            {
                JsonElement root = doc.RootElement;
                log = Prop(root, "log") ?? "";
                bool ok = root.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
                if (!ok)
                    throw new DebugEvalException(code, Prop(root, "error") ?? "unknown error", log);
                return Prop(root, "result") ?? "";
            }
        }

        private static string Prop(JsonElement root, string name)
        {
            JsonElement v;
            if (root.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }

        /// <summary>
        /// Evaluates and converts the result: int, long, float, double, bool,
        /// string and enums are supported (parsed with the invariant culture).
        /// </summary>
        public T EvalT<T>(string code)
        {
            string r = Eval(code).Trim();
            return Parse<T>(r, code);
        }

        internal static T Parse<T>(string r, string code)
        {
            Type t = typeof(T);
            object v;
            if (t == typeof(string)) v = r;
            else if (t == typeof(int)) v = int.Parse(r, CultureInfo.InvariantCulture);
            else if (t == typeof(long)) v = long.Parse(r, CultureInfo.InvariantCulture);
            else if (t == typeof(short)) v = short.Parse(r, CultureInfo.InvariantCulture);
            else if (t == typeof(float)) v = float.Parse(r, CultureInfo.InvariantCulture);
            else if (t == typeof(double)) v = double.Parse(r, CultureInfo.InvariantCulture);
            else if (t == typeof(bool)) v = r.Equals("true", StringComparison.OrdinalIgnoreCase) || r == "True";
            else if (t.IsEnum) v = Enum.Parse(t, r, true);
            else throw new NotSupportedException("EvalT<" + t.Name + "> for '" + code + "' (result '" + r + "')");
            return (T)v;
        }

        /// <summary>Runs an action on the game thread (Sync) and ignores the result.</summary>
        public void Sync(string statements)
        {
            Eval("Sync(() => { " + statements + " return \"ok\"; })");
        }

        /// <summary>Clears the persistent script state of the host.</summary>
        public void Reset()
        {
            Eval("!reset");
        }

        // ---- probes (same shape as PROBES in .claude/mcp/vexillum_dev.py) ----

        /// <summary>One player as the server sees it (Server.players).</summary>
        public sealed class PlayerInfo
        {
            public string Name;
            public string Class;
            public int Score;
            public string Ping;
            public bool IsBot;
            public float? Health;
            public float? X, Y;
            public int EntityId;
            public bool IsOp;
            public bool Ready;
            public int WeaponIndex;
            public override string ToString()
            {
                return Name + " class=" + Class + " score=" + Score + " bot=" + IsBot + " hp=" + Health + " pos=" + X + "," + Y + " id=" + EntityId;
            }
        }

        /// <summary>One entity of the level (server or client).</summary>
        public sealed class EntityInfo
        {
            public string Type;
            public int Id;
            public float X, Y, VX, VY;
            public string PlayerName;
            public override string ToString()
            {
                return Type + " #" + Id + " pos=" + X + "," + Y + " vel=" + VX + "," + VY + (PlayerName != null ? " player=" + PlayerName : "");
            }
        }

        private const string playersScript =
            "Sync(() => string.Join(\"\\n\", ((System.Collections.IEnumerable)Server.players).Cast<Player>().Select(p => " +
            "p.name + \"|\" + p.CurrentClass + \"|\" + p.Score + \"|\" + p.pingString + \"|\" + p.isBot + \"|\" + " +
            "(p.Entity != null ? p.Entity.Health.ToString(System.Globalization.CultureInfo.InvariantCulture) : \"-\") + \"|\" + " +
            "(p.Entity != null ? p.Entity.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + \",\" + p.Entity.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) : \"-\") + \"|\" + " +
            "p.GetID() + \"|\" + Get(p, \"isOp\") + \"|\" + Get(p, \"ready\") + \"|\" + p.WeaponIndex)))";

        /// <summary>Server.players: name, class, score, ping, bot flag, health, position, entity id, op, ready, weapon index.</summary>
        public List<PlayerInfo> ServerPlayers()
        {
            List<PlayerInfo> r = new List<PlayerInfo>();
            string text = Eval(playersScript);
            foreach (string line in text.Split('\n'))
            {
                if (line.Trim().Length == 0)
                    continue;
                string[] f = line.Split('|');
                PlayerInfo p = new PlayerInfo();
                p.Name = f[0];
                p.Class = f[1];
                p.Score = int.Parse(f[2], CultureInfo.InvariantCulture);
                p.Ping = f[3];
                p.IsBot = bool.Parse(f[4]);
                p.Health = f[5] == "-" ? (float?)null : float.Parse(f[5], CultureInfo.InvariantCulture);
                if (f[6] != "-")
                {
                    string[] xy = f[6].Split(',');
                    p.X = float.Parse(xy[0], CultureInfo.InvariantCulture);
                    p.Y = float.Parse(xy[1], CultureInfo.InvariantCulture);
                }
                p.EntityId = int.Parse(f[7], CultureInfo.InvariantCulture);
                p.IsOp = bool.Parse(f[8]);
                p.Ready = bool.Parse(f[9]);
                p.WeaponIndex = int.Parse(f[10], CultureInfo.InvariantCulture);
                r.Add(p);
            }
            return r;
        }

        /// <summary>The server player with that name, or null.</summary>
        public PlayerInfo ServerPlayer(string name)
        {
            foreach (PlayerInfo p in ServerPlayers())
                if (p.Name == name)
                    return p;
            return null;
        }

        /// <summary>Server.level.frame (read on the Server Main thread).</summary>
        public int ServerFrame()
        {
            return EvalT<int>("Sync(() => Server.level.frame)");
        }

        /// <summary>Server.level.GetTime() in ms.</summary>
        public int ServerTime()
        {
            return EvalT<int>("Sync(() => Server.level.GetTime())");
        }

        /// <summary>Server.level.ShortName.</summary>
        public string ServerMap()
        {
            return Eval("Sync(() => Server.level.ShortName)").Trim();
        }

        /// <summary>Server.ready.</summary>
        public bool ServerReady()
        {
            return EvalT<bool>("Server.ready");
        }

        private const string entitiesScript =
            "Sync(() => string.Join(\"\\n\", ((List<Entity>){0}.getEntities()).Select(e => e.GetType().Name + \"|\" + e.ID + \"|\" + " +
            "e.Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + \",\" + e.Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"|\" + " +
            "e.Velocity.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + \",\" + e.Velocity.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) + \"|\" + " +
            "(e.player != null ? e.player.name : \"\"))))";

        /// <summary>Server.level.getEntities(): type, id, position, velocity, owning player name.</summary>
        public List<EntityInfo> ServerEntities()
        {
            return ParseEntities(Eval(string.Format(entitiesScript, "Server.level")));
        }

        /// <summary>Client side: ((GameView)Game.View).Level.getEntities().</summary>
        public List<EntityInfo> ClientEntities()
        {
            return ParseEntities(Eval(string.Format(entitiesScript, "((GameView)Game.View).Level")));
        }

        private static List<EntityInfo> ParseEntities(string text)
        {
            List<EntityInfo> r = new List<EntityInfo>();
            foreach (string line in text.Split('\n'))
            {
                if (line.Trim().Length == 0)
                    continue;
                string[] f = line.Split('|');
                EntityInfo e = new EntityInfo();
                e.Type = f[0];
                e.Id = int.Parse(f[1], CultureInfo.InvariantCulture);
                string[] xy = f[2].Split(',');
                e.X = float.Parse(xy[0], CultureInfo.InvariantCulture);
                e.Y = float.Parse(xy[1], CultureInfo.InvariantCulture);
                string[] v = f[3].Split(',');
                e.VX = float.Parse(v[0], CultureInfo.InvariantCulture);
                e.VY = float.Parse(v[1], CultureInfo.InvariantCulture);
                e.PlayerName = f.Length > 4 && f[4].Length > 0 ? f[4] : null;
                r.Add(e);
            }
            return r;
        }

        public void Dispose()
        {
            try { if (client != null) client.Dispose(); } catch (Exception) { }
            client = null;
        }
    }
}
