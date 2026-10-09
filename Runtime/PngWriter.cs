using System;
using System.IO;
using System.IO.Compression;

namespace AreaCapture
{
    /// <summary>
    /// Pure C# RGBA8 PNG encoder. Unlike <c>Texture2D.EncodeToPNG</c> it touches no Unity object, so it can run on
    /// a worker thread while the main thread keeps rendering.
    /// </summary>
    public static class PngWriter
    {
        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <param name="rgba">Tightly packed RGBA bytes, <c>width * height * 4</c> long.</param>
        /// <param name="bottomUp">True for <c>Texture2D.GetRawTextureData</c> order (first row is the bottom row).</param>
        public static byte[] EncodeRgba(byte[] rgba, int width, int height, bool bottomUp, CompressionLevel level = CompressionLevel.Fastest)
        {
            int stride = width * 4;
            if (rgba == null || rgba.Length < stride * height)
                throw new ArgumentException("Pixel buffer is smaller than width * height * 4.", nameof(rgba));

            // One filter byte (0 = none) in front of every scanline
            var raw = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int srcRow = bottomUp ? height - 1 - y : y;
                Buffer.BlockCopy(rgba, srcRow * stride, raw, y * (stride + 1) + 1, stride);
            }

            var output = new MemoryStream(raw.Length / 4 + 1024);
            output.Write(Signature, 0, Signature.Length);

            var ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)width);
            WriteBigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 6;  // colour type: RGBA
            WriteChunk(output, "IHDR", ihdr, ihdr.Length);

            using (var zlib = new MemoryStream(raw.Length / 4 + 1024))
            {
                zlib.WriteByte(0x78); // zlib header: deflate, 32K window
                zlib.WriteByte(0x01);
                using (var deflate = new DeflateStream(zlib, level, true))
                    deflate.Write(raw, 0, raw.Length);
                var adler = new byte[4];
                WriteBigEndian(adler, 0, Adler32(raw));
                zlib.Write(adler, 0, 4);
                WriteChunk(output, "IDAT", zlib.GetBuffer(), (int)zlib.Length);
            }

            WriteChunk(output, "IEND", new byte[0], 0);
            return output.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data, int length)
        {
            var len = new byte[4];
            WriteBigEndian(len, 0, (uint)length);
            s.Write(len, 0, 4);

            var typeBytes = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, length);

            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < 4; i++) crc = CrcTable[(crc ^ typeBytes[i]) & 0xFF] ^ (crc >> 8);
            for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            var crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, crc ^ 0xFFFFFFFFu);
            s.Write(crcBytes, 0, 4);
        }

        private static uint Adler32(byte[] data)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int end = Math.Min(i + 5552, data.Length); // largest block that cannot overflow uint
                for (; i < end; i++)
                {
                    a += data[i];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
            }
            return (b << 16) | a;
        }

        private static void WriteBigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
