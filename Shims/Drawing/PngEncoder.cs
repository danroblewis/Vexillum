using System;
using System.IO;
using System.IO.Compression;

namespace System.Drawing
{
    /// <summary>
    /// Minimal PNG writer for Bitmap.Save(stream, ImageFormat.Png): 8-bit RGBA
    /// (colour type 6), no interlace, filter type None on every row, one IDAT
    /// chunk compressed with System.IO.Compression.ZLibStream (zlib header and
    /// Adler-32 included), CRC-32 on every chunk. Pixels are written with
    /// straight alpha, exactly as stored, so Texture2D.FromStream (StbImageSharp)
    /// reproduces the bitmap's texels byte for byte.
    /// </summary>
    internal static class PngEncoder
    {
        private static readonly byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly uint[] crcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        internal static uint Crc32(byte[] buf, int offset, int count, uint crc)
        {
            uint c = crc ^ 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
                c = crcTable[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        /// <param name="bgra">B,G,R,A per pixel, top row first, stride width*4.</param>
        internal static void Write(Stream stream, byte[] bgra, int width, int height)
        {
            stream.Write(signature, 0, signature.Length);

            byte[] ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)width);
            WriteBigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8;    // bit depth
            ihdr[9] = 6;    // colour type: RGBA
            ihdr[10] = 0;   // compression: deflate
            ihdr[11] = 0;   // filter method
            ihdr[12] = 0;   // interlace: none
            WriteChunk(stream, "IHDR", ihdr, 0, ihdr.Length);

            int rowLength = width * 4;
            byte[] row = new byte[rowLength + 1];   // leading filter byte, always 0 (None)
            MemoryStream compressed = new MemoryStream(rowLength * height / 4 + 1024);
            using (ZLibStream z = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            {
                for (int y = 0; y < height; y++)
                {
                    int src = y * rowLength;
                    for (int x = 0; x < rowLength; x += 4)
                    {
                        row[1 + x] = bgra[src + x + 2];      // R
                        row[2 + x] = bgra[src + x + 1];      // G
                        row[3 + x] = bgra[src + x];          // B
                        row[4 + x] = bgra[src + x + 3];      // A
                    }
                    z.Write(row, 0, row.Length);
                }
            }
            byte[] idat = compressed.GetBuffer();
            WriteChunk(stream, "IDAT", idat, 0, (int)compressed.Length);

            WriteChunk(stream, "IEND", idat, 0, 0);
            stream.Flush();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data, int offset, int count)
        {
            byte[] header = new byte[8];
            WriteBigEndian(header, 0, (uint)count);
            header[4] = (byte)type[0];
            header[5] = (byte)type[1];
            header[6] = (byte)type[2];
            header[7] = (byte)type[3];
            stream.Write(header, 0, 8);
            if (count > 0)
                stream.Write(data, offset, count);
            uint crc = Crc32(header, 4, 4, 0);
            crc = Crc32(data, offset, count, crc);
            byte[] trailer = new byte[4];
            WriteBigEndian(trailer, 0, crc);
            stream.Write(trailer, 0, 4);
        }

        private static void WriteBigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }
    }
}
