using System;
using System.Collections.Generic;
using UnityEngine;
using UnityStubs.Gui;
using Verse;

namespace Dark.Signs
{
    // Pre-rendered label for one sign: background boxes plus text, baked into a small RGBA
    // texture composed on the CPU from the game's glyph atlas.
    // EnsureBaked returns false while the atlas is not available yet, and for text that
    // cannot be baked at all (see Unbakeable); callers retry or draw the GUI way.
    // If anything in the bake throws, the class disables itself for the session.
    public class SignLabelTexture
    {
        public static bool Broken { get; private set; }

        public const float WorldBakeScale = 2f;   // oversample world labels for mipmapping

        // ---- Font atlas: one CPU copy, shared by all labels ----
        //
        // 1.7 has no Unity font atlas. The game's text backend keeps a readable mirror of its
        // glyph atlas, but only the glyphs it pre-bakes at one large size are guaranteed to be
        // in that mirror; glyphs rasterised later at other sizes are not copied across. So
        // labels are composed from the pre-baked glyphs, scaled down to the label's size, and
        // the copy is refreshed whenever the backend reports a new atlas version.
        private class AtlasCopy
        {
            public int version;        // ITextMeshProBackend.AtlasVersion when copied
            public bool readable;      // copied straight from CPU memory rather than via a blit
            public int width;
            public int height;
            public Color32[] pixels;   // bottom-up rows, as GetPixels32
        }

        private static AtlasCopy atlasCopy;
        private static bool lastReadDirect;
        private static Color32? bgColor;   // centre pixel of TexUI.GrayTextBG
        private static int totalBuilds;

        // The current CPU copy of the glyph atlas, or null while the backend has none yet.
        private static AtlasCopy TryGetAtlas(ITextMeshProBackend backend, int fontIndex)
        {
            int version = backend.AtlasVersion;
            if (atlasCopy != null && atlasCopy.version == version)
            {
                return atlasCopy;
            }
            Texture2D atlas = backend.GetAtlasTexture(fontIndex);
            if (atlas == null)
            {
                return null;
            }
            atlasCopy = new AtlasCopy
            {
                version = version,
                width = atlas.width,
                height = atlas.height,
                pixels = ReadPixels(atlas)
            };
            atlasCopy.readable = lastReadDirect;
            return atlasCopy;
        }

        // Drop the copy so the next bake takes a fresh one.
        private static void InvalidateAtlas()
        {
            atlasCopy = null;
        }

        // CPU copy of any texture. Reads directly when allowed, otherwise via a blit.
        private static Color32[] ReadPixels(Texture tex)
        {
            lastReadDirect = tex is Texture2D t2 && t2.isReadable;
            if (lastReadDirect)
            {
                return ((Texture2D)tex).GetPixels32();
            }
            RenderTexture rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var reader = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
                reader.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
                Color32[] px = reader.GetPixels32();
                UnityEngine.Object.Destroy(reader);
                return px;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static Color32 BgColor
        {
            get
            {
                if (bgColor == null)
                {
                    Texture bg = TexUI.GrayTextBG;
                    Color32[] px = ReadPixels(bg);
                    bgColor = px[(bg.height / 2) * bg.width + bg.width / 2];
                }
                return bgColor.Value;
            }
        }

        // ---- Per-sign state ----
        private static readonly GUIContent tmpContent = new GUIContent();

        private Texture2D tex;
        private Material worldMaterial;
        private Rect bounds;   // UI units (unscaled px), relative to the anchor point

        public Texture2D Texture => tex;
        public Rect Bounds => bounds;

        // Own material rather than MaterialPool: the pool never releases entries.
        public Material WorldMaterial
        {
            get
            {
                if (tex == null) return null;
                if (worldMaterial == null)
                {
                    // None of the map's transparent shaders write depth, so between them the
                    // render queue decides what's on top, not altitude. Comments, billboards
                    // and neon signs use TransparentPostLight; go after that and after the
                    // overlay shader so the label is above every sign graphic (and, like the
                    // old GUI labels, not darkened by night lighting).
                    worldMaterial = new Material(ShaderDatabase.TransparentPostLight) { name = "SignLabel" };
                    worldMaterial.renderQueue = Mathf.Max(ShaderDatabase.TransparentPostLight.renderQueue,
                        ShaderDatabase.MetaOverlay.renderQueue) + 50;
                }
                worldMaterial.mainTexture = tex;
                return worldMaterial;
            }
        }

        // Keys of the current bake
        private bool built;
        private string[] builtLines;
        private GameFont builtFont;
        private Color builtColor;
        private float builtScale;
        private bool builtWorld;

        // True when the current text has a character the backend has no pre-baked glyph for
        // (anything outside its Latin set). Such a label cannot be composed here, so callers
        // draw it the GUI way instead. Re-evaluated whenever the inputs change.
        private bool unbakeable;
        public bool Unbakeable => unbakeable;

        private int staleRetries;
        private const int MaxStaleRetries = 5;

        // True when the texture matches the inputs and is ready to use. False while a bake
        // is pending (glyphs uploading) or after a failure; callers fall back or retry.
        public bool EnsureBaked(string[] lines, GameFont font, Color color, float scale, bool world)
        {
            if (Broken) return false;

            string reason = null;
            if (!built) reason = "new";
            else if (!ReferenceEquals(lines, builtLines)) reason = "lines";
            else if (font != builtFont) reason = "font";
            else if (color != builtColor) reason = "color";
            else if (scale != builtScale) reason = "scale";
            else if (world != builtWorld) reason = "mode";
            if (reason == null) return !unbakeable;

            try
            {
                return Rebuild(lines, font, color, scale, world, reason);
            }
            catch (Exception e)
            {
                Broken = true;
                Log.Warning("(Signs) Cached label rendering failed, falling back to GUI labels: " + e);
                return false;
            }
            finally
            {
                Text.Font = GameFont.Small;
            }
        }

        // Screen-space draw. Returns false when the label could not be drawn from the cache
        // this frame; the caller then draws it the IMGUI way.
        public bool Draw(Vector2 anchor, string[] lines, GameFont font, Color color)
        {
            float scale = Prefs.UIScale;
            if (!EnsureBaked(lines, font, color, scale, false)) return false;
            if (tex == null) return true;   // nothing to draw

            // Snap to physical pixels so the bake draws 1:1.
            var r = new Rect(
                Mathf.Round((anchor.x + bounds.x) * scale) / scale,
                Mathf.Round((anchor.y + bounds.y) * scale) / scale,
                tex.width / scale,
                tex.height / scale);
            if (r.Overlaps(new Rect(0f, 0f, UI.screenWidth, UI.screenHeight)))
            {
                GUI.DrawTexture(r, tex);
            }
            return true;
        }

        // Takes the atlas copy and bakes. False while the backend has no atlas yet.
        private bool Rebuild(string[] lines, GameFont font, Color color, float scale, bool world, string reason)
        {
            Text.Font = font;   // resolves the Tiny -> Small fallback; caller resets it
            GUIStyle style = Text.CurFontStyle;
            Font unityFont = style.font;
            int baseSize = style.fontSize > 0 ? style.fontSize
                : unityFont.fontSize > 0 ? unityFont.fontSize
                : Mathf.RoundToInt(style.lineHeight);
            int fontSizePx = Mathf.Max(1, Mathf.RoundToInt(baseSize * scale));

            ITextMeshProBackend backend = TextMeshProBackend.Current;
            if (backend == null) return false;   // text system not up yet; try again next frame

            AtlasCopy atlas = TryGetAtlas(backend, unityFont.GameFontIndex);
            if (atlas == null) return false;

            return Build(lines, font, color, scale, world, style, backend, atlas, fontSizePx, baseSize, reason);
        }

        private void SetBuiltKeys(string[] lines, GameFont font, Color color, float scale, bool world)
        {
            unbakeable = false;
            built = true;
            builtLines = lines;
            builtFont = font;
            builtColor = color;
            builtScale = scale;
            builtWorld = world;
            staleRetries = 0;
        }

        private struct Glyph
        {
            public float left, top, right, bottom;   // px, relative to anchor
            public float u0, v0, u1, v1;             // atlas UVs, v measured from the top
        }

        private static readonly List<Glyph> glyphs = new List<Glyph>();
        private static readonly List<Rect> bgs = new List<Rect>();

        // Coverage of the atlas over a rectangle given in atlas texels (y counted up from the
        // bottom row). Shrinking a pre-baked glyph to label size means one label pixel spans
        // several texels, so they are averaged; a rectangle smaller than a texel is
        // interpolated instead.
        private static float AtlasCoverage(AtlasCopy atlas, float x0, float x1, float y0, float y1)
        {
            if (x1 - x0 <= 1f && y1 - y0 <= 1f)
            {
                float cx = Mathf.Clamp((x0 + x1) * 0.5f - 0.5f, 0f, atlas.width - 1);
                float cy = Mathf.Clamp((y0 + y1) * 0.5f - 0.5f, 0f, atlas.height - 1);
                int ix = (int)cx, iy = (int)cy;
                int ix1 = Mathf.Min(ix + 1, atlas.width - 1), iy1 = Mathf.Min(iy + 1, atlas.height - 1);
                float tx = cx - ix, ty = cy - iy;
                float bottom = Mathf.Lerp(atlas.pixels[iy * atlas.width + ix].a, atlas.pixels[iy * atlas.width + ix1].a, tx);
                float top = Mathf.Lerp(atlas.pixels[iy1 * atlas.width + ix].a, atlas.pixels[iy1 * atlas.width + ix1].a, tx);
                return Mathf.Lerp(bottom, top, ty) / 255f;
            }

            int xa = Mathf.Max(0, Mathf.FloorToInt(x0)), xb = Mathf.Min(atlas.width, Mathf.CeilToInt(x1));
            int ya = Mathf.Max(0, Mathf.FloorToInt(y0)), yb = Mathf.Min(atlas.height, Mathf.CeilToInt(y1));
            float sum = 0f, area = 0f;
            for (int y = ya; y < yb; y++)
            {
                float wy = Mathf.Min(y + 1f, y1) - Mathf.Max(y, y0);
                if (wy <= 0f) continue;
                int row = y * atlas.width;
                for (int x = xa; x < xb; x++)
                {
                    float wx = Mathf.Min(x + 1f, x1) - Mathf.Max(x, x0);
                    if (wx <= 0f) continue;
                    float w = wx * wy;
                    sum += atlas.pixels[row + x].a * w;
                    area += w;
                }
            }
            return area > 0f ? sum / (area * 255f) : 0f;
        }

        // Returns false if the atlas copy turned out to predate our glyphs (caller retries next
        // frame), or if the text cannot be baked at all (see Unbakeable).
        private bool Build(string[] lines, GameFont font, Color color, float scale, bool world, GUIStyle style,
            ITextMeshProBackend backend, AtlasCopy atlas, int fontSizePx, int baseSize, string reason)
        {
            Font unityFont = style.font;
            int fontIndex = unityFont.GameFontIndex;
            string allText = string.Concat(lines);

            // Pre-baked glyphs are at the backend's own bake size; k brings them to label size.
            if (!backend.TryGetFontMetrics(fontIndex, out TmpFontMetrics bakedMetrics) || backend.BakePixelSize <= 0f)
            {
                return false;
            }
            float k = fontSizePx / backend.BakePixelSize;

            float bgFraction = Comp_Sign.GetLineHeightBGFraction(font);
            // Line step is measured with the Small font, as the original did.
            Text.Font = GameFont.Small;
            float lineStep = Text.LineHeight * bgFraction * 1.1f;
            if (font == GameFont.Medium) lineStep += 4f;
            Text.Font = font;

            float ascentPx = unityFont.ascent * ((float)fontSizePx / baseSize);
            RectOffset pad = style.padding;
            Vector2 contentOffset = style.contentOffset;
            Color textColor = color * style.normal.textColor;

            // The GUI's ascent for this font against the pre-baked one at label size; they agree
            // to within rounding, and the difference keeps the text on the GUI's baseline.
            float ascentShift = ascentPx - bakedMetrics.ascent * k;
            bool missingGlyph = false;

            glyphs.Clear();
            bgs.Clear();
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;

            float y = 0f;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i];
                if (s.Length > 0)
                {
                    tmpContent.text = s;
                    Vector2 size = style.CalcSize(tmpContent);
                    float rx = -size.x / 2f;
                    float ry = y - 3f;

                    bgs.Add(new Rect(rx - 4f, y, size.x + 8f, size.y * bgFraction));
                    minX = Mathf.Min(minX, rx - 4f);
                    maxX = Mathf.Max(maxX, rx + size.x + 4f);
                    minY = Mathf.Min(minY, ry);
                    maxY = Mathf.Max(maxY, y + size.y);

                    // Scaled-down glyphs can come out a pixel or two narrower or wider than the
                    // GUI measured, so centre the run inside the box the GUI size gave us.
                    float runWidth = 0f;
                    for (int c = 0; c < s.Length; c++)
                    {
                        if (!backend.TryGetGlyph(fontIndex, s[c], out TmpGlyphMetrics m) || !m.found)
                        {
                            missingGlyph = true;
                            break;
                        }
                        runWidth += m.advanceX * k;
                    }
                    if (missingGlyph) break;

                    float inner = (size.x - pad.left - pad.right) * scale;
                    float penX = (rx + pad.left + contentOffset.x) * scale + (inner - runWidth) * 0.5f;
                    float lineTop = (ry + pad.top + contentOffset.y) * scale + ascentShift;
                    for (int c = 0; c < s.Length; c++)
                    {
                        backend.TryGetGlyph(fontIndex, s[c], out TmpGlyphMetrics m);
                        if (m.visible)
                        {
                            glyphs.Add(new Glyph
                            {
                                left = penX + m.x0 * k,
                                top = lineTop + m.y0 * k,
                                right = penX + m.x1 * k,
                                bottom = lineTop + m.y1 * k,
                                u0 = m.u0, v0 = m.v0, u1 = m.u1, v1 = m.v1
                            });
                        }
                        penX += m.advanceX * k;
                    }
                }
                y += lineStep;
            }

            if (missingGlyph)
            {
                // Remember the inputs so this is not re-checked every frame, then report it.
                DestroyTexture();
                bounds = new Rect(0f, 0f, 0f, 0f);
                SetBuiltKeys(lines, font, color, scale, world);
                unbakeable = true;
                return false;
            }

            if (bgs.Count == 0)
            {
                DestroyTexture();
                bounds = new Rect(0f, 0f, 0f, 0f);
                SetBuiltKeys(lines, font, color, scale, world);
                return true;
            }

            // Texture covers the union of boxes and text, in physical pixels.
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            int W = Mathf.Max(1, Mathf.CeilToInt(bounds.width * scale));
            int H = Mathf.Max(1, Mathf.CeilToInt(bounds.height * scale));
            float ox = bounds.x * scale;   // texture origin, px relative to anchor
            float oy = bounds.y * scale;
            var px = new Color[W * H];      // straight alpha, row 0 = top (flipped on upload)

            Color bg = BgColor;
            for (int i = 0; i < bgs.Count; i++)
            {
                Rect b = bgs[i];
                int x0 = Mathf.Clamp(Mathf.RoundToInt(b.x * scale - ox), 0, W);
                int x1 = Mathf.Clamp(Mathf.RoundToInt(b.xMax * scale - ox), 0, W);
                int y0 = Mathf.Clamp(Mathf.RoundToInt(b.y * scale - oy), 0, H);
                int y1 = Mathf.Clamp(Mathf.RoundToInt(b.yMax * scale - oy), 0, H);
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                        px[yy * W + xx] = bg;
            }

            int staleGlyphs = 0;
            for (int g = 0; g < glyphs.Count; g++)
            {
                Glyph gl = glyphs[g];
                float gw = gl.right - gl.left, gh = gl.bottom - gl.top;
                if (gw <= 0f || gh <= 0f) continue;
                bool glyphHit = false;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(gl.left - ox));
                int x1 = Mathf.Min(W, Mathf.CeilToInt(gl.right - ox));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(gl.top - oy));
                int y1 = Mathf.Min(H, Mathf.CeilToInt(gl.bottom - oy));
                float du = gl.u1 - gl.u0, dv = gl.v1 - gl.v0;
                for (int yy = y0; yy < y1; yy++)
                {
                    // This label pixel's span across the glyph, as fractions of its height.
                    float fy0 = Mathf.Clamp01((yy + oy - gl.top) / gh);
                    float fy1 = Mathf.Clamp01((yy + 1f + oy - gl.top) / gh);
                    if (fy1 <= fy0) continue;
                    // Atlas v runs down from the top; the copy's rows run up from the bottom.
                    float sy0 = (1f - (gl.v0 + dv * fy1)) * atlas.height;
                    float sy1 = (1f - (gl.v0 + dv * fy0)) * atlas.height;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        float fx0 = Mathf.Clamp01((xx + ox - gl.left) / gw);
                        float fx1 = Mathf.Clamp01((xx + 1f + ox - gl.left) / gw);
                        if (fx1 <= fx0) continue;
                        float sx0 = (gl.u0 + du * fx0) * atlas.width;
                        float sx1 = (gl.u0 + du * fx1) * atlas.width;
                        // Scale by how much of this pixel the glyph actually covers at its edges.
                        float a = AtlasCoverage(atlas, sx0, sx1, sy0, sy1)
                            * ((fx1 - fx0) * gw) * ((fy1 - fy0) * gh) * textColor.a;
                        if (a <= 0f) continue;
                        if (a > 1f) a = 1f;
                        glyphHit = true;
                        ref Color dst = ref px[yy * W + xx];
                        float outA = a + dst.a * (1f - a);
                        float r = (textColor.r * a + dst.r * dst.a * (1f - a)) / outA;
                        float gg = (textColor.g * a + dst.g * dst.a * (1f - a)) / outA;
                        float bb = (textColor.b * a + dst.b * dst.a * (1f - a)) / outA;
                        dst = new Color(r, gg, bb, outA);
                    }
                }
                if (!glyphHit) staleGlyphs++;
            }

            if (staleGlyphs > 0 && staleRetries < MaxStaleRetries)
            {
                // The atlas copy predates the upload of these glyphs. Recopy and retry next frame.
                staleRetries++;
                InvalidateAtlas();
                if (Prefs.DevMode && totalBuilds < 10)
                {
                    Log.Message($"(Signs) label bake deferred: {staleGlyphs}/{glyphs.Count} glyph(s) missing from the atlas copy (retry {staleRetries})");
                }
                return false;
            }

            // Upload, flipping rows because Texture2D row 0 is the bottom.
            var upload = new Color32[W * H];
            for (int yy = 0; yy < H; yy++)
            {
                int src = yy * W, dstRow = (H - 1 - yy) * W;
                for (int xx = 0; xx < W; xx++) upload[dstRow + xx] = px[src + xx];
            }
            if (tex == null)
            {
                // Mipmaps so the world-space quad stays legible when zoomed out.
                tex = new Texture2D(W, H, TextureFormat.RGBA32, true)
                {
                    name = "SignLabel",
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
            }
            else if (tex.width != W || tex.height != H)
            {
                tex.Reinitialize(W, H);   // same object, so the world material stays valid
            }
            tex.SetPixels32(upload);
            tex.Apply(true, false);
            SetBuiltKeys(lines, font, color, scale, world);

            totalBuilds++;
            if (Prefs.DevMode && (totalBuilds <= 3 || totalBuilds % 500 == 0))
            {
                Log.Message($"(Signs) label bake #{totalBuilds} ({reason}, {(world ? "world" : "screen")}): font={unityFont.name} sizePx={fontSizePx} scale={scale} " +
                    $"atlas={atlas.width}x{atlas.height} readable={atlas.readable} glyphs={glyphs.Count}/{allText.Length} stale={staleGlyphs} " +
                    $"tex={W}x{H} bounds={bounds} bg={(Color)bg}");
            }
            return true;
        }

        private void DestroyTexture()
        {
            if (tex != null)
            {
                UnityEngine.Object.Destroy(tex);
                tex = null;
            }
        }

        public void Dispose()
        {
            DestroyTexture();
            if (worldMaterial != null)
            {
                UnityEngine.Object.Destroy(worldMaterial);
                worldMaterial = null;
            }
            built = false;
            builtLines = null;
            unbakeable = false;
        }
    }
}
