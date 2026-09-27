using System;
using System.Collections.Generic;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Status probe, login, the join sequence and the steady-state packets of
    /// a single client against the real server on bases (maxbots 1, so the
    /// level holds only the flags and the client's own humanoid).
    /// </summary>
    public class HandshakeTests : IClassFixture<SoloBasesFixture>
    {
        private readonly SoloBasesFixture fx;

        public HandshakeTests(SoloBasesFixture fx)
        {
            this.fx = fx;
        }

        // PROTO-01
        [Fact]
        public void Status_probe_answers_one_ready_byte_and_closes()
        {
            int reply;
            bool closed = ScriptedClient.ProbeAndCheckClosed("127.0.0.1", fx.Server.Port, TimeSpan.FromSeconds(5), out reply);
            Assert.Equal(1, reply);          // server.ready == true after "Ready for connections"
            Assert.True(closed, "the server must close the probe connection after the one byte (WriteData(true))");
            Assert.True(fx.Server.Console.ServerReady());
            // ready == false is only set inside setLevel, which first disconnects every
            // existing connection and constructs new ones only after the level is loaded,
            // so a probe can never observe 0 through the wire (see LevelChangeTests).
        }

        // PROTO-02
        [Fact]
        public void Login_is_answered_with_ServerID_protocol_3_and_the_map_name()
        {
            string name = Names.Unique("alice");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(12345UL, name);
                ServerIdPacket sid = c.WaitFor<ServerIdPacket>(null, 10);
                Assert.Equal(3, sid.ProtocolVersion);
                Assert.Equal("bases", sid.MapName);
                Assert.Equal(fx.Server.Console.ServerMap(), sid.MapName);
                Assert.NotNull(fx.Server.WaitFor(@"127\.0\.0\.1:\d+ logged in as " + name, 5));
                Assert.Equal(1, c.PacketCount); // nothing else until the client reports its status
            }
        }

        // PROTO-02: any Steam ticket is accepted (the auth failure path is commented out)
        [Fact]
        public void Login_with_a_garbage_steam_ticket_is_still_accepted()
        {
            string name = Names.Unique("ticket");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(777UL, name, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
                ServerIdPacket sid = c.WaitFor<ServerIdPacket>(null, 10);
                Assert.Equal(3, sid.ProtocolVersion);
                Assert.Empty(c.Packets<DisconnectPacket>());
            }
        }

        // PROTO-03, PROTO-21, PROTO-25
        [Fact]
        public void Ready_status_with_the_map_md5_triggers_the_join_sequence_in_order()
        {
            string name = Names.Unique("alice");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(12345UL, name);
                c.WaitFor<ServerIdPacket>(null, 10);
                byte[] md5 = MapFile.Md5(fx.Runtime.MapPath("bases"));
                c.Status(true, md5);
                c.WaitFor<LevelFinishPacket>(null, 30);
                // The game-mode state and the join announcement are flushed with the
                // "joined the game" chat line; ping times and scores follow in the same task.
                ChatPacket joined = c.WaitFor<ChatPacket>(p => p.Text == Colour.Orange + name + " joined the game", 10);
                c.WaitFor<ScorePacket>(null, 10);

                // --- order of everything after the login reply, pings excluded (the ping
                // thread writes independently of the join task)
                List<ServerPacket> seq = new List<ServerPacket>();
                foreach (ServerPacket p in c.AllPackets())
                    if (!(p is PingPacket) && !(p is ServerIdPacket))
                        seq.Add(p);
                Assert.IsType<TerrainStatePacket>(seq[0]);
                Assert.IsType<EntityListPacket>(seq[1]);
                Assert.IsType<PlayerSpawnPacket>(seq[2]);
                Assert.Equal(12345UL, ((PlayerSpawnPacket)seq[2]).SteamId);
                Assert.IsType<LevelFinishPacket>(seq[3]);        // maxbots 1: no other players, so no other 5s
                Assert.IsType<GameModeBytePacket>(seq[4]);
                Assert.IsType<GameModeBytePacket>(seq[5]);
                Assert.IsType<GameModeBytePacket>(seq[6]);
                Assert.Same(joined, seq[7]);
                int pingTimesAt = seq.FindIndex(p => p is PingTimesPacket);
                int scoreAt = seq.FindIndex(p => p is ScorePacket);
                Assert.True(pingTimesAt > 7 && scoreAt > pingTimesAt, "130 then 131 after the join chat; got " + string.Join(" ", seq));

                // --- 3: the terrain bitfield equals the reference for bases (LevelTerrainTests oracle)
                TerrainStatePacket terrain = (TerrainStatePacket)seq[0];
                Assert.Equal(terrain.CompressedLength, terrain.Compressed.Length);
                Assert.Equal(3914 * 1024 / 8, terrain.Bits.Length);
                Assert.Equal("059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3", new TerrainSnapshot(terrain.Bits, 3914, 1024).Sha256Hex());
                Assert.NotNull(fx.Server.WaitFor("Sent terrain state to " + name, 5));

                // --- 4: flags at their region positions (data.txt: flag_Green 951 387, flag_Blue 2933 387;
                // y = 1024 - 387 - 14), the client's own humanoid with a class byte, no projectiles
                EntityListPacket list = (EntityListPacket)seq[1];
                Assert.True(list.Entities.Count >= 3, "at least two flags and one humanoid: " + list);
                EntityListEntry blue = list.Entities.Find(e => e.TypeIndex == 0);
                EntityListEntry green = list.Entities.Find(e => e.TypeIndex == 3);
                Assert.NotNull(blue);
                Assert.NotNull(green);
                Assert.Equal("BlueFlagEntity", blue.TypeName);
                Assert.Equal(new Vec2(2933, 623), blue.Position);
                Assert.Equal(new Vec2(951, 623), green.Position);
                Assert.Null(blue.Class);
                PlayerSpawnPacket me = (PlayerSpawnPacket)seq[2];
                EntityListEntry self = list.Entities.Find(e => e.Id == me.EntityId);
                Assert.NotNull(self);
                Assert.Equal(4, self.TypeIndex);
                Assert.True(self.Class == PlayerClass.Green || self.Class == PlayerClass.Blue, "spawn class " + self.Class);
                Assert.DoesNotContain(list.Entities, e => e.TypeIndex == 2 || e.TypeIndex == 5 || e.TypeIndex == 12 || e.TypeIndex == 13);
                Assert.Equal(3, list.Entities.Count);   // maxbots 1: flags + self only

                // --- 5 (self): steam id, name, health, class, weapons from settings.txt, index 0
                Assert.Equal(name, me.Name);
                Assert.Equal(100f, me.Health);
                Assert.Equal(self.Class, me.Class);
                Assert.Equal(new byte[] { 6, 9, 10 }, me.WeaponTypeIndexes);
                Assert.Equal(new string[] { "RocketLauncher", "SMG", "Sword" }, me.Weapons);
                Assert.Equal(0, me.WeaponIndex);
                Assert.Equal(me.EntityId, c.MyEntityId);

                // --- 120s: blue score, green score, max captures (5 from the fixture's settings.txt)
                GameModeBytePacket g0 = (GameModeBytePacket)seq[4], g1 = (GameModeBytePacket)seq[5], g2 = (GameModeBytePacket)seq[6];
                Assert.Equal(3, g0.Command); Assert.Equal(0, g0.Value);
                Assert.Equal(2, g1.Command); Assert.Equal(0, g1.Value);
                Assert.Equal(4, g2.Command); Assert.Equal(5, g2.Value);
                Assert.Empty(c.Packets<GameModeShortPacket>());    // no carriers: no 121
                Assert.Empty(c.Packets<MessagePacket>());          // own PLAYER_JOIN goes to the others only

                // --- 130/131 carry our own entity id
                PingTimesPacket pt = (PingTimesPacket)seq[pingTimesAt];
                Assert.Contains(pt.Entries, e => e.Key == me.EntityId);
                Assert.Single(pt.Entries);                        // maxbots 1: we are the only ready player
                ScorePacket sc = (ScorePacket)seq[scoreAt];
                Assert.Equal(me.EntityId, sc.EntityId);
                Assert.Equal(0, sc.Score);
            }
        }

        // PROTO-11
        [Fact]
        public void Frame_sync_packets_carry_a_monotonic_60Hz_frame_about_every_100ms()
        {
            string name = Names.Unique("frame");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                int serverFrameBefore = fx.Server.Console.ServerFrame();
                c.JoinGame(name);
                int mark = c.PacketCount;
                // maxbots 1 and nothing moving: the server sends 8 (frame only) instead of 30
                ServerPacket first = null;
                DateTime t0 = DateTime.UtcNow;
                List<int> frames = new List<int>();
                List<DateTime> times = new List<DateTime>();
                bool got = c.WaitUntil(delegate()
                {
                    frames.Clear();
                    times.Clear();
                    foreach (ServerPacket p in c.AllPackets())
                    {
                        if (p.Sequence < mark)
                            continue;
                        if (p is FrameSyncPacket) { frames.Add(((FrameSyncPacket)p).Frame); times.Add(p.ReceivedAt); if (first == null) first = p; }
                        else if (p is PositionsPacket) { frames.Add(((PositionsPacket)p).Frame); times.Add(p.ReceivedAt); if (first == null) first = p; }
                    }
                    return times.Count >= 2 && (times[times.Count - 1] - times[0]).TotalSeconds >= 2.0;
                }, TimeSpan.FromSeconds(8));
                Assert.True(got, "expected frame packets for 2 s; got " + frames.Count);
                int serverFrameAfter = fx.Server.Console.ServerFrame();

                // the first FrameByte is absolute (int32): it must be a real server frame number
                Assert.True(frames[0] >= serverFrameBefore && frames[0] <= serverFrameAfter,
                    "first frame " + frames[0] + " outside the server's [" + serverFrameBefore + ", " + serverFrameAfter + "]");
                if (first is FrameSyncPacket)
                    Assert.True(((FrameSyncPacket)first).Absolute, "first packet 8 must carry an int32 frame");
                // afterwards sbyte deltas in [1, 127]: strictly increasing, small steps
                for (int i = 1; i < frames.Count; i++)
                {
                    int d = frames[i] - frames[i - 1];
                    Assert.True(d >= 1 && d <= 127, "frame delta " + d + " between packets " + (i - 1) + " and " + i);
                }
                double seconds = (times[times.Count - 1] - times[0]).TotalSeconds;
                double rate = (frames[frames.Count - 1] - frames[0]) / seconds;
                Assert.InRange(rate, 60 * 0.85, 60 * 1.15);
                Assert.True(frames.Count >= 15, "only " + frames.Count + " frame packets in " + seconds + " s");
                // and the client's tracked frame follows the server's within a few frames
                int tracked = c.Frame;
                int now = fx.Server.Console.ServerFrame();
                Assert.InRange(now - tracked, 0, 60);
            }
        }

        // PROTO-10 (a)
        [Fact]
        public void Server_pings_every_five_seconds_and_reports_ping_times_after_each_ping()
        {
            string name = Names.Unique("ping");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.JoinGame(name);
                DateTime joined = DateTime.UtcNow;
                PingPacket p1 = c.WaitFor<PingPacket>(null, TimeSpan.FromSeconds(6.5));
                Assert.True((p1.ReceivedAt - joined).TotalSeconds <= 5.6, "first ping " + (p1.ReceivedAt - joined).TotalSeconds + " s after joining");
                PingPacket p2 = c.WaitFor<PingPacket>(null, TimeSpan.FromSeconds(7), p1.Sequence + 1);
                double interval = (p2.ReceivedAt - p1.ReceivedAt).TotalSeconds;
                Assert.InRange(interval, 4.5, 6.0);
                foreach (PingPacket ping in new PingPacket[] { p1, p2 })
                {
                    PingTimesPacket times = c.WaitFor<PingTimesPacket>(null, TimeSpan.FromSeconds(2), ping.Sequence + 1);
                    Assert.Equal(ping.Sequence + 1, times.Sequence);   // 130 written right after the ping
                    Assert.True(times.Entries.Count >= 1);
                    KeyValuePair<short, short> mine = times.Entries.Find(e => e.Key == c.MyEntityId);
                    Assert.Equal(c.MyEntityId, mine.Key);
                    Assert.InRange(mine.Value, 0, 100);
                }
                Assert.Empty(c.Packets<DisconnectPacket>());
                Assert.False(c.IsClosed);
            }
        }

        // PROTO-24 (b)
        [Fact]
        public void Unknown_client_packet_id_is_ignored_and_the_connection_keeps_working()
        {
            string name = Names.Unique("unknown");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.JoinGame(name);
                int mark = c.PacketCount;
                int errorsBefore = fx.Server.Count("Error handling");
                c.SendRaw(99);
                c.SendChat("still here");
                ChatPacket chat = c.WaitFor<ChatPacket>(p => p.Text.EndsWith("> still here"), TimeSpan.FromSeconds(5), mark);
                Assert.Equal(Colour.ForClass(c.Me.Class) + name + Colour.White + "> still here", chat.Text);
                Assert.Empty(c.Packets<DisconnectPacket>());
                Assert.False(c.IsClosed);
                Assert.Equal(errorsBefore, fx.Server.Count("Error handling"));
            }
        }

        // PROTO-18: FrameList.GetFrame falls back to the last frame, so "Too much lag" is unreachable
        [Fact]
        public void Hitscan_before_any_ping_reply_is_not_treated_as_lag()
        {
            string name = Names.Unique("lag");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.AutoPing = false;        // lagFrames stays 0 and the server cannot measure a ping
                c.JoinGame(name);
                c.SendWeaponSelect(1);     // SMG
                Assert.True(Poll.Until(() => fx.Server.Console.ServerPlayer(name).WeaponIndex == 1, TimeSpan.FromSeconds(5)));
                ServerSide side = new ServerSide(fx.Server);
                Vec2 pivot = side.WeaponPivot(name);
                int mark = c.PacketCount;
                c.SendHitscan(0f, (int)pivot.X, (int)pivot.Y);
                c.SendChat("after hitscan");
                c.WaitFor<ChatPacket>(p => p.Text.EndsWith("> after hitscan"), TimeSpan.FromSeconds(5), mark);
                Assert.True(c.NoneWithin<DisconnectPacket>(null, TimeSpan.FromMilliseconds(500), mark));
                Assert.False(c.IsClosed);
                Assert.Equal(0, fx.Server.Count("Too much lag"));
            }
        }
    }
}
