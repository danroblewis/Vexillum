using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SevenZip.Compression.LZMA;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// Reads a .map container the way LevelLoader.LoadData does (magic
    /// 0x004F876B, LZMA-alone payload, long name, then (name, int64 length,
    /// bytes) entries until an empty name) without decoding the images, plus
    /// the MD5 the client sends in its Status packet (LevelLoader.LevelMd5 =
    /// MD5 of the whole file, magic included).
    /// </summary>
    public sealed class MapFile
    {
        public string Path { get; private set; }
        public string LongName { get; private set; }
        /// <summary>Entries in file order.</summary>
        public List<KeyValuePair<string, byte[]>> Entries { get; private set; }
        /// <summary>The bytes after the magic number (what the server sends in packets 220-222).</summary>
        public byte[] CompressedPayload { get; private set; }
        /// <summary>Size of collision.png, i.e. the level's width and height in pixels.</summary>
        public int Width { get; private set; }
        public int Height { get; private set; }

        public byte[] Entry(string name)
        {
            foreach (KeyValuePair<string, byte[]> e in Entries)
                if (e.Key == name)
                    return e.Value;
            return null;
        }

        public static MapFile Read(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            return Parse(file, path);
        }

        /// <summary>Parses map bytes including the 4-byte magic.</summary>
        public static MapFile Parse(byte[] file, string path)
        {
            if (file.Length < 4 || BitConverter.ToInt32(file, 0) != Protocol.MapMagic)
                throw new InvalidDataException("not a Vexillum map (bad magic): " + path);
            byte[] compressed = new byte[file.Length - 4];
            Array.Copy(file, 4, compressed, 0, compressed.Length);
            byte[] payload = SevenZipHelper.Decompress(compressed);
            MapFile m = new MapFile();
            m.Path = path;
            m.CompressedPayload = compressed;
            m.Entries = new List<KeyValuePair<string, byte[]>>();
            using (BinaryReader r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                m.LongName = r.ReadString();
                while (r.BaseStream.Position < r.BaseStream.Length)
                {
                    string name = r.ReadString();
                    if (name.Trim().Length == 0)
                        break;
                    long len = r.ReadInt64();
                    m.Entries.Add(new KeyValuePair<string, byte[]>(name, r.ReadBytes((int)len)));
                }
            }
            byte[] col = m.Entry("collision.png");
            if (col != null && col.Length >= 24)
            {
                m.Width = (col[16] << 24) | (col[17] << 16) | (col[18] << 8) | col[19];
                m.Height = (col[20] << 24) | (col[21] << 16) | (col[22] << 8) | col[23];
            }
            return m;
        }

        /// <summary>MD5 of the file, as LevelLoader.LevelMd5 computes it.</summary>
        public static byte[] Md5(string path)
        {
            using (FileStream s = File.OpenRead(path))
            using (MD5 md5 = MD5.Create())
                return md5.ComputeHash(s);
        }

        public static string Hex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
