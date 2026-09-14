using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Dark.Signs
{
    // Pre-rendered label for one sign: background boxes plus text, baked into a small RGBA
    // texture composed on the CPU from the font atlas. 
    // Newly requested glyphs reach the GPU atlas only on the next render, so a bake is
    // deferred one frame after a request; EnsureBaked returns false until then.
    // If anything in the bake throws, the class disables itself for the session.
    public class SignLabelTexture
    {
        public static bool Broken { get; private set; }

        public const float WorldBakeScale = 2f;   // oversample world labels for mipmapping

        // ---- Font atlas: rebuild tracking and CPU copies, shared by all labels ----
        private static readonly Dictionary<Font, int> atlasGenerations = new Dictionary<Font, int>();

        private class AtlasCopy
        {
            public int generation;
            public int frame;          // Time.frameCount when copied
            public bool readable;      // copied straight from CPU memory rather than via a blit
            public int width;
            public int height;
            public Color32[] pixels;   // bottom-up rows, as Unity's GetPixels32
        }

        private static readonly Dictionary<Font, AtlasCopy> atlasCopies = new Dictionary<Font, AtlasCopy>();
        private static readonly Dictionary<Font, int> lastRequestFrame = new Dictionary<Font, int>();
        private static bool lastReadDirect;
        private static Color32? bgColor;   // centre pixel of TexUI.GrayTextBG
        private static int totalBuilds;

        static SignLabelTexture()
        {
            Font.textureRebuilt += f =>
            {
                atlasGenerations.TryGetValue(f, out int g);
                atlasGenerations[f] = g + 1;
            };
        }

        private static int GenerationOf(Font f)
        {
            atlasGenerations.TryGetValue(f, out int g);
            return g;
        }

        // A fresh CPU copy of the font atlas, or null if glyphs were requested this frame.
        private static AtlasCopy TryGetAtlas(Font font)
        {
            int gen = GenerationOf(font);
            lastRequestFrame.TryGetValue(font, out int requested);
            if (atlasCopies.TryGetValue(font, out AtlasCopy copy) && copy.generation == gen && copy.frame > requested)
            {
                return copy;
            }
            if (requested >= Time.frameCount)
            {
                return null;
            }
            Texture atlas = font.material.mainTexture;
            copy = new AtlasCopy
            {
                generation = gen,
                frame = Time.frameCount,
                width = atlas.width,
                height = atlas.height,
                pixels = ReadPixels(atlas)
            };
            copy.readable = lastReadDirect;
            atlasCopies[font] = copy;
            return copy;
        }

        // Drop the copy and make the next one wait a frame.
        private static void InvalidateAtlas(Font font)
        {
            atlasCopies.Remove(font);
            lastRequestFrame[font] = Time.frameCount;
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

        // What we last asked the atlas for
        private string[] requestedLines;
        private Font requestedFont;
        private int requestedSizePx;
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
            if (reason == null) return true;

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

        // Requests the glyphs, waits until the atlas copy is newer than the request, then bakes.
        private bool Rebuild(string[] lines, GameFont font, Color color, float scale, bool world, string reason)
        {
            Text.Font = font;   // resolves the Tiny -> Small fallback; caller resets it
            GUIStyle style = Text.CurFontStyle;
            Font unityFont = style.font;
            int baseSize = style.fontSize > 0 ? style.fontSize
                : unityFont.fontSize > 0 ? unityFont.fontSize
                : Mathf.RoundToInt(style.lineHeight);
            int fontSizePx = Mathf.Max(1, Mathf.RoundToInt(baseSize * scale));

            if (!ReferenceEquals(lines, requestedLines) || unityFont != requestedFont || fontSizePx != requestedSizePx)
            {
                unityFont.RequestCharactersInTexture(string.Concat(lines), fontSizePx, style.fontStyle);
                lastRequestFrame[unityFont] = Time.frameCount;
                requestedLines = lines;
                requestedFont = unityFont;
                requestedSizePx = fontSizePx;
                staleRetries = 0;
            }

            AtlasCopy atlas = TryGetAtlas(unityFont);
            if (atlas == null) return false;   // requested this frame; bake next frame

            return Build(lines, font, color, scale, world, style, atlas, fontSizePx, baseSize, reason);
        }

        private void SetBuiltKeys(string[] lines, GameFont font, Color color, float scale, bool world)
        {
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
            public CharacterInfo info;
        }

        private static readonly List<Glyph> glyphs = new List<Glyph>();
        private static readonly List<Rect> bgs = new List<Rect>();

        // Returns false if the atlas copy turned out to predate our glyphs; caller retries next frame.
        private bool Build(string[] lines, GameFont font, Color color, float scale, bool world, GUIStyle style,
            AtlasCopy atlas, int fontSizePx, int baseSize, string reason)
        {
            Font unityFont = style.font;
            FontStyle fontStyle = style.fontStyle;
            string allText = string.Concat(lines);

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

                    float penX = (rx + pad.left + contentOffset.x) * scale;
                    float baseline = (ry + pad.top + contentOffset.y) * scale + ascentPx;
                    for (int c = 0; c < s.Length; c++)
                    {
                        if (!unityFont.GetCharacterInfo(s[c], out CharacterInfo ci, fontSizePx, fontStyle))
                        {
                            continue;
                        }
                        glyphs.Add(new Glyph
                        {
                            left = penX + ci.minX,
                            top = baseline - ci.maxY,
                            right = penX + ci.maxX,
                            bottom = baseline - ci.minY,
                            info = ci
                        });
                        penX += ci.advance;
                    }
                }
                y += lineStep;
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
                CharacterInfo ci = gl.info;
                for (int yy = y0; yy < y1; yy++)
                {
                    float fy = (yy + 0.5f + oy - gl.top) / gh;
                    if (fy < 0f || fy >= 1f) continue;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        float fx = (xx + 0.5f + ox - gl.left) / gw;
                        if (fx < 0f || fx >= 1f) continue;
                        // Bilinear over the four UV corners handles rotated/flipped atlas glyphs.
                        Vector2 uv = Vector2.Lerp(Vector2.Lerp(ci.uvTopLeft, ci.uvTopRight, fx),
                                                  Vector2.Lerp(ci.uvBottomLeft, ci.uvBottomRight, fx), fy);
                        int ax = Mathf.Clamp((int)(uv.x * atlas.width), 0, atlas.width - 1);
                        int ay = Mathf.Clamp((int)(uv.y * atlas.height), 0, atlas.height - 1);
                        float a = atlas.pixels[ay * atlas.width + ax].a / 255f * textColor.a;
                        if (a <= 0f) continue;
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
                InvalidateAtlas(unityFont);
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
            requestedLines = null;
        }
    }
}
