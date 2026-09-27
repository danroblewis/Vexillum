using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Packets 220-222: the map download a client without the level (or with
    /// a different version of it) receives before it can join, on both
    /// shipped maps (one server per map).
    /// </summary>
    public class MapDownloadTests : IClassFixture<SoloBasesFixture>, IClassFixture<SoloComplexFixture>
    {
        private readonly SoloBasesFixture bases;
        private readonly SoloComplexFixture complex;

        public MapDownloadTests(SoloBasesFixture bases, SoloComplexFixture complex)
        {
            this.bases = bases;
            this.complex = complex;
        }

        private ServerFixture For(string map)
        {
            return map == "bases" ? (ServerFixture)bases : complex;
        }

        private static string ShippedMap(string map)
        {
            return Path.Combine(Repo.RuntimeDir, "Maps", map + ".map");
        }

        // PROTO-04
        [Theory]
        [InlineData("bases", 3914, 1024, "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3")]
        [InlineData("complex", 2736, 818, "d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6")]
        public void Map_download_reproduces_the_shipped_map_file_byte_for_byte(string map, int w, int h, string terrainSha)
        {
            ServerFixture fx = For(map);
            string name = Names.Unique("dl");
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.Login(1UL, name);
                ServerIdPacket sid = c.WaitFor<ServerIdPacket>(null, 10);
                Assert.Equal(map, sid.MapName);
                int mark = c.PacketCount;
                c.Status(false, new byte[16]);
                LevelEndPacket end = c.WaitFor<LevelEndPacket>(null, TimeSpan.FromSeconds(60), mark);

                // 220 announces the length, every 221 chunk is at most 1020 bytes and only the last is shorter
                long fileLength = new FileInfo(ShippedMap(map)).Length;
                LevelBeginPacket begin = c.WaitFor<LevelBeginPacket>(null, TimeSpan.FromSeconds(1), mark);
                Assert.Equal(fileLength - 4, begin.TotalLength);
                List<LevelChunkPacket> chunks = c.Packets<LevelChunkPacket>(mark);
                Assert.True(chunks.Count >= 2, "chunks: " + chunks.Count);
                Assert.Equal((begin.TotalLength + 1019) / 1020, chunks.Count);
                long sum = 0;
                for (int i = 0; i < chunks.Count; i++)
                {
                    Assert.Equal(chunks[i].Length, chunks[i].Bytes.Length);
                    if (i < chunks.Count - 1)
                        Assert.Equal(1020, chunks[i].Length);
                    else
                        Assert.InRange(chunks[i].Length, 1, 1020);
                    sum += chunks[i].Length;
                }
                Assert.Equal(begin.TotalLength, sum);
                // 220, all 221s, 222 arrive in that order with nothing else in between
                List<ServerPacket> between = c.AllPackets().GetRange(begin.Sequence, end.Sequence - begin.Sequence + 1);
                Assert.IsType<LevelBeginPacket>(between[0]);
                Assert.IsType<LevelEndPacket>(between[between.Count - 1]);
                for (int i = 1; i < between.Count - 1; i++)
                    Assert.IsType<LevelChunkPacket>(between[i]);

                // magic + payload is the shipped file
                byte[] file = new byte[end.MapBytes.Length + 4];
                Array.Copy(BitConverter.GetBytes(0x004F876B), 0, file, 0, 4);
                Array.Copy(end.MapBytes, 0, file, 4, end.MapBytes.Length);
                Assert.Equal(File.ReadAllBytes(ShippedMap(map)), file);
                MapFile parsed = MapFile.Parse(file, map);
                Assert.Equal(w, parsed.Width);
                Assert.Equal(h, parsed.Height);

                // the join continues once the client reports the right md5
                byte[] md5 = MapFile.Md5(ShippedMap(map));
                int mark2 = c.PacketCount;
                c.Status(true, md5);
                TerrainStatePacket terrain = c.WaitFor<TerrainStatePacket>(null, TimeSpan.FromSeconds(30), mark2);
                Assert.Equal(terrainSha, new TerrainSnapshot(terrain.Bits, w, h).Sha256Hex());
                c.WaitFor<LevelFinishPacket>(null, TimeSpan.FromSeconds(30), mark2);
                Assert.Empty(c.Packets<DisconnectPacket>());
            }
        }

        // PROTO-04 through the harness: a client with an empty Maps/ directory installs the map it received
        [Fact]
        public void Client_without_the_map_downloads_installs_and_joins()
        {
            string mapsDir = Path.Combine(Path.GetTempPath(), "vexillum-acceptance", "maps-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(mapsDir);
            try
            {
                using (ScriptedClient c = new ScriptedClient("127.0.0.1", bases.Server.Port, mapsDir))
                {
                    c.JoinGame(Names.Unique("fresh"));
                    Assert.Equal("bases", c.MapName);
                    Assert.NotNull(c.ReceivedMapBytes);
                    Assert.Equal(MapFile.Read(ShippedMap("bases")).CompressedPayload, c.ReceivedMapBytes);
                    Assert.True(File.Exists(Path.Combine(mapsDir, "bases.map")));
                    Assert.Equal(MapFile.Md5(ShippedMap("bases")), MapFile.Md5(Path.Combine(mapsDir, "bases.map")));
                    Assert.Equal("059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3", c.Terrain.Sha256Hex());
                    Assert.True(c.MyEntityId > 0);
                }
            }
            finally
            {
                try { Directory.Delete(mapsDir, true); } catch (Exception) { }
            }
        }

        // PROTO-05
        [Fact]
        public void Ready_with_a_wrong_md5_gets_the_map_instead_of_a_disconnect()
        {
            string name = Names.Unique("wrongmd5");
            using (ScriptedClient c = new ScriptedClient(bases.Server))
            {
                c.Login(2UL, name);
                c.WaitFor<ServerIdPacket>(null, 10);
                int mark = c.PacketCount;
                byte[] wrong = new byte[16];
                for (int i = 0; i < 16; i++)
                    wrong[i] = 0xFF;
                c.Status(true, wrong);
                LevelBeginPacket begin = c.WaitFor<LevelBeginPacket>(null, TimeSpan.FromSeconds(10), mark);
                Assert.Equal(mark, begin.Sequence);   // the very next packet
                c.WaitFor<LevelEndPacket>(null, TimeSpan.FromSeconds(60), mark);
                Assert.Empty(c.Packets<DisconnectPacket>());
                Assert.Empty(c.Packets<TerrainStatePacket>());
                Assert.Equal(0, bases.Server.Count("different version of the map"));
                Assert.False(c.IsClosed);
            }
        }
    }
}
