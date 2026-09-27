using System;
using System.Collections.Generic;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    public class ExplosionsFixture : GameplayFixture
    {
    }

    /// <summary>
    /// Explosion determinism and terrain sync (SRV-18, SRV-19a, SRV-21). The
    /// headless replays use HeadlessLevel (process-wide game state), so the
    /// class lives in the serial GameState collection; the live server is a
    /// class fixture of its own.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class ExplosionTests : IClassFixture<ExplosionsFixture>
    {
        public const string PristineBases = "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3";
        private readonly ExplosionsFixture fx;

        public ExplosionTests(ExplosionsFixture fx)
        {
            this.fx = fx;
        }

        /// <summary>Fires one rocket straight down from destructible ground and returns the server's explosion packet.</summary>
        private ExplodePacket RocketIntoGround(ScriptedClient a, int x)
        {
            Walk.Park(fx, a, x, 470);
            int m = a.PacketCount;
            a.SendWeaponActivate(Protocol.Button.Left, true, (float)Math.PI / 2);
            a.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == a.MyEntityId, TimeSpan.FromSeconds(5), m);
            a.SendWeaponActivate(Protocol.Button.Left, false, (float)Math.PI / 2);
            ExplodePacket boom = a.WaitFor<ExplodePacket>(p => p.Radius == 26 && !p.Nonlethal, TimeSpan.FromSeconds(10), m);
            // the explosion is a frame task at boom.Frame; wait until the server has stepped past it
            GameplayFixture.WaitUntil(() => fx.Console.ServerFrame() > boom.Frame + 3, TimeSpan.FromSeconds(5), "explosion frame processed");
            return boom;
        }

        // SRV-18
        [Fact]
        public void Explosions_are_deterministic_given_the_seed_and_match_the_live_server()
        {
            ExplodePacket boom;
            TerrainSnapshot before;
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            {
                // packet 3 is the server's terrain at the join: the state the explosion is applied to
                before = a.Terrain;
                Assert.Equal(before.Sha256Hex(), fx.TerrainSha256());
                boom = RocketIntoGround(a, 1220);
                fx.Leave(a);
            }
            string live = fx.TerrainSha256();
            Assert.NotEqual(before.Sha256Hex(), live);
            Assert.Equal(26, boom.Radius);

            // replay the same explosion on independent headless levels seeded with the pre-shot terrain
            // (explosions only touch bit 0, so the bitfield plus the map's collision codes is the whole state)
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                HeadlessLevel l1 = HeadlessLevel.Load(rt, "bases");
                HeadlessLevel l2 = HeadlessLevel.Load(rt, "bases");
                HeadlessLevel l3 = HeadlessLevel.Load(rt, "bases");
                Assert.Equal(PristineBases, l1.Snapshot().Sha256Hex());
                foreach (HeadlessLevel l in new HeadlessLevel[] { l1, l2, l3 })
                    l.SetTerrainState(before.Bits);
                Assert.Equal(before.Sha256Hex(), l1.Snapshot().Sha256Hex());
                l1.Explode((int)boom.Position.X, (int)boom.Position.Y, boom.Radius, boom.Seed, boom.Nonlethal, null, null);
                l2.Explode((int)boom.Position.X, (int)boom.Position.Y, boom.Radius, boom.Seed, boom.Nonlethal, null, null);
                l3.Explode((int)boom.Position.X, (int)boom.Position.Y, boom.Radius, boom.Seed + 1, boom.Nonlethal, null, null);
                string h1 = l1.Snapshot().Sha256Hex(), h2 = l2.Snapshot().Sha256Hex(), h3 = l3.Snapshot().Sha256Hex();
                Assert.Equal(h1, h2);
                Assert.NotEqual(before.Sha256Hex(), h1);
                Assert.NotEqual(h1, h3);
                Assert.Equal(live, h1);
                Assert.True(l1.Snapshot().CountDifferences(l3.Snapshot()) > 0, "another seed rotates the traces differently");
                Assert.True(l1.Snapshot().CountDifferences(before) > 50, "a radius-26 explosion clears a visible crater");
            }
        }

        // SRV-21
        [Fact]
        public void Late_joiners_receive_the_destroyed_terrain_not_the_pristine_map()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            {
                RocketIntoGround(a, 1260);
                string live = fx.TerrainSha256();
                Assert.NotEqual(PristineBases, live);
                using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
                {
                    // JoinGame sent the md5 of the .map file and the server accepted it: the check is over the file,
                    // not the live terrain, which arrives destroyed in packet 3
                    Assert.NotNull(b.Terrain);
                    Assert.Equal(live, b.Terrain.Sha256Hex());
                    Assert.NotEqual(PristineBases, b.Terrain.Sha256Hex());
                    Assert.NotEqual(a.Terrain.Sha256Hex(), b.Terrain.Sha256Hex());
                    fx.Leave(b);
                }
                fx.Leave(a);
            }
        }

        // SRV-19 (a): Solid stops the traces; only destructible pixels near the centre are cleared
        [Fact]
        public void Headless_explosion_never_clears_solid_pixels_and_stays_within_reach()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                HeadlessLevel level = HeadlessLevel.Load(rt, "bases");
                // inside destructible ground (code 15) with a Solid run within 24 px (probed with the Python oracle);
                // in Empty pixels a trace shrinks to nothing within ~18 px, so the centre must sit in terrain
                const int cx = 420, cy = 422, radius = 26;
                Assert.True(level.CollisionNibble(cx, cy) >= 12, "centre in destructible terrain");
                int reach = 3 * radius / 2 + 8;   // damageRadius plus the widest trace circle
                Dictionary<(int, int), byte> codes = new Dictionary<(int, int), byte>();
                Dictionary<(int, int), bool> solidBefore = new Dictionary<(int, int), bool>();
                int solidRun = 0, destructible = 0;
                for (int x = cx - 70; x <= cx + 70; x++)
                    for (int y = cy - 70; y <= cy + 70; y++)
                    {
                        byte c = level.CollisionNibble(x, y);
                        codes[(x, y)] = c;
                        solidBefore[(x, y)] = level.IsSolid(x, y);
                        if (c == 1) solidRun++;
                        else if (c >= 2) destructible++;
                    }
                Assert.True(solidRun > 100 && destructible > 100, "spot has both Solid and destructible pixels: " + solidRun + "/" + destructible);

                level.Explode(cx, cy, radius, 4242, false, null, null);

                int cleared = 0;
                foreach (KeyValuePair<(int, int), bool> kv in solidBefore)
                {
                    int x = kv.Key.Item1, y = kv.Key.Item2;
                    bool now = level.IsSolid(x, y);
                    if (kv.Value && !now)
                    {
                        cleared++;
                        Assert.NotEqual(1, codes[(x, y)]);   // Solid never clears (fraction 0, Destroy refuses)
                        double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                        Assert.True(d <= reach, "pixel " + x + "," + y + " at distance " + d + " is out of reach " + reach);
                    }
                    Assert.False(!kv.Value && now, "an empty pixel became solid");
                }
                Assert.True(cleared > 0, "some destructible pixels were cleared");
            }
        }

        // SRV-18: a radius below 4 is a plain circle and independent of the seed
        [Fact]
        public void Small_explosions_are_seed_independent_circles()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                HeadlessLevel l1 = HeadlessLevel.Load(rt, "bases");
                HeadlessLevel l2 = HeadlessLevel.Load(rt, "bases");
                const int x = 1240, y = 444;
                Assert.True(l1.CollisionNibble(x, y) >= 12, "destructible pixel");
                Assert.True(l1.IsSolid(x, y));
                l1.Explode(x, y, 2, 1, true, null, null);
                l2.Explode(x, y, 2, 987654, true, null, null);
                Assert.Equal(l1.Snapshot().Sha256Hex(), l2.Snapshot().Sha256Hex());
                // DrawCircle clears dx*dx + dy*dy < 4: the centre and its four neighbours (those that were solid)
                int cleared = 0;
                for (int dx = -3; dx <= 3; dx++)
                    for (int dy = -3; dy <= 3; dy++)
                        if (!l1.IsSolid(x + dx, y + dy) && l2.CollisionNibble(x + dx, y + dy) != 0)
                        {
                            cleared++;
                            Assert.True(dx * dx + dy * dy < 4, "cleared pixel outside the radius-2 circle: " + dx + "," + dy);
                        }
                Assert.True(cleared >= 3, "cleared " + cleared + " pixels of the small circle");
            }
        }
    }
}
