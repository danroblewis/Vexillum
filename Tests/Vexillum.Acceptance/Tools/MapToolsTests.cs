using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SevenZip.Compression.LZMA;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// The map packing tools (MapTool/MapCreator.cs, MapExtractor.cs,
    /// MapUtil.cs) driven through their static methods (the Program.cs
    /// wrappers end in Console.ReadKey). MapUtil reads mapfile.list from the
    /// current directory and LevelLoader reads Maps/ relative to it, so the
    /// class runs in the serial GameState collection.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class MapToolsTests
    {
        /// <summary>The eight entries of the shipped maps, in file order (docs/ARCHITECTURE.md "Map file format").</summary>
        private static readonly string[] shippedEntries =
        {
            "main.jpg", "background.jpg", "collision.png", "data.txt", "sky.jpg", "left.png", "right.png", "bottom.jpg"
        };

        private const string BasesTerrainSha256 = "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3";

        private static void WriteMapfileList(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "mapfile.list"), string.Join("\n", shippedEntries) + "\n", Encoding.ASCII);
        }

        // TOOLS-01
        [Fact]
        public void Extract_then_create_reproduces_the_payload_and_the_terrain_of_bases()
        {
            using (TempDir work = new TempDir())
            {
                WriteMapfileList(work.Path);
                string mapCopy = work.File("bases.map");
                File.Copy(Path.Combine(Repo.RuntimeDir, "Maps", "bases.map"), mapCopy);
                MapFile original = MapFile.Read(mapCopy);
                Assert.Equal(shippedEntries, original.Entries.ConvertAll(e => e.Key));

                // ---- extract ----
                string extractOut;
                using (new CwdScope(work.Path))
                    extractOut = MapToolsDriver.ExtractMap(mapCopy);
                string folder = work.File("bases");
                Assert.Contains("Map files saved to" + folder, extractOut);
                string[] written = Directory.GetFiles(folder);
                Assert.Equal(8, written.Length);
                foreach (KeyValuePair<string, byte[]> e in original.Entries)
                {
                    string f = Path.Combine(folder, "bases_" + e.Key);
                    Assert.True(File.Exists(f), "missing " + f);
                    Assert.Equal(e.Value, File.ReadAllBytes(f));
                }

                // ---- re-create ----
                string createOut;
                using (new CwdScope(work.Path))
                    createOut = MapToolsDriver.CreateMap(folder, original.LongName);
                string repacked = Path.Combine(folder, "bases.map");
                Assert.Contains("Map saved to " + repacked, createOut);
                byte[] bytes = File.ReadAllBytes(repacked);
                Assert.Equal(Protocol.MapMagic, BitConverter.ToInt32(bytes, 0));

                byte[] compressed = new byte[bytes.Length - 4];
                Array.Copy(bytes, 4, compressed, 0, compressed.Length);
                byte[] payload = SevenZipHelper.Decompress(compressed);
                long endOfRecords;
                using (BinaryReader r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
                {
                    Assert.Equal(original.LongName, r.ReadString());
                    foreach (KeyValuePair<string, byte[]> e in original.Entries)
                    {
                        Assert.Equal(e.Key, r.ReadString());
                        Assert.Equal((long)e.Value.Length, r.ReadInt64());
                        Assert.Equal(e.Value, r.ReadBytes(e.Value.Length));
                    }
                    endOfRecords = r.BaseStream.Position;
                }
                // MapCreator compresses temp.GetBuffer(): the MemoryStream's whole
                // capacity, so the payload carries zero padding after the records
                // (the empty-name terminator LevelLoader stops at).
                Assert.True(payload.Length > endOfRecords, "expected GetBuffer() padding after the records");
                for (long i = endOfRecords; i < payload.Length; i++)
                    Assert.Equal(0, payload[i]);

                MapFile re = MapFile.Read(repacked);
                Assert.Equal(original.LongName, re.LongName);
                Assert.Equal(8, re.Entries.Count);

                // ---- the game loads it ----
                using (ScratchRuntime rt = new ScratchRuntime())
                {
                    File.Copy(repacked, rt.MapPath("bases"), true);
                    LevelLoader.LevelData d = HeadlessLevel.LoadData(rt.Root, "bases");
                    Assert.NotNull(d);
                    Assert.Equal(original.LongName, d.longName);
                    Assert.Equal(3914, d.bitmaps["collision"].Width);
                    Assert.Equal(1024, d.bitmaps["collision"].Height);

                    HeadlessLevel level = HeadlessLevel.Load(rt, "bases");
                    Assert.Equal(BasesTerrainSha256, level.Snapshot().Sha256Hex());
                }
            }
        }

        // TOOLS-02 (a)
        [Fact]
        public void ExtractMap_rejects_a_file_without_the_magic_number()
        {
            using (TempDir work = new TempDir())
            {
                string bogus = work.File("notamap.map");
                byte[] junk = new byte[64];
                new Random(7).NextBytes(junk);
                BitConverter.GetBytes(0x12345678).CopyTo(junk, 0);
                File.WriteAllBytes(bogus, junk);

                string output;
                using (new CwdScope(work.Path))
                    output = MapToolsDriver.ExtractMap(bogus);

                Assert.Contains("Error: Not a valid map file.", output);
                string folder = work.File("notamap");
                Assert.True(Directory.Exists(folder), "the <name>/ folder is created before the magic check");
                Assert.Empty(Directory.GetFileSystemEntries(folder));
            }
        }

        // TOOLS-02 (b)
        [Fact]
        public void ExtractMap_reports_a_missing_file()
        {
            using (TempDir work = new TempDir())
            {
                string output;
                using (new CwdScope(work.Path))
                    output = MapToolsDriver.ExtractMap(work.File("absent.map"));
                Assert.Contains("Error: File does not exist.", output);
                Assert.Empty(Directory.GetFileSystemEntries(work.Path));
            }
        }

        // TOOLS-02 (c)
        [Fact]
        public void CreateMap_stops_at_the_first_missing_entry_without_writing_a_map()
        {
            using (TempDir work = new TempDir())
            {
                WriteMapfileList(work.Path);
                string folder = work.File("bases");
                Directory.CreateDirectory(folder);
                MapFile original = MapFile.Read(Path.Combine(Repo.RuntimeDir, "Maps", "bases.map"));
                foreach (KeyValuePair<string, byte[]> e in original.Entries)
                    if (e.Key != "data.txt")
                        File.WriteAllBytes(Path.Combine(folder, "bases_" + e.Key), e.Value);

                string output;
                using (new CwdScope(work.Path))
                    output = MapToolsDriver.CreateMap(folder, "Bases");

                Assert.Contains("Error: Couldn't find required file " + Path.Combine(folder, "bases_data.txt"), output);
                Assert.DoesNotContain("Compressing", output);
                Assert.False(File.Exists(Path.Combine(folder, "bases.map")), "the return happens before the output file is opened");
            }
        }

        // TOOLS-02 (d)
        [Fact]
        public void MapUtil_throws_when_mapfile_list_is_missing_from_the_cwd()
        {
            using (TempDir work = new TempDir())
            {
                MapToolsDriver.ResetFileNames();
                Exception ex;
                using (new CwdScope(work.Path))
                    ex = Assert.ThrowsAny<Exception>(() => MapToolsDriver.GetFileNames());
                Assert.Equal("Could not load map data", ex.Message);

                // With the list present the names come back in file order.
                WriteMapfileList(work.Path);
                string[] names;
                using (new CwdScope(work.Path))
                    names = MapToolsDriver.GetFileNames();
                Assert.Equal(shippedEntries, names);
            }
        }
    }
}
