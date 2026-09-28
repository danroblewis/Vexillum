using System;

namespace System.Drawing.Imaging
{
    /// <summary>GDI+ pixel format identifiers with their original numeric values.</summary>
    public enum PixelFormat
    {
        Indexed = 0x00010000,
        Gdi = 0x00020000,
        Alpha = 0x00040000,
        PAlpha = 0x00080000,
        Extended = 0x00100000,
        Canonical = 0x00200000,
        Undefined = 0,
        DontCare = 0,
        Format1bppIndexed = 0x00030101,
        Format4bppIndexed = 0x00030402,
        Format8bppIndexed = 0x00030803,
        Format16bppGrayScale = 0x00101004,
        Format16bppRgb555 = 0x00021005,
        Format16bppRgb565 = 0x00021006,
        Format16bppArgb1555 = 0x00061007,
        Format24bppRgb = 0x00021808,
        Format32bppRgb = 0x00022009,
        Format32bppArgb = 0x0026200A,
        Format32bppPArgb = 0x000E200B,
        Format48bppRgb = 0x0010300C,
        Format64bppArgb = 0x0034400D,
        Format64bppPArgb = 0x001C400E,
        Max = 15
    }
}
