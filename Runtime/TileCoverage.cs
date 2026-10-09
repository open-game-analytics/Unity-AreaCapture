using System;

namespace AreaCapture
{
    /// <summary>Pure C# (no UnityEngine) so the rule can be tested outside Unity.</summary>
    public static class TileCoverage
    {
        /// <summary>
        /// True when a tile has nothing to show: every pixel is fully transparent. <paramref name="rgba"/> is
        /// 4 bytes per pixel (RGBA32), as <c>Texture2D.GetRawTextureData</c> returns it.
        /// </summary>
        public static bool IsEmpty(ReadOnlySpan<byte> rgba)
        {
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] != 0) return false;
            return true;
        }
    }
}
