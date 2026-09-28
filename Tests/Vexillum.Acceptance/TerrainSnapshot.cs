using System;
using System.Security.Cryptography;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// The terrain bitfield as TerrainArray.ToBytes() lays it out: column-major
    /// (x outer, y inner), 8 pixels per byte, least significant bit first, bit 0
    /// (solid) only. World y is up (y = 0 is the bottom row). Received from the
    /// server in packet 3 (after LZMA) or produced by a HeadlessLevel.
    /// </summary>
    public sealed class TerrainSnapshot
    {
        public byte[] Bits { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public TerrainSnapshot(byte[] bits, int width, int height)
        {
            if (width > 0 && height > 0 && bits.Length != (width * height) / 8)
                throw new ArgumentException("bitfield has " + bits.Length + " bytes, expected " + (width * height) / 8 + " for " + width + "x" + height);
            Bits = bits;
            Width = width;
            Height = height;
        }

        /// <summary>Solid bit of pixel (x, y); needs Width/Height (throws otherwise).</summary>
        public bool IsSolid(int x, int y)
        {
            if (Width <= 0 || Height <= 0)
                throw new InvalidOperationException("map size unknown; construct with width/height");
            if (x < 0 || y < 0 || x >= Width || y >= Height)
                return true; // TerrainArray.GetTerrain: out of bounds is solid
            int idx = x * Height + y;
            return (Bits[idx >> 3] & (1 << (idx & 7))) != 0;
        }

        /// <summary>Number of solid pixels.</summary>
        public int SolidCount()
        {
            int n = 0;
            foreach (byte b in Bits)
                n += System.Numerics.BitOperations.PopCount(b);
            return n;
        }

        /// <summary>SHA-256 hex of the bitfield (comparable to terrain_reference and LevelTerrainTests).</summary>
        public string Sha256Hex()
        {
            return MapFile.Hex(SHA256.HashData(Bits));
        }

        /// <summary>Pixels whose solid bit differs between two snapshots of the same size.</summary>
        public int CountDifferences(TerrainSnapshot other)
        {
            if (other.Bits.Length != Bits.Length)
                throw new ArgumentException("size mismatch");
            int n = 0;
            for (int i = 0; i < Bits.Length; i++)
                n += System.Numerics.BitOperations.PopCount((byte)(Bits[i] ^ other.Bits[i]));
            return n;
        }
    }
}
