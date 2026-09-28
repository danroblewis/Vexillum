using System;

namespace System.Drawing.Imaging
{
    /// <summary>
    /// GDI+ codec descriptor. There are no GDI+ codecs on this platform, so
    /// GetImageEncoders/GetImageDecoders return empty arrays. The only caller,
    /// MapCreator.GetJpgEncoder, is never invoked and returns null on that.
    /// </summary>
    public sealed class ImageCodecInfo
    {
        private static readonly ImageCodecInfo[] none = new ImageCodecInfo[0];

        internal ImageCodecInfo()
        {
        }

        public Guid Clsid { get; set; }
        public Guid FormatID { get; set; }
        public string CodecName { get; set; }
        public string DllName { get; set; }
        public string FormatDescription { get; set; }
        public string FilenameExtension { get; set; }
        public string MimeType { get; set; }

        public static ImageCodecInfo[] GetImageEncoders()
        {
            return none;
        }

        public static ImageCodecInfo[] GetImageDecoders()
        {
            return none;
        }
    }
}
