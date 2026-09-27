using System;
using System.Collections.Generic;
using System.Globalization;
using Vexillum;
using Vexillum.Acceptance;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.physicsterrain
{
    /// <summary>
    /// Explosions on the real dedicated server: ServerLevel.Explode defers the
    /// crater by two frames and broadcasts packet 15, packet 3 carries the
    /// LZMA of ToBytes, and the damage formula as coded. Driven through the
    /// in-process debug console and the scripted protocol client.
    /// </summary>
    public class QuietBasesFixture : ServerFixture
    {
        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
            runtime.SetServerSetting("maxbots", "1");   // one client at a time: no bots, so no other explosions
        }
    }

    public class ServerTerrainSyncTests : IClassFixture<QuietBasesFixture>
    {
        private const string BasesSha = "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3";
        private const string HashScript =
            "Sync(() => { byte[] b = (byte[])Server.level.GetTerrainState(); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant(); })";

        private readonly QuietBasesFixture fx;

        public ServerTerrainSyncTests(QuietBasesFixture fx)
        {
            this.fx = fx;
        }

        private string ServerHash()
        {
            return fx.Server.Console.Eval(HashScript);
        }

        private void WaitForPlayerGone(string name)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (fx.Server.Console.ServerPlayer(name) == null)
                    return;
                System.Threading.Thread.Sleep(100);
            }
            throw new TimeoutException("player " + name + " still listed");
        }

        // PHYS-32 + PHYS-33: packet 3 reproduces the terrain, Explode is deferred two frames and broadcast as
        // packet 15 with the seed, the crater equals the headless one, and a later joiner receives the crater.
        [Fact]
        public void Explode_is_deferred_two_frames_broadcast_as_packet_15_and_transmitted_to_later_joiners()
        {
            string headlessHash;
            lock (HeadlessLevel.GameStateLock)
            {
                HeadlessLevel h = HeadlessLevel.Load(fx.Runtime, "bases");
                h.Explode(800, 300, 26, 777, false, null, null);
                headlessHash = h.Snapshot().Sha256Hex();
            }
            Assert.NotEqual(BasesSha, headlessHash);

            string b0;
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame("phys-a-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                // PHYS-33: packet 3 is the LZMA (SevenZipHelper format) of ToBytes: 500992 bytes, the oracle hash
                TerrainStatePacket t = a.WaitFor<TerrainStatePacket>(null, 10);
                Assert.Equal(500992, t.Bits.Length);
                Assert.True(t.CompressedLength < 500992 / 4, "compressed: " + t.CompressedLength);
                Assert.Equal(t.CompressedLength, t.Compressed.Length);
                byte[] again = SevenZip.Compression.LZMA.SevenZipHelper.Decompress(t.Compressed);
                Assert.Equal(t.Bits, again);
                Assert.Equal(BasesSha, a.Terrain.Sha256Hex());
                Assert.Equal(BasesSha, ServerHash());

                int mark = a.PacketCount;
                // PHYS-32: nothing is destroyed inside the call; the crater lands at frame + 2
                string r = fx.Server.Console.Eval(
                    "Sync(() => { int f = (int)Server.level.frame; Server.level.Explode(800, 300, 26, 777, false, (Player)null, (Weapon)null); " +
                    "byte[] b = (byte[])Server.level.GetTerrainState(); return f + \"|\" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant(); })");
                string[] parts = r.Split('|');
                int callFrame = int.Parse(parts[0], CultureInfo.InvariantCulture);
                Assert.Equal(BasesSha, parts[1]);

                ExplodePacket p = a.WaitFor<ExplodePacket>(x => x.Seed == 777, TimeSpan.FromSeconds(10), mark);
                Assert.Equal(callFrame + 2, p.Frame);
                Assert.Equal(new Vec2(800, 300), p.Position);
                Assert.Equal(26, p.Radius);
                Assert.False(p.Nonlethal);

                Assert.True(a.WaitUntil(() => fx.Server.Console.ServerFrame() >= callFrame + 3, TimeSpan.FromSeconds(10)), "server frames advance");
                string after = ServerHash();
                Assert.Equal(headlessHash, after);

                // the client's snapshot of packet 3 is not updated by packet 15 (it records, never applies)
                Assert.Equal(BasesSha, a.Terrain.Sha256Hex());
                b0 = after;
                string name = a.Name;
                a.Close();
                WaitForPlayerGone(name);
            }

            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                b.JoinGame("phys-b-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Assert.Equal(b0, b.Terrain.Sha256Hex());
                Assert.NotEqual(BasesSha, b.Terrain.Sha256Hex());
                Assert.Equal(headlessHash, b.Terrain.Sha256Hex());
                // the crater is visible in the received bitfield: fewer solid pixels than the shipped map
                Assert.True(b.Terrain.SolidCount() < 1694982, "solid count " + b.Terrain.SolidCount());
                Assert.True(b.Terrain.SolidCount() > 1694982 - 39 * 39 * 4);
            }
        }
    }

    public class BotsBasesFixture : ServerFixture
    {
        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
            // default maxbots 6: one human plus five bots on both teams give an enemy and a friendly attacker
        }
    }

    public class ServerExplosionDamageTests : IClassFixture<BotsBasesFixture>
    {
        private readonly BotsBasesFixture fx;

        public ServerExplosionDamageTests(BotsBasesFixture fx)
        {
            this.fx = fx;
        }

        private static string Players(string name, string attacker)
        {
            return "var ps = ((System.Collections.IEnumerable)Server.players).Cast<Player>().ToList(); " +
                   "Player v = ps.First(p => p.name == \"" + name + "\"); Player a = ps.First(p => p.name == \"" + attacker + "\"); ";
        }

        /// <summary>Waits until the player's entity position is unchanged over 150 ms (the humanoid rests).</summary>
        private Vec2 SettledPosition(string name)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            DebugConsole.PlayerInfo last = null;
            while (DateTime.UtcNow < deadline)
            {
                DebugConsole.PlayerInfo p = fx.Server.Console.ServerPlayer(name);
                if (p != null && p.X != null && last != null && p.X == last.X && p.Y == last.Y)
                    return new Vec2(p.X.Value, p.Y.Value);
                last = p;
                System.Threading.Thread.Sleep(150);
            }
            throw new TimeoutException("entity of " + name + " did not settle");
        }

        /// <summary>Explode at (victim + offset) with the attacker; returns the exact distance the server used.</summary>
        private float ExplodeAt(string victim, string attacker, int dx, int dy, out int callFrame)
        {
            string r = fx.Server.Console.Eval("Sync(() => { " + Players(victim, attacker) +
                "v.Entity.Health = 100f; int x = (int)v.Entity.Position.X + (" + dx + "); int y = (int)v.Entity.Position.Y + (" + dy + "); " +
                "Server.level.Explode(x, y, 26, 5, false, a, (Weapon)null); " +
                "return Server.level.frame + \"|\" + (v.Entity.Position - new Vec2(x, y)).Length().ToString(System.Globalization.CultureInfo.InvariantCulture); })");
            string[] parts = r.Split('|');
            callFrame = int.Parse(parts[0], CultureInfo.InvariantCulture);
            return float.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        private float HealthOf(string name)
        {
            return fx.Server.Console.EvalT<float>("Sync(() => ((System.Collections.IEnumerable)Server.players).Cast<Player>().First(p => p.name == \"" + name + "\").Entity.Health)");
        }

        private void WaitFrames(int frame)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && fx.Server.Console.ServerFrame() < frame)
                System.Threading.Thread.Sleep(30);
            Assert.True(fx.Server.Console.ServerFrame() >= frame, "server reached frame " + frame);
        }

        // PHYS-31: MaxHealth * min(1, d / damageRadius) * 0.5 inside the damage radius, only across teams.
        [Fact]
        public void Explosion_damage_is_half_the_distance_ratio_inside_the_damage_radius_and_only_across_teams()
        {
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.JoinGame("victim-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Assert.Equal(100f, c.Me.Health);
                List<DebugConsole.PlayerInfo> players = fx.Server.Console.ServerPlayers();
                DebugConsole.PlayerInfo me = players.Find(p => p.Name == c.Name);
                Assert.NotNull(me);
                DebugConsole.PlayerInfo enemy = players.Find(p => p.IsBot && p.Class != me.Class);
                DebugConsole.PlayerInfo friend = players.Find(p => p.IsBot && p.Class == me.Class);
                Assert.NotNull(enemy);
                Assert.NotNull(friend);

                // d = 30: Health = 100 - 100 * (30/39) * 0.5 = 61.54
                SettledPosition(c.Name);
                int mark = c.PacketCount;
                int f;
                float d30 = ExplodeAt(c.Name, enemy.Name, -30, 0, out f);
                Assert.InRange(d30, 29.5f, 31.5f);
                float expected = 100f - 100f * Math.Min(1f, d30 / 39f) * 0.5f;
                WaitFrames(f + 3);
                Assert.Equal(expected, HealthOf(c.Name), 1);
                HealthPacket hp = c.WaitFor<HealthPacket>(p => p.EntityId == c.MyEntityId && p.Health < 99f, TimeSpan.FromSeconds(5), mark);
                Assert.Equal(expected, hp.Health, 1);
                Assert.True(c.WaitUntil(() => Math.Abs(c.Me.Health - expected) < 0.1f, TimeSpan.FromSeconds(5)), "client state follows the packet");

                // d = 45: outside the damage radius (39): unchanged
                SettledPosition(c.Name);
                float d45 = ExplodeAt(c.Name, enemy.Name, 45, 0, out f);
                Assert.InRange(d45, 44f, 46.5f);           // the settled position may have a fractional part
                WaitFrames(f + 3);
                Assert.Equal(100f, HealthOf(c.Name));

                // same team: CanDamage false -> no damage at d = 30
                SettledPosition(c.Name);
                float dFriend = ExplodeAt(c.Name, friend.Name, -30, 0, out f);
                Assert.InRange(dFriend, 29.5f, 31.5f);
                WaitFrames(f + 3);
                Assert.Equal(100f, HealthOf(c.Name));

                // self: CanDamage(p, p) is true, the player is hurt by their own explosion
                SettledPosition(c.Name);
                float dSelf = ExplodeAt(c.Name, c.Name, -30, 0, out f);
                WaitFrames(f + 3);
                Assert.Equal(100f - 100f * Math.Min(1f, dSelf / 39f) * 0.5f, HealthOf(c.Name), 1);

                // point blank (last, in case the settled position is integral: a zero-length knock-back vector
                // leaves the victim with a NaN velocity, see docs/PORTING.md): ratio ~1 -> amount ~0 -> health
                // practically unchanged, but the health packet is still sent
                SettledPosition(c.Name);
                mark = c.PacketCount;
                float d0 = ExplodeAt(c.Name, enemy.Name, 0, 0, out f);
                Assert.True(d0 < 1f, "distance " + d0);
                float expected0 = 100f - 100f * Math.Min(1f, d0 / 39f) * 0.5f;
                WaitFrames(f + 3);
                Assert.Equal(expected0, HealthOf(c.Name), 1);
                Assert.True(HealthOf(c.Name) > 99.9f, "point blank does (almost) no damage: " + HealthOf(c.Name));
                HealthPacket hp0 = c.WaitFor<HealthPacket>(p => p.EntityId == c.MyEntityId, TimeSpan.FromSeconds(5), mark);
                Assert.Equal(expected0, hp0.Health, 1);
            }
        }
    }
}
