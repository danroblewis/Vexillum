using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.physicsterrain
{
    /// <summary>
    /// LevelLoader as the server and the client use it: the file checks
    /// (missing file, magic number, corrupt payload), LevelExists/LevelMd5
    /// against the file on disk, and the data.txt grammar on a synthetic map
    /// written with <see cref="MapWriter"/>. LevelLoader reads Maps/ relative
    /// to the current directory and Util.Debug appends to a process-wide
    /// buffer, so the class runs in the serial GameState collection.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class LevelLoaderTests
    {
        private const int BogusMagic = 0x12345678;

        /// <summary>Current directory = the scratch runtime, Util.IsServer = true, restored on dispose.</summary>
        private sealed class RuntimeScope : IDisposable
        {
            private readonly string previous;

            public RuntimeScope(ScratchRuntime rt)
            {
                previous = Directory.GetCurrentDirectory();
                Directory.SetCurrentDirectory(rt.Root);
                Util.IsServer = true;
            }

            public void Dispose()
            {
                Directory.SetCurrentDirectory(previous);
            }
        }

        private static StringBuilder DebugBuffer()
        {
            FieldInfo f = typeof(Util).GetField("debug", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(f);
            return (StringBuilder)f.GetValue(null);
        }

        /// <summary>Empties the Util.Debug buffer so the next read holds only what the test produced.</summary>
        private static void ClearDebugLog()
        {
            DebugBuffer().Clear();
        }

        /// <summary>
        /// What Util.Debug produced since <see cref="ClearDebugLog"/>: the buffer
        /// plus Server/debug_server.log in the scratch runtime (Util flushes the
        /// buffer there, relative to the current directory, once it exceeds 1 KB).
        /// </summary>
        private static string DebugLog(ScratchRuntime rt)
        {
            return rt.ReadServerLog() + DebugBuffer().ToString();
        }

        private static byte[] JunkBytes(int count, int seed)
        {
            byte[] junk = new byte[count];
            new Random(seed).NextBytes(junk);
            return junk;
        }

        // PHYS-37
        [Fact]
        public void Missing_map_LoadData_returns_null_LevelExists_is_false_and_LevelMd5_throws()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            using (new RuntimeScope(rt))
            {
                Assert.False(File.Exists(rt.MapPath("nothere")));
                Assert.Null(LevelLoader.LoadData("nothere"));
                Assert.False(LevelLoader.LevelExists("nothere"));
                Assert.Throws<FileNotFoundException>(() => LevelLoader.LevelMd5("nothere"));
                // and the shipped map next to it is seen
                Assert.True(LevelLoader.LevelExists("bases"));
            }
        }

        // PHYS-37
        [Fact]
        public void File_without_the_magic_number_is_rejected_with_the_bad_magic_error_although_LevelExists_is_true()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            using (new RuntimeScope(rt))
            {
                byte[] junk = JunkBytes(64, 7);
                BitConverter.GetBytes(BogusMagic).CopyTo(junk, 0);
                File.WriteAllBytes(rt.MapPath("bogus"), junk);

                ClearDebugLog();
                Assert.Null(LevelLoader.LoadData("bogus"));
                string log = DebugLog(rt);
                Assert.Contains("Error: Not a valid map file.", log);
                Assert.DoesNotContain("Error loading level:", log);
                // LevelExists is a pure File.Exists: the file is not validated
                Assert.True(LevelLoader.LevelExists("bogus"));
                Assert.Equal(MD5.HashData(junk), LevelLoader.LevelMd5("bogus"));
            }
        }

        // PHYS-37: the magic passes, the payload does not (an entry claims more bytes than the file holds)
        [Fact]
        public void Magic_followed_by_a_corrupt_payload_is_rejected_with_error_loading_level()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            using (new RuntimeScope(rt))
            {
                byte[] records;
                using (MemoryStream ms = new MemoryStream())
                {
                    using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8, true))
                    {
                        w.Write("Corrupt");
                        w.Write("main.png");
                        w.Write((long)999999);
                        w.Write(JunkBytes(16, 3));
                    }
                    records = ms.ToArray();
                }
                byte[] compressed = SevenZip.Compression.LZMA.SevenZipHelper.Compress(records);
                byte[] file = new byte[4 + compressed.Length];
                BitConverter.GetBytes(Protocol.MapMagic).CopyTo(file, 0);
                Array.Copy(compressed, 0, file, 4, compressed.Length);
                File.WriteAllBytes(rt.MapPath("corrupt"), file);

                ClearDebugLog();
                Assert.Null(LevelLoader.LoadData("corrupt"));
                string log = DebugLog(rt);
                Assert.Contains("Error loading level:", log);
                Assert.DoesNotContain("Not a valid map file", log);
            }
        }

        // PHYS-37
        [Theory]
        [InlineData("bases")]
        [InlineData("complex")]
        public void LevelMd5_is_the_md5_of_the_whole_file_including_the_magic(string map)
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            using (new RuntimeScope(rt))
            {
                byte[] file = File.ReadAllBytes(rt.MapPath(map));
                Assert.Equal(Protocol.MapMagic, BitConverter.ToInt32(file, 0));
                byte[] md5 = LevelLoader.LevelMd5(map);
                Assert.Equal(16, md5.Length);
                Assert.Equal(MD5.HashData(file), md5);
                Assert.Equal(MapFile.Md5(rt.MapPath(map)), md5);
                Assert.True(LevelLoader.LevelExists(map));

                // the hash covers every byte: flipping one in the payload changes it, and
                // the payload alone (what the client receives in 220-222) is not what is hashed
                byte[] flipped = (byte[])file.Clone();
                flipped[100] ^= 0xFF;
                File.WriteAllBytes(rt.MapPath(map + "-flipped"), flipped);
                Assert.NotEqual(md5, LevelLoader.LevelMd5(map + "-flipped"));
                byte[] payloadOnly = new byte[file.Length - 4];
                Array.Copy(file, 4, payloadOnly, 0, payloadOnly.Length);
                Assert.NotEqual(MD5.HashData(payloadOnly), md5);
            }
        }

        // PHYS-37: the two shipped maps do not collide
        [Fact]
        public void LevelMd5_differs_between_the_shipped_maps()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            using (new RuntimeScope(rt))
            {
                Assert.NotEqual(LevelLoader.LevelMd5("bases"), LevelLoader.LevelMd5("complex"));
            }
        }

        private const string DataWithUnknownCommand =
            "region spawn_Green 10 20 30 40\n" +
            "frobnicate 1 2\n" +
            "\n" +
            "region flag_Blue 5 6 7 8\n" +
            "region ladder_1 1 2 3 4\n";

        // PHYS-38
        [Fact]
        public void Data_regions_load_in_file_order_and_unknown_commands_and_blank_lines_are_ignored()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                MapWriter.Write(rt.MapPath("synthetic"), "Synthetic Test Map",
                                MapWriter.MinimalEntries(8, 8, SyntheticLevel.Empty, DataWithUnknownCommand));

                ClearDebugLog();
                LevelLoader.LevelData d = HeadlessLevel.LoadData(rt.Root, "synthetic");
                Assert.NotNull(d);
                Assert.Equal("synthetic", d.shortName);
                Assert.Equal("Synthetic Test Map", d.longName);
                Assert.Equal(new SortedSet<string> { "main", "background", "collision" }, new SortedSet<string>(d.bitmaps.Keys));
                Assert.Equal(new[] { "spawn_Green", "flag_Blue", "ladder_1" }, d.regions.ConvertAll(r => r.name));
                Assert.Equal(10, d.regions[0].x1);
                Assert.Equal(20, d.regions[0].y1);
                Assert.Equal(5, d.regions[1].x1);
                Assert.Equal(6, d.regions[1].y1);
                Assert.Equal(1, d.regions[2].x1);
                Assert.Equal(2, d.regions[2].y1);
                Assert.DoesNotContain("Error", DebugLog(rt));

                // x2/y2 are private: RandomPosition with a zero size spans x1..x2-1, y1..y2-1
                Random rnd = new Random(1);
                int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
                for (int i = 0; i < 500; i++)
                {
                    Vec2 p = d.regions[0].RandomPosition(rnd, Vec2.Zero, 100);
                    minX = Math.Min(minX, (int)p.X); maxX = Math.Max(maxX, (int)p.X);
                    minY = Math.Min(minY, (int)p.Y); maxY = Math.Max(maxY, (int)p.Y);
                }
                Assert.Equal(10, minX);
                Assert.Equal(29, maxX);                 // x2 = 30
                Assert.Equal(100 - 39 + 16, minY);      // y2 = 40
                Assert.Equal(100 - 20 + 16, maxY);

                // and the Level built from it files the spawn and flag regions (size from main, not data.txt)
                HeadlessLevel level = HeadlessLevel.Load(rt, "synthetic");
                Assert.Equal(8, level.Width);
                Assert.Equal(8, level.Height);
                Assert.Single(level.Spawns[PlayerClass.Green]);
                Assert.Same(level.Regions[0], level.Spawns[PlayerClass.Green][0]);
                Assert.False(level.Spawns.ContainsKey(PlayerClass.Blue));
                Assert.Same(level.Regions[1], level.Flags[PlayerClass.Blue]);
                Assert.False(level.Flags.ContainsKey(PlayerClass.Green));
            }
        }

        private const string DataWithMalformedLine =
            "region spawn_Green 10 20 30 40\n" +
            "region spawn_Blue 1 x 2 3\n" +
            "region flag_Blue 5 6 7 8\n";

        // PHYS-38: what the 2013 code does with a malformed line when no game window exists.
        // The per-line catch calls Vexillum.Error, which dereferences Vexillum.game (null on the
        // server and headless), so the NullReferenceException escapes the line loop into the outer
        // catch: the loader logs "Error loading level:" plus a stack trace and returns null.
        [Fact]
        public void Malformed_data_line_aborts_the_headless_load_because_Vexillum_Error_needs_the_game()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                MapWriter.Write(rt.MapPath("synthetic"), "Synthetic Test Map",
                                MapWriter.MinimalEntries(8, 8, SyntheticLevel.Empty, DataWithMalformedLine));

                ClearDebugLog();
                Assert.Null(HeadlessLevel.LoadData(rt.Root, "synthetic"));
                string log = DebugLog(rt);
                Assert.Contains("Error loading level:", log);
                Assert.Contains("Vexillum.Error(", log);
                Assert.DoesNotContain("Parse error on line", log);
            }
        }

        // PHYS-38: the intended behaviour of the per-line try/catch.
        [Fact(Skip = "Known original bug: Vexillum.Error dereferences the null Vexillum.game on the server, so a malformed data.txt line aborts LoadData with a NullReferenceException instead of reporting the line and continuing, docs/PORTING.md")]
        public void Malformed_data_line_is_reported_with_its_line_number_and_the_other_regions_still_load()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                MapWriter.Write(rt.MapPath("synthetic"), "Synthetic Test Map",
                                MapWriter.MinimalEntries(8, 8, SyntheticLevel.Empty, DataWithMalformedLine));

                ClearDebugLog();
                LevelLoader.LevelData d = HeadlessLevel.LoadData(rt.Root, "synthetic");
                Assert.NotNull(d);
                Assert.Equal(new[] { "spawn_Green", "flag_Blue" }, d.regions.ConvertAll(r => r.name));
                Assert.Contains("Parse error on line 2 of data.txt for map synthetic", DebugLog(rt) + System.Windows.Forms.MessageBox.LastText);
            }
        }
    }
}
