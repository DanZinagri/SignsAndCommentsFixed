using System.Collections.Generic;
using UnityEngine;

namespace Dark.Signs
{
    // One glyph copied out of the game's font atlas: its coverage bitmap plus layout metrics.
    //
    // Labels are composed from these copies, never straight from the atlas. The atlas is shared
    // with every piece of UI text in the game and gets rebuilt or repacked whenever it fills, so
    // a glyph read from it once can be gone or moved a frame later. A copy cannot, so once a
    // character has been read successfully at a size, later bakes never touch the atlas for it.
    internal sealed class CachedGlyph
    {
        public bool visible;
        public float x0, y0, x1, y1;   // glyph box in source px, relative to pen (layout per version)
        public float advance;          // source px

        public int width, height;      // bitmap size in texels
        public byte[] alpha;           // coverage, bottom-up rows

        // Where the glyph sits inside the bitmap, in texels (y counted up from the bottom row):
        // x at the glyph's left (A) and right (B) edge, y at its top (A) and bottom (B) edge.
        public float sxA, sxB, syA, syB;

        // Average coverage over the part of the glyph spanning fractions fx0..fx1 across and
        // fy0..fy1 down (0 = left/top, 1 = right/bottom).
        public float Coverage(float fx0, float fx1, float fy0, float fy1)
        {
            float xa = sxA + (sxB - sxA) * fx0, xb = sxA + (sxB - sxA) * fx1;
            float ya = syA + (syB - syA) * fy0, yb = syA + (syB - syA) * fy1;
            return AreaCoverage(Mathf.Min(xa, xb), Mathf.Max(xa, xb), Mathf.Min(ya, yb), Mathf.Max(ya, yb));
        }

        // Coverage over a rectangle in texels. When one label pixel spans several texels (glyph
        // shrunk to label size) they are averaged; a rectangle smaller than a texel is
        // interpolated instead.
        private float AreaCoverage(float x0, float x1, float y0, float y1)
        {
            if (width == 0 || height == 0) return 0f;
            if (x1 - x0 <= 1f && y1 - y0 <= 1f)
            {
                float cx = Mathf.Clamp((x0 + x1) * 0.5f - 0.5f, 0f, width - 1);
                float cy = Mathf.Clamp((y0 + y1) * 0.5f - 0.5f, 0f, height - 1);
                int ix = (int)cx, iy = (int)cy;
                int ix1 = Mathf.Min(ix + 1, width - 1), iy1 = Mathf.Min(iy + 1, height - 1);
                float tx = cx - ix, ty = cy - iy;
                float bottom = Mathf.Lerp(alpha[iy * width + ix], alpha[iy * width + ix1], tx);
                float top = Mathf.Lerp(alpha[iy1 * width + ix], alpha[iy1 * width + ix1], tx);
                return Mathf.Lerp(bottom, top, ty) / 255f;
            }

            int xs = Mathf.Max(0, Mathf.FloorToInt(x0)), xe = Mathf.Min(width, Mathf.CeilToInt(x1));
            int ys = Mathf.Max(0, Mathf.FloorToInt(y0)), ye = Mathf.Min(height, Mathf.CeilToInt(y1));
            float sum = 0f, area = 0f;
            for (int y = ys; y < ye; y++)
            {
                float wy = Mathf.Min(y + 1f, y1) - Mathf.Max(y, y0);
                if (wy <= 0f) continue;
                int row = y * width;
                for (int x = xs; x < xe; x++)
                {
                    float wx = Mathf.Min(x + 1f, x1) - Mathf.Max(x, x0);
                    if (wx <= 0f) continue;
                    float w = wx * wy;
                    sum += alpha[row + x] * w;
                    area += w;
                }
            }
            return area > 0f ? sum / (area * 255f) : 0f;
        }

        // False when a visible glyph came out blank: the atlas copy did not hold it (yet).
        public bool HasInk()
        {
            if (!visible) return true;
            if (alpha == null) return false;
            for (int i = 0; i < alpha.Length; i++)
            {
                if (alpha[i] != 0) return true;
            }
            return false;
        }
    }

    // Session-wide store of glyph copies. Never cleared: fonts don't change within a session,
    // and the set stays small (the characters used on signs, per font and size).
    internal static class GlyphCache
    {
        private static readonly Dictionary<(int font, int size, int style, char c), CachedGlyph> glyphs =
            new Dictionary<(int, int, int, char), CachedGlyph>();

        public static int Count => glyphs.Count;

        public static bool TryGet(int font, int size, int style, char c, out CachedGlyph glyph)
        {
            return glyphs.TryGetValue((font, size, style, c), out glyph);
        }

        public static void Add(int font, int size, int style, char c, CachedGlyph glyph)
        {
            glyphs[(font, size, style, c)] = glyph;
        }
    }
}
