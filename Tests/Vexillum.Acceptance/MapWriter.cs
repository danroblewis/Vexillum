using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SevenZip.Compression.LZMA;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// Writes a .map container the way MapTool/MapCreator.cs does (magic
    /// 0x004F876B, then the LZMA of: long name, (name, int64 length, bytes)
    /// entries, empty-name terminator) so a test can hand LevelLoader a
    /// synthetic map with its own data.txt or a deliberately broken file.
    /// Images are encoded with the System.Drawing shim's PNG encoder, so the
    /// loader decodes them through the same path as the shipped maps.
    /// Counterpart of <see cref="MapFile"/>, which only reads.
    /// </summary>
    public static class MapWriter
    {
        /// <summary>PNG bytes of a shim bitmap.</summary>
        public static byte[] Png(System.Drawing.Bitmap bitmap)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>A w x h bitmap of one ARGB colour, encoded as PNG.</summary>
        public static byte[] FilledPng(int w, int h, uint argb)
        {
            System.Drawing.Bitmap b = new System.Drawing.Bitmap(w, h);
            System.Drawing.Color c = System.Drawing.Color.FromArgb(unchecked((int)argb));
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    b.SetPixel(x, y, c);
            return Png(b);
        }

        /// <summary>
        /// The LZMA payload's plain form: long name, then the entries, then the
        /// empty-name terminator LevelLoader stops at.
        /// </summary>
        public static byte[] Records(string longName, IList<KeyValuePair<string, byte[]>> entries)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    w.Write(longName);
                    foreach (KeyValuePair<string, byte[]> e in entries)
                    {
                        w.Write(e.Key);
                        w.Write((long)e.Value.Length);
                        w.Write(e.Value);
                    }
                    w.Write("");
                }
                return ms.ToArray();
            }
        }

        /// <summary>The whole file: magic, then the LZMA of <see cref="Records"/>.</summary>
        public static byte[] Pack(string longName, IList<KeyValuePair<string, byte[]>> entries)
        {
            byte[] compressed = SevenZipHelper.Compress(Records(longName, entries));
            byte[] file = new byte[4 + compressed.Length];
            BitConverter.GetBytes(Protocol.MapMagic).CopyTo(file, 0);
            Array.Copy(compressed, 0, file, 4, compressed.Length);
            return file;
        }

        public static void Write(string path, string longName, IList<KeyValuePair<string, byte[]>> entries)
        {
            File.WriteAllBytes(path, Pack(longName, entries));
        }

        /// <summary>
        /// The three images the base Level constructor needs (main, background,
        /// collision; w x h, main filled with <see cref="SyntheticLevel.MainFill"/>,
        /// collision with <paramref name="collisionArgb"/>) followed by data.txt
        /// with the given text. The result is a map LevelLoader.LoadData and
        /// HeadlessLevel.Load accept.
        /// </summary>
        public static List<KeyValuePair<string, byte[]>> MinimalEntries(int w, int h, uint collisionArgb, string dataTxt)
        {
            List<KeyValuePair<string, byte[]>> entries = new List<KeyValuePair<string, byte[]>>();
            entries.Add(new KeyValuePair<string, byte[]>("main.png", FilledPng(w, h, SyntheticLevel.MainFill)));
            entries.Add(new KeyValuePair<string, byte[]>("background.png", FilledPng(w, h, 0xFF654321)));
            entries.Add(new KeyValuePair<string, byte[]>("collision.png", FilledPng(w, h, collisionArgb)));
            entries.Add(new KeyValuePair<string, byte[]>("data.txt", Encoding.ASCII.GetBytes(dataTxt)));
            return entries;
        }
    }
}
