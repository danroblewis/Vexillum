using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MiscUtil.Conversion;
using MiscUtil.IO;
using SevenZip.Compression.LZMA;
using Vexillum.Game;
using Vexillum.util;

namespace Vexillum.Acceptance
{
    /// <summary>Thrown when the server refused the connection or a packet id is unknown.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    /// <summary>Thrown by WaitFor when the server sent packet 254 (or closed) before the awaited packet arrived.</summary>
    public sealed class ServerDisconnectedException : Exception
    {
        public string Reason { get; private set; }
        public ServerDisconnectedException(string reason, string message) : base(message) { Reason = reason; }
    }

    /// <summary>
    /// A headless implementation of the client side of docs/PROTOCOL.md: a raw
    /// TcpClient with little-endian MiscUtil readers/writers, a background
    /// thread that decodes every server packet into a typed
    /// <see cref="ServerPacket"/> record, and send helpers for every client
    /// packet. Nothing in Game/ is used for the protocol itself except the
    /// LZMA helper and the enums, so the client exercises the real server
    /// exactly as the shipped game would.
    ///
    /// Typical use:
    /// <code>
    /// using (ScriptedClient c = new ScriptedClient(server))
    /// {
    ///     c.JoinGame("alice");                    // login, status, terrain, entities, spawn, finish
    ///     c.SendChat("hello");
    ///     ChatPacket p = c.WaitFor&lt;ChatPacket&gt;(x => x.Text.Contains("hello"), 5);
    /// }
    /// </code>
    /// Pings from the server (packet 0) are answered automatically. An unknown
    /// packet id stops the reader and surfaces as a <see cref="ProtocolException"/>
    /// from the next WaitFor. Positions of tracked entities are updated from
    /// packets 30/31/32; the terrain is a snapshot of packet 3 (explosions
    /// received later are recorded, not applied).
    /// </summary>
    public sealed class ScriptedClient : IDisposable
    {
        private static long nextSteamSeed = 0x5EED0000;

        private readonly string host;
        private readonly int port;
        private readonly string mapsDir;
        private TcpClient tcp;
        private NetworkStream stream;
        private EndianBinaryReader reader;
        private EndianBinaryWriter writer;
        private readonly object writeLock = new object();
        private readonly object sync = new object();
        private readonly List<ServerPacket> packets = new List<ServerPacket>();
        private Thread readerThread;
        private Exception readerError;
        private bool closed;
        private int cFrame = -1;
        private byte[] levelBytes;
        private int levelPos;

        /// <summary>Fires on the reader thread for every decoded packet.</summary>
        public event Action<ServerPacket> PacketReceived;

        /// <summary>Reply to server pings (packet 0) automatically; default true.</summary>
        public bool AutoPing = true;

        /// <summary>The Steam id used by <see cref="JoinGame"/>; unique per client instance.</summary>
        public ulong SteamId;
        /// <summary>Player name given to JoinGame/Login.</summary>
        public string Name { get; private set; }
        /// <summary>Map name announced by packet 2.</summary>
        public string MapName { get; private set; }
        /// <summary>Width/height of the map (from the local .map) once known; 0 otherwise.</summary>
        public int MapWidth { get; private set; }
        public int MapHeight { get; private set; }
        /// <summary>Entity id of the local player once PlayerSpawn for our steam id arrived; -1 before.</summary>
        public short MyEntityId { get; private set; }
        /// <summary>Latest terrain bitfield from packet 3, or null.</summary>
        public TerrainSnapshot Terrain { get; private set; }
        /// <summary>Latest frame number received (packets 8/30).</summary>
        public int Frame { get; private set; }
        /// <summary>Map bytes received through 220-222 (without magic), or null.</summary>
        public byte[] ReceivedMapBytes { get; private set; }
        /// <summary>True once the server closed the connection or the reader stopped.</summary>
        public bool IsClosed { get { lock (sync) return closed; } }
        /// <summary>The disconnect packet if the server sent one.</summary>
        public DisconnectPacket Disconnect { get; private set; }

        /// <summary>Players known through PlayerSpawn (5), keyed by entity id.</summary>
        public readonly Dictionary<short, PlayerState> Players = new Dictionary<short, PlayerState>();
        /// <summary>Entities known through 4/40/42, positions updated by 30, velocities by 31, angles by 32.</summary>
        public readonly Dictionary<short, EntityState> Entities = new Dictionary<short, EntityState>();

        public sealed class PlayerState
        {
            public short EntityId;
            public ulong SteamId;
            public string Name;
            public float Health;
            public PlayerClass Class;
            public string[] Weapons;
            public int WeaponIndex;
            public int Score;
            public int Ping;
            public bool Removed;
            public override string ToString() { return Name + "#" + EntityId + " " + Class + " hp=" + Health + " score=" + Score; }
        }

        public sealed class EntityState
        {
            public short Id;
            public string TypeName;
            public Vec2 Position;
            public Vec2 Velocity;
            public byte Movement;
            public float ArmAngle;
            public bool IsPlayer;
            public short OwnerId = -1;
            public int CreatedFrame;
            public bool Removed;
            public bool Moving { get { return (Movement & 1) != 0; } }
            public bool Direction { get { return (Movement & 2) != 0; } }
            public bool Jumping { get { return (Movement & 4) != 0; } }
            public override string ToString() { return TypeName + "#" + Id + "@" + Position + " v=" + Velocity; }
        }

        /// <summary>Connects to a server started by the harness; maps are looked up in its scratch runtime.</summary>
        public ScriptedClient(ServerProcess server) : this("127.0.0.1", server.Port, server.Runtime.MapsDir)
        {
        }

        /// <summary>Connects to host:port; <paramref name="mapsDir"/> is where JoinGame looks for (and installs) maps, may be null.</summary>
        public ScriptedClient(string host, int port, string mapsDir)
        {
            this.host = host;
            this.port = port;
            this.mapsDir = mapsDir;
            MyEntityId = -1;
            SteamId = (ulong)Interlocked.Increment(ref nextSteamSeed) * 1000003UL + (ulong)Environment.ProcessId;
            tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.Connect(host, port);
            stream = tcp.GetStream();
            reader = new EndianBinaryReader(EndianBitConverter.Little, stream);
            writer = new EndianBinaryWriter(EndianBitConverter.Little, stream);
            readerThread = new Thread(ReadLoop);
            readerThread.Name = "ScriptedClient " + port;
            readerThread.IsBackground = true;
            readerThread.Start();
        }

        // ---------------------------------------------------------------- probe

        /// <summary>
        /// The status probe: a fresh connection sends byte 255 and reads one
        /// bool (server ready), after which the server closes the socket.
        /// Returns null when nothing answered within the timeout.
        /// </summary>
        public static bool? Probe(string host, int port, TimeSpan timeout)
        {
            try
            {
                using (TcpClient t = new TcpClient())
                {
                    t.Connect(host, port);
                    t.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                    NetworkStream s = t.GetStream();
                    s.WriteByte(Protocol.C2S.StatusProbe);
                    s.Flush();
                    int b = s.ReadByte();
                    if (b < 0)
                        return null;
                    return b != 0;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Probe on a raw connection: returns the reply byte and whether the server then closed the socket.</summary>
        public static bool ProbeAndCheckClosed(string host, int port, TimeSpan timeout, out int replyByte)
        {
            using (TcpClient t = new TcpClient())
            {
                t.Connect(host, port);
                t.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                NetworkStream s = t.GetStream();
                s.WriteByte(Protocol.C2S.StatusProbe);
                s.Flush();
                replyByte = s.ReadByte();
                int next = s.ReadByte(); // -1 = orderly close
                return next == -1;
            }
        }

        // ---------------------------------------------------------------- sends

        private void Flush()
        {
            writer.Flush();
            stream.Flush();
        }

        /// <summary>Packet 0: ping reply.</summary>
        public void SendPingReply()
        {
            lock (writeLock) { writer.Write(Protocol.C2S.PingReply); Flush(); }
        }

        /// <summary>Packet 1: login with an empty auth ticket (what the offline Steam shim produces).</summary>
        public void Login(ulong steamId, string name)
        {
            Login(steamId, name, new byte[0]);
        }

        public void Login(ulong steamId, string name, byte[] ticket)
        {
            Name = name;
            SteamId = steamId;
            lock (writeLock)
            {
                writer.Write(Protocol.C2S.Login);
                writer.Write(steamId);
                writer.Write(name);
                writer.Write(ticket.Length);
                writer.Write(ticket);
                Flush();
            }
        }

        /// <summary>Packet 2: ready flag plus the 16-byte map MD5 (zeros when not ready).</summary>
        public void Status(bool ready, byte[] md5)
        {
            if (md5 == null)
                md5 = new byte[16];
            if (md5.Length != 16)
                throw new ArgumentException("md5 must be 16 bytes");
            lock (writeLock)
            {
                writer.Write(Protocol.C2S.Status);
                writer.Write(ready);
                writer.Write(md5);
                Flush();
            }
        }

        /// <summary>Packet 10: legacy weapon select (server selects, no echo).</summary>
        public void SendWeaponSelectLegacy(int index)
        {
            lock (writeLock) { writer.Write(Protocol.C2S.SelectWeaponLegacy); writer.Write((byte)index); Flush(); }
        }

        /// <summary>Packet 11: button 0/1/2 (L/M/R), 255 fire grapple, 254 release; down 1/0; arm angle in radians.</summary>
        public void SendWeaponActivate(int button, bool down, float armAngle)
        {
            lock (writeLock)
            {
                writer.Write(Protocol.C2S.WeaponActivate);
                writer.Write((byte)button);
                writer.Write((byte)(down ? 1 : 0));
                writer.Write(armAngle);
                Flush();
            }
        }

        /// <summary>Packet 12: a KeyAction (int16 ordinal in declaration order).</summary>
        public void SendWeaponAction(KeyAction action)
        {
            short ordinal = (short)Array.IndexOf(Enum.GetValues(typeof(KeyAction)), action);
            SendWeaponActionRaw(ordinal);
        }

        public void SendWeaponActionRaw(short ordinal)
        {
            lock (writeLock) { writer.Write(Protocol.C2S.WeaponAction); writer.Write(ordinal); Flush(); }
        }

        /// <summary>Packet 13: select weapon (echoed to the other clients).</summary>
        public void SendWeaponSelect(int index)
        {
            lock (writeLock) { writer.Write(Protocol.C2S.SelectWeapon); writer.Write((byte)index); Flush(); }
        }

        /// <summary>Packet 14: position unchanged.</summary>
        public void SendPositionUnchanged()
        {
            lock (writeLock) { writer.Write(Protocol.C2S.PositionUnchanged); Flush(); }
        }

        /// <summary>Packet 15: position unchanged plus arm angle and movement bits.</summary>
        public void SendPositionUnchanged(float armAngle, bool moving, bool direction, bool jumping)
        {
            lock (writeLock)
            {
                writer.Write(Protocol.C2S.PositionUnchangedMovement);
                WriteMovement(armAngle, moving, direction, jumping);
                Flush();
            }
        }

        /// <summary>Packet 16: position delta (sbyte each).</summary>
        public void SendPositionDelta(int dx, int dy)
        {
            lock (writeLock) { writer.Write(Protocol.C2S.PositionDelta); writer.Write((sbyte)dx); writer.Write((sbyte)dy); Flush(); }
        }

        /// <summary>Packet 17: position delta plus movement.</summary>
        public void SendPositionDelta(int dx, int dy, float armAngle, bool moving, bool direction, bool jumping)
        {
            lock (writeLock)
            {
                writer.Write(Protocol.C2S.PositionDeltaMovement);
                writer.Write((sbyte)dx);
                writer.Write((sbyte)dy);
                WriteMovement(armAngle, moving, direction, jumping);
                Flush();
            }
        }

        /// <summary>Packet 18: absolute position (int16 x, int16 y).</summary>
        public void SendPositionAbsolute(int x, int y)
        {
            lock (writeLock) { writer.Write(Protocol.C2S.PositionAbsolute); writer.Write((short)x); writer.Write((short)y); Flush(); }
        }

        public void SendPositionAbsolute(Vec2 v)
        {
            SendPositionAbsolute((int)v.X, (int)v.Y);
        }

        /// <summary>Packet 19: absolute position plus movement.</summary>
        public void SendPositionAbsolute(int x, int y, float armAngle, bool moving, bool direction, bool jumping)
        {
            lock (writeLock)
            {
                writer.Write(Protocol.C2S.PositionAbsoluteMovement);
                writer.Write((short)x);
                writer.Write((short)y);
                WriteMovement(armAngle, moving, direction, jumping);
                Flush();
            }
        }

        private void WriteMovement(float armAngle, bool moving, bool direction, bool jumping)
        {
            writer.Write(Protocol.EncodeAngle(armAngle));
            writer.Write(Protocol.EncodeMovement(moving, direction, jumping));
        }

        /// <summary>Packet 20: hitscan from origin at angle (radians).</summary>
        public void SendHitscan(float angle, int originX, int originY)
        {
            lock (writeLock)
            {
                writer.Write(Protocol.C2S.Hitscan);
                writer.Write(Protocol.EncodeAngle(angle));
                writer.Write((short)originX);
                writer.Write((short)originY);
                Flush();
            }
        }

        /// <summary>Packet 60: chat line or /command.</summary>
        public void SendChat(string message)
        {
            lock (writeLock) { writer.Write(Protocol.C2S.Chat); writer.Write(message); Flush(); }
        }

        /// <summary>Writes arbitrary bytes (for malformed/unknown packet tests).</summary>
        public void SendRaw(params byte[] bytes)
        {
            lock (writeLock) { writer.Write(bytes); Flush(); }
        }

        // ---------------------------------------------------------------- join

        /// <summary>
        /// The whole handshake with a fresh unique steam id: Login, wait for
        /// ServerID (protocol 3), Status(true, md5 of Maps/&lt;map&gt;.map), or when
        /// the map is missing locally Status(false) then download it through
        /// 220-222, save it and send Status(true, md5); then wait for terrain,
        /// entity list, our PlayerSpawn and LevelFinish. Throws
        /// <see cref="ServerDisconnectedException"/> if the server refuses.
        /// </summary>
        public void JoinGame(string name)
        {
            JoinGame(name, SteamId, TimeSpan.FromSeconds(30));
        }

        public void JoinGame(string name, ulong steamId, TimeSpan timeout)
        {
            Login(steamId, name);
            ServerIdPacket sid = WaitFor<ServerIdPacket>(null, timeout);
            if (sid.ProtocolVersion != Protocol.ProtocolVersion)
                throw new ProtocolException("server protocol version " + sid.ProtocolVersion + ", expected " + Protocol.ProtocolVersion);
            string mapPath = mapsDir != null ? Path.Combine(mapsDir, sid.MapName + ".map") : null;
            if (mapPath == null || !File.Exists(mapPath))
            {
                Status(false, null);
                LevelEndPacket end = WaitFor<LevelEndPacket>(null, timeout);
                if (mapPath != null)
                {
                    Directory.CreateDirectory(mapsDir);
                    using (FileStream fs = File.Open(mapPath, FileMode.Create))
                    {
                        new BinaryWriter(fs).Write(Protocol.MapMagic);
                        fs.Write(end.MapBytes, 0, end.MapBytes.Length);
                    }
                }
            }
            byte[] md5 = mapPath != null ? MapFile.Md5(mapPath) : new byte[16];
            if (mapPath != null)
            {
                MapFile mf = MapFile.Read(mapPath);
                MapWidth = mf.Width;
                MapHeight = mf.Height;
            }
            Status(true, md5);
            WaitFor<TerrainStatePacket>(null, timeout);
            WaitFor<EntityListPacket>(null, timeout);
            WaitFor<PlayerSpawnPacket>(p => p.SteamId == steamId, timeout);
            WaitFor<LevelFinishPacket>(null, timeout);
        }

        /// <summary>The local player's state (after JoinGame), or null.</summary>
        public PlayerState Me
        {
            get { lock (sync) { PlayerState p; return MyEntityId >= 0 && Players.TryGetValue(MyEntityId, out p) ? p : null; } }
        }

        /// <summary>The local player's entity (after JoinGame), or null.</summary>
        public EntityState MyEntity
        {
            get { lock (sync) { EntityState e; return MyEntityId >= 0 && Entities.TryGetValue(MyEntityId, out e) ? e : null; } }
        }

        /// <summary>The player named <paramref name="name"/>, or null.</summary>
        public PlayerState PlayerNamed(string name)
        {
            lock (sync)
            {
                foreach (PlayerState p in Players.Values)
                    if (p.Name == name && !p.Removed)
                        return p;
            }
            return null;
        }

        // ---------------------------------------------------------------- waits

        /// <summary>Number of packets received so far (use as the start index of a later WaitFor).</summary>
        public int PacketCount { get { lock (sync) return packets.Count; } }

        /// <summary>Snapshot of every packet received so far.</summary>
        public List<ServerPacket> AllPackets()
        {
            lock (sync) return new List<ServerPacket>(packets);
        }

        /// <summary>Snapshot of the received packets of one type.</summary>
        public List<T> Packets<T>() where T : ServerPacket
        {
            return Packets<T>(0);
        }

        public List<T> Packets<T>(int startIndex) where T : ServerPacket
        {
            List<T> r = new List<T>();
            lock (sync)
            {
                for (int i = startIndex; i < packets.Count; i++)
                {
                    T t = packets[i] as T;
                    if (t != null)
                        r.Add(t);
                }
            }
            return r;
        }

        /// <summary>Waits for a packet of type T matching the predicate (null = any), among packets received at any time.</summary>
        public T WaitFor<T>(Func<T, bool> predicate, TimeSpan timeout) where T : ServerPacket
        {
            return WaitFor<T>(predicate, timeout, 0);
        }

        public T WaitFor<T>(Func<T, bool> predicate, double seconds) where T : ServerPacket
        {
            return WaitFor<T>(predicate, TimeSpan.FromSeconds(seconds), 0);
        }

        /// <summary>Waits for a matching packet received after the current one (ignores history).</summary>
        public T WaitForNext<T>(Func<T, bool> predicate, TimeSpan timeout) where T : ServerPacket
        {
            return WaitFor<T>(predicate, timeout, PacketCount);
        }

        public T WaitForNext<T>(Func<T, bool> predicate, double seconds) where T : ServerPacket
        {
            return WaitFor<T>(predicate, TimeSpan.FromSeconds(seconds), PacketCount);
        }

        /// <summary>
        /// Waits for a matching packet at index &gt;= startIndex. Throws
        /// TimeoutException, ServerDisconnectedException (packet 254 seen or
        /// socket closed with no match) or the reader's ProtocolException.
        /// </summary>
        public T WaitFor<T>(Func<T, bool> predicate, TimeSpan timeout, int startIndex) where T : ServerPacket
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            lock (sync)
            {
                int scanned = startIndex;
                while (true)
                {
                    for (; scanned < packets.Count; scanned++)
                    {
                        T t = packets[scanned] as T;
                        if (t != null && (predicate == null || predicate(t)))
                            return t;
                    }
                    if (readerError != null)
                        throw readerError is ProtocolException
                            ? new ProtocolException(readerError.Message + " (while waiting for " + typeof(T).Name + ")")
                            : new IOException("reader failed while waiting for " + typeof(T).Name + ": " + readerError.Message, readerError);
                    if (typeof(T) != typeof(DisconnectPacket))
                    {
                        for (int i = startIndex; i < packets.Count; i++)
                        {
                            DisconnectPacket d = packets[i] as DisconnectPacket;
                            if (d != null)
                                throw new ServerDisconnectedException(d.Reason, "server disconnected us (\"" + d.Reason + "\") while waiting for " + typeof(T).Name);
                        }
                    }
                    if (closed)
                        throw new ServerDisconnectedException(null, "connection closed while waiting for " + typeof(T).Name + "; last packets: " + Tail(5));
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                        throw new TimeoutException("no " + typeof(T).Name + (predicate != null ? " matching the predicate" : "") + " within " + timeout.TotalSeconds + " s; " + packets.Count + " packets received, last: " + Tail(5));
                    Monitor.Wait(sync, left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250));
                }
            }
        }

        /// <summary>Returns true when no packet of type T matching the predicate arrives within the timeout (for negative assertions).</summary>
        public bool NoneWithin<T>(Func<T, bool> predicate, TimeSpan timeout, int startIndex) where T : ServerPacket
        {
            try
            {
                WaitFor<T>(predicate, timeout, startIndex);
                return false;
            }
            catch (TimeoutException)
            {
                return true;
            }
        }

        /// <summary>Waits until the socket is closed by the server; returns false on timeout.</summary>
        public bool WaitForClose(TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            lock (sync)
            {
                while (!closed)
                {
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                        return false;
                    Monitor.Wait(sync, left < TimeSpan.FromMilliseconds(250) ? left : TimeSpan.FromMilliseconds(250));
                }
                return true;
            }
        }

        /// <summary>Polls a condition over the client's state until it holds; false on timeout.</summary>
        public bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                lock (sync)
                {
                    if (condition())
                        return true;
                }
                if (DateTime.UtcNow >= deadline)
                    return false;
                lock (sync)
                {
                    Monitor.Wait(sync, 50);
                }
            }
        }

        private string Tail(int n)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = Math.Max(0, packets.Count - n); i < packets.Count; i++)
                sb.Append(packets[i]).Append(' ');
            return sb.ToString().TrimEnd();
        }

        /// <summary>The reader's error, if it stopped with one.</summary>
        public Exception ReaderError { get { lock (sync) return readerError; } }

        // ---------------------------------------------------------------- reader

        private void ReadLoop()
        {
            try
            {
                while (true)
                {
                    int cmd = reader.ReadByte();
                    ServerPacket p = Decode((byte)cmd);
                    p.Id = (byte)cmd;
                    p.ReceivedAt = DateTime.UtcNow;
                    if (AutoPing && p is PingPacket)
                        SendPingReply();
                    lock (sync)
                    {
                        p.Sequence = packets.Count;
                        packets.Add(p);
                        Apply(p);
                        Monitor.PulseAll(sync);
                    }
                    Action<ServerPacket> h = PacketReceived;
                    if (h != null)
                        h(p);
                }
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    bool expected = ex is EndOfStreamException || ex is IOException || ex is ObjectDisposedException || ex is SocketException;
                    if (!expected || (ex is ProtocolException))
                        readerError = ex;
                    closed = true;
                    Monitor.PulseAll(sync);
                }
            }
        }

        private Vec2 ReadVec2()
        {
            float x = reader.ReadInt16();
            float y = reader.ReadInt16();
            return new Vec2((int)x, (int)y);
        }

        private int ReadFrameByte()
        {
            if (cFrame != -1)
                return cFrame + reader.ReadSByte();
            return reader.ReadInt32();
        }

        private static PlayerClass ReadEnumClass(byte ordinal)
        {
            return (PlayerClass)Enum.GetValues(typeof(PlayerClass)).GetValue(ordinal);
        }

        private ServerPacket Decode(byte cmd)
        {
            switch (cmd)
            {
                case Protocol.S2C.Ping:
                    return new PingPacket();
                case Protocol.S2C.ServerId:
                {
                    ServerIdPacket p = new ServerIdPacket();
                    p.ProtocolVersion = reader.ReadByte();
                    p.MapName = reader.ReadString();
                    return p;
                }
                case Protocol.S2C.TerrainState:
                {
                    TerrainStatePacket p = new TerrainStatePacket();
                    p.CompressedLength = reader.ReadInt32();
                    p.Compressed = reader.ReadBytes(p.CompressedLength);
                    p.Bits = SevenZipHelper.Decompress(p.Compressed);
                    return p;
                }
                case Protocol.S2C.EntityList:
                {
                    EntityListPacket p = new EntityListPacket();
                    int count = reader.ReadInt16();
                    for (int i = 0; i < count; i++)
                    {
                        EntityListEntry e = new EntityListEntry();
                        e.Id = reader.ReadInt16();
                        e.TypeIndex = reader.ReadByte();
                        e.TypeName = Protocol.EntityTypeName(e.TypeIndex);
                        e.Position = ReadVec2();
                        if (e.TypeName == "HumanoidEntity")
                            e.Class = ReadEnumClass(reader.ReadByte());
                        p.Entities.Add(e);
                    }
                    return p;
                }
                case Protocol.S2C.PlayerSpawn:
                {
                    PlayerSpawnPacket p = new PlayerSpawnPacket();
                    p.EntityId = reader.ReadInt16();
                    p.SteamId = reader.ReadUInt64();
                    p.Name = reader.ReadString();
                    p.Health = reader.ReadSingle();
                    p.Class = ReadEnumClass(reader.ReadByte());
                    int n = reader.ReadByte();
                    p.WeaponTypeIndexes = new byte[n];
                    p.Weapons = new string[n];
                    for (int i = 0; i < n; i++)
                    {
                        p.WeaponTypeIndexes[i] = reader.ReadByte();
                        p.Weapons[i] = Protocol.EntityTypeName(p.WeaponTypeIndexes[i]);
                    }
                    p.WeaponIndex = reader.ReadByte();
                    return p;
                }
                case Protocol.S2C.FrameSync:
                {
                    FrameSyncPacket p = new FrameSyncPacket();
                    p.Absolute = cFrame == -1;
                    p.Frame = cFrame = ReadFrameByte();
                    return p;
                }
                case Protocol.S2C.LevelFinish:
                    return new LevelFinishPacket();
                case Protocol.S2C.Ammo:
                {
                    AmmoPacket p = new AmmoPacket();
                    p.WeaponIndex = reader.ReadByte();
                    p.TotalAmmo = reader.ReadByte();
                    p.ClipAmmo = reader.ReadByte();
                    return p;
                }
                case Protocol.S2C.WeaponSelect:
                {
                    WeaponSelectPacket p = new WeaponSelectPacket();
                    p.EntityId = reader.ReadInt16();
                    p.Index = reader.ReadByte();
                    return p;
                }
                case Protocol.S2C.WeaponFire:
                {
                    WeaponFirePacket p = new WeaponFirePacket();
                    p.EntityId = reader.ReadInt16();
                    p.Index = reader.ReadByte();
                    return p;
                }
                case Protocol.S2C.Explode:
                {
                    ExplodePacket p = new ExplodePacket();
                    p.Frame = reader.ReadInt32();
                    p.Position = ReadVec2();
                    p.Radius = reader.ReadInt16();
                    p.Seed = reader.ReadInt32();
                    p.Nonlethal = reader.ReadBoolean();
                    return p;
                }
                case Protocol.S2C.Teleport:
                {
                    TeleportPacket p = new TeleportPacket();
                    p.Position = ReadVec2();
                    return p;
                }
                case Protocol.S2C.Hitscan:
                {
                    HitscanPacket p = new HitscanPacket();
                    p.EntityId = reader.ReadInt16();
                    return p;
                }
                case Protocol.S2C.ClassChange:
                {
                    ClassChangePacket p = new ClassChangePacket();
                    p.EntityId = reader.ReadInt16();
                    p.Class = ReadEnumClass(reader.ReadByte());
                    int n = reader.ReadByte();
                    p.Weapons = new string[n];
                    for (int i = 0; i < n; i++)
                        p.Weapons[i] = Protocol.EntityTypeName(reader.ReadByte());
                    return p;
                }
                case Protocol.S2C.Hook:
                {
                    HookPacket p = new HookPacket();
                    p.Frame = reader.ReadInt32();
                    p.PlayerEntityId = reader.ReadInt16();
                    p.HookEntityId = reader.ReadInt16();
                    return p;
                }
                case Protocol.S2C.Positions:
                {
                    PositionsPacket p = new PositionsPacket();
                    p.Frame = cFrame = ReadFrameByte();
                    int n = reader.ReadInt16();
                    for (int i = 0; i < n; i++)
                    {
                        EntityMotion m = new EntityMotion();
                        m.Id = reader.ReadInt16();
                        m.Vector = ReadVec2();
                        m.MovementByte = reader.ReadByte();
                        p.Entries.Add(m);
                    }
                    return p;
                }
                case Protocol.S2C.Velocities:
                {
                    VelocitiesPacket p = new VelocitiesPacket();
                    int n = reader.ReadInt16();
                    for (int i = 0; i < n; i++)
                    {
                        EntityMotion m = new EntityMotion();
                        m.Id = reader.ReadInt16();
                        m.Vector = ReadVec2();
                        m.MovementByte = reader.ReadByte();
                        p.Entries.Add(m);
                    }
                    return p;
                }
                case Protocol.S2C.Angles:
                {
                    AnglesPacket p = new AnglesPacket();
                    int n = reader.ReadInt16();
                    for (int i = 0; i < n; i++)
                    {
                        AngleEntry a = new AngleEntry();
                        a.Id = reader.ReadInt16();
                        a.Raw = reader.ReadSByte();
                        a.Angle = Protocol.DecodeAngle(a.Raw);
                        p.Entries.Add(a);
                    }
                    return p;
                }
                case Protocol.S2C.EntityCreate:
                {
                    EntityCreatePacket p = new EntityCreatePacket();
                    p.Frame = reader.ReadInt32();
                    p.EntityId = reader.ReadInt16();
                    p.TypeIndex = reader.ReadByte();
                    p.TypeName = Protocol.EntityTypeName(p.TypeIndex);
                    p.Position = ReadVec2();
                    p.IsPlayer = reader.ReadBoolean();
                    return p;
                }
                case Protocol.S2C.EntityRemove:
                {
                    EntityRemovePacket p = new EntityRemovePacket();
                    p.Frame = reader.ReadInt32();
                    p.EntityId = reader.ReadInt16();
                    return p;
                }
                case Protocol.S2C.ProjectileCreate:
                {
                    ProjectileCreatePacket p = new ProjectileCreatePacket();
                    p.Frame = reader.ReadInt32();
                    p.EntityId = reader.ReadInt16();
                    p.TypeIndex = reader.ReadByte();
                    p.TypeName = Protocol.EntityTypeName(p.TypeIndex);
                    p.Position = ReadVec2();
                    p.Angle = reader.ReadSingle();
                    p.OwnerId = reader.ReadInt16();
                    return p;
                }
                case Protocol.S2C.Chat:
                {
                    ChatPacket p = new ChatPacket();
                    p.Text = reader.ReadString();
                    return p;
                }
                case Protocol.S2C.Message:
                {
                    MessagePacket p = new MessagePacket();
                    p.MessageId = reader.ReadByte();
                    int n = reader.ReadByte();
                    p.Args = new string[n];
                    for (int i = 0; i < n; i++)
                        p.Args[i] = reader.ReadString();
                    p.DurationMs = reader.ReadInt16();
                    return p;
                }
                case Protocol.S2C.Sound:
                {
                    SoundPacket p = new SoundPacket();
                    p.Frame = reader.ReadInt32();
                    p.SoundId = reader.ReadByte();
                    p.EntityId = reader.ReadInt16();
                    return p;
                }
                case Protocol.S2C.Health:
                {
                    HealthPacket p = new HealthPacket();
                    p.EntityId = reader.ReadInt16();
                    p.Health = reader.ReadSingle();
                    return p;
                }
                case Protocol.S2C.GameModeByte:
                {
                    GameModeBytePacket p = new GameModeBytePacket();
                    p.Command = reader.ReadByte();
                    p.Value = reader.ReadByte();
                    return p;
                }
                case Protocol.S2C.GameModeShort:
                {
                    GameModeShortPacket p = new GameModeShortPacket();
                    p.Command = reader.ReadByte();
                    p.Value = reader.ReadInt16();
                    return p;
                }
                case Protocol.S2C.GameModeString:
                {
                    GameModeStringPacket p = new GameModeStringPacket();
                    p.Command = reader.ReadByte();
                    p.Value = reader.ReadString();
                    return p;
                }
                case Protocol.S2C.PingTimes:
                {
                    PingTimesPacket p = new PingTimesPacket();
                    int n = reader.ReadByte();
                    for (int i = 0; i < n; i++)
                    {
                        short id = reader.ReadInt16();
                        short ms = reader.ReadInt16();
                        p.Entries.Add(new KeyValuePair<short, short>(id, ms));
                    }
                    return p;
                }
                case Protocol.S2C.Score:
                {
                    ScorePacket p = new ScorePacket();
                    p.EntityId = reader.ReadInt16();
                    p.Score = reader.ReadInt16();
                    return p;
                }
                case Protocol.S2C.LevelBegin:
                {
                    LevelBeginPacket p = new LevelBeginPacket();
                    p.TotalLength = reader.ReadInt32();
                    levelBytes = new byte[p.TotalLength];
                    levelPos = 0;
                    return p;
                }
                case Protocol.S2C.LevelChunk:
                {
                    LevelChunkPacket p = new LevelChunkPacket();
                    p.Length = reader.ReadInt16();
                    p.Bytes = reader.ReadBytes(p.Length);
                    if (levelBytes == null)
                        throw new ProtocolException("level chunk (221) before level begin (220)");
                    if (levelPos + p.Length > levelBytes.Length)
                        throw new ProtocolException("level chunks exceed the announced length " + levelBytes.Length);
                    Array.Copy(p.Bytes, 0, levelBytes, levelPos, p.Length);
                    levelPos += p.Length;
                    return p;
                }
                case Protocol.S2C.LevelEnd:
                {
                    LevelEndPacket p = new LevelEndPacket();
                    if (levelBytes == null)
                        throw new ProtocolException("level end (222) before level begin (220)");
                    if (levelPos != levelBytes.Length)
                        throw new ProtocolException("level end after " + levelPos + " of " + levelBytes.Length + " bytes");
                    p.MapBytes = levelBytes;
                    levelBytes = null;
                    return p;
                }
                case Protocol.S2C.LevelChanging:
                    return new LevelChangingPacket();
                case Protocol.S2C.Disconnect:
                {
                    DisconnectPacket p = new DisconnectPacket();
                    p.Reason = reader.ReadString();
                    return p;
                }
                default:
                    throw new ProtocolException("unknown server packet id " + cmd + " (protocol regression: docs/PROTOCOL.md lists no such packet)");
            }
        }

        /// <summary>Updates Players/Entities/Terrain from a decoded packet (called under the lock).</summary>
        private void Apply(ServerPacket packet)
        {
            EntityState e;
            PlayerState pl;
            switch (packet.Id)
            {
                case Protocol.S2C.ServerId:
                    MapName = ((ServerIdPacket)packet).MapName;
                    break;
                case Protocol.S2C.TerrainState:
                    Terrain = new TerrainSnapshot(((TerrainStatePacket)packet).Bits, MapWidth, MapHeight);
                    break;
                case Protocol.S2C.EntityList:
                    foreach (EntityListEntry le in ((EntityListPacket)packet).Entities)
                    {
                        e = new EntityState();
                        e.Id = le.Id;
                        e.TypeName = le.TypeName;
                        e.Position = le.Position;
                        Entities[le.Id] = e;
                    }
                    break;
                case Protocol.S2C.PlayerSpawn:
                {
                    PlayerSpawnPacket p = (PlayerSpawnPacket)packet;
                    pl = new PlayerState();
                    pl.EntityId = p.EntityId;
                    pl.SteamId = p.SteamId;
                    pl.Name = p.Name;
                    pl.Health = p.Health;
                    pl.Class = p.Class;
                    pl.Weapons = p.Weapons;
                    pl.WeaponIndex = p.WeaponIndex;
                    Players[p.EntityId] = pl;
                    if (p.SteamId == SteamId)
                        MyEntityId = p.EntityId;
                    if (Entities.TryGetValue(p.EntityId, out e))
                        e.IsPlayer = true;
                    break;
                }
                case Protocol.S2C.FrameSync:
                    Frame = ((FrameSyncPacket)packet).Frame;
                    break;
                case Protocol.S2C.WeaponSelect:
                {
                    WeaponSelectPacket p = (WeaponSelectPacket)packet;
                    if (Players.TryGetValue(p.EntityId, out pl))
                        pl.WeaponIndex = p.Index;
                    break;
                }
                case Protocol.S2C.Teleport:
                    if (MyEntityId >= 0 && Entities.TryGetValue(MyEntityId, out e))
                        e.Position = ((TeleportPacket)packet).Position;
                    break;
                case Protocol.S2C.ClassChange:
                {
                    ClassChangePacket p = (ClassChangePacket)packet;
                    short id = p.EntityId == -1 ? MyEntityId : p.EntityId;
                    if (Players.TryGetValue(id, out pl))
                    {
                        pl.Class = p.Class;
                        pl.Weapons = p.Weapons;
                        pl.WeaponIndex = 0;
                    }
                    break;
                }
                case Protocol.S2C.Positions:
                {
                    PositionsPacket p = (PositionsPacket)packet;
                    Frame = p.Frame;
                    foreach (EntityMotion m in p.Entries)
                        if (Entities.TryGetValue(m.Id, out e))
                        {
                            e.Position = m.Vector;
                            e.Movement = m.MovementByte;
                        }
                    break;
                }
                case Protocol.S2C.Velocities:
                    foreach (EntityMotion m in ((VelocitiesPacket)packet).Entries)
                        if (Entities.TryGetValue(m.Id, out e))
                        {
                            e.Velocity = m.Vector;
                            e.Movement = m.MovementByte;
                        }
                    break;
                case Protocol.S2C.Angles:
                    foreach (AngleEntry a in ((AnglesPacket)packet).Entries)
                        if (Entities.TryGetValue(a.Id, out e))
                            e.ArmAngle = a.Angle;
                    break;
                case Protocol.S2C.EntityCreate:
                {
                    EntityCreatePacket p = (EntityCreatePacket)packet;
                    e = new EntityState();
                    e.Id = p.EntityId;
                    e.TypeName = p.TypeName;
                    e.Position = p.Position;
                    e.IsPlayer = p.IsPlayer;
                    e.CreatedFrame = p.Frame;
                    Entities[p.EntityId] = e;
                    break;
                }
                case Protocol.S2C.EntityRemove:
                {
                    EntityRemovePacket p = (EntityRemovePacket)packet;
                    if (Entities.TryGetValue(p.EntityId, out e))
                    {
                        e.Removed = true;
                        Entities.Remove(p.EntityId);
                    }
                    if (Players.TryGetValue(p.EntityId, out pl))
                    {
                        pl.Removed = true;
                        Players.Remove(p.EntityId);
                    }
                    break;
                }
                case Protocol.S2C.ProjectileCreate:
                {
                    ProjectileCreatePacket p = (ProjectileCreatePacket)packet;
                    e = new EntityState();
                    e.Id = p.EntityId;
                    e.TypeName = p.TypeName;
                    e.Position = p.Position;
                    e.OwnerId = p.OwnerId;
                    e.CreatedFrame = p.Frame;
                    Entities[p.EntityId] = e;
                    break;
                }
                case Protocol.S2C.Health:
                {
                    HealthPacket p = (HealthPacket)packet;
                    if (Players.TryGetValue(p.EntityId, out pl))
                        pl.Health = p.Health;
                    break;
                }
                case Protocol.S2C.PingTimes:
                    foreach (KeyValuePair<short, short> kv in ((PingTimesPacket)packet).Entries)
                        if (Players.TryGetValue(kv.Key, out pl))
                            pl.Ping = kv.Value;
                    break;
                case Protocol.S2C.Score:
                {
                    ScorePacket p = (ScorePacket)packet;
                    if (Players.TryGetValue(p.EntityId, out pl))
                        pl.Score = p.Score;
                    break;
                }
                case Protocol.S2C.LevelEnd:
                    ReceivedMapBytes = ((LevelEndPacket)packet).MapBytes;
                    break;
                case Protocol.S2C.Disconnect:
                    Disconnect = (DisconnectPacket)packet;
                    break;
            }
        }

        /// <summary>Closes the socket (the server sees a disconnect).</summary>
        public void Close()
        {
            lock (sync)
            {
                if (closed)
                    return;
                closed = true;
                Monitor.PulseAll(sync);
            }
            try { tcp.Close(); } catch (Exception) { }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
