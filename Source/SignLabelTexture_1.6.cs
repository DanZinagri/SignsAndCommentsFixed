using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Dark.Signs
{
    // Pre-rendered label for one sign: background boxes plus text, baked into a small RGBA
    // texture composed on the CPU from the font atlas. 1.6 build (Unity dynamic font atlas);
    // the 1.7 build uses SignLabelTexture.cs.
    // Newly requested glyphs reach the GPU atlas only on the next render, so a bake is
    // deferred one frame after a request; EnsureBaked returns false until then.
    // Unity rebuilds a full font atlas from only the characters requested that frame, so
    // glyphs requested a frame earlier can be gone by the time the bake reads them. A rebuild
    // since the request, or any character the atlas no longer has, re-requests and retries;
    // a bake is never kept with letters missing (see Unbakeable).
    // Each glyph is copied out of the atlas once (GlyphCache); after that, labels using it are
    // composed from the copy and never depend on the atlas again.
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
        private int requestedGeneration;   // atlas generation right after the request
        private bool rerequest;            // the request went stale; make it again
        private int staleRetries;
        private const int MaxStaleRetries = 5;

        // True when the label could not be composed with every letter after all retries.
        // Callers draw it the GUI way instead. Re-evaluated whenever the inputs change, and,
        // when it gave up only because the atlas was busy, again after RetryAfterFrames.
        private bool unbakeable;
        public bool Unbakeable => unbakeable;

        // A give-up caused by atlas churn is temporary: try the bake again later so the label
        // does not stay on the slow GUI path for the rest of the session. A character the
        // font does not have can never bake, so that kind is not retried.
        private const int RetryAfterFrames = 300;   // ~5 s at 60 fps
        private int retryAtFrame = -1;              // -1: no retry scheduled
        private bool retrying;                      // retry in progress; keep the GUI label up

        public bool RetryDue => unbakeable && retryAtFrame >= 0 && Time.frameCount >= retryAtFrame;
        // Draw this label the GUI way: it gave up, or a retry of a give-up has not landed yet.
        public bool DrawAsGui => unbakeable || retrying;

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
            if (reason == null && RetryDue)
            {
                unbakeable = false;
                retrying = true;
                retryAtFrame = -1;
                staleRetries = 0;
                rerequest = true;
                reason = "retry";
            }
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

            // Every character already copied: no request, no atlas copy, no frame of waiting.
            string uncached = UncachedChars(lines, unityFont, fontSizePx, style.fontStyle);
            if (uncached == null)
            {
                return Build(lines, font, color, scale, world, style, null, fontSizePx, baseSize, reason);
            }

            bool inputsChanged = !ReferenceEquals(lines, requestedLines) || unityFont != requestedFont || fontSizePx != requestedSizePx;
            if (inputsChanged) staleRetries = 0;
            // A rebuild since our request may have dropped our glyphs: ask again.
            if (inputsChanged || rerequest || GenerationOf(unityFont) != requestedGeneration)
            {
                unityFont.RequestCharactersInTexture(uncached, fontSizePx, style.fontStyle);
                lastRequestFrame[unityFont] = Time.frameCount;
                requestedLines = lines;
                requestedFont = unityFont;
                requestedSizePx = fontSizePx;
                // Read after the request: a rebuild it triggers keeps the characters it asked for.
                requestedGeneration = GenerationOf(unityFont);
                rerequest = false;
            }

            AtlasCopy atlas = TryGetAtlas(unityFont);
            if (atlas == null) return false;   // requested this frame; bake next frame

            return Build(lines, font, color, scale, world, style, atlas, fontSizePx, baseSize, reason);
        }

        private void SetBuiltKeys(string[] lines, GameFont font, Color color, float scale, bool world)
        {
            unbakeable = false;
            retrying = false;
            retryAtFrame = -1;
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
            public CachedGlyph source;
        }

        private static readonly List<Glyph> glyphs = new List<Glyph>();
        private static readonly List<Rect> bgs = new List<Rect>();

        // ---- Glyph copies (see GlyphCache) ----
        // Unity rasterises a glyph per size and style, so a copy is keyed by all three. Each is
        // stored upright at its native size, so labels at that size draw it 1:1.

        private static readonly System.Text.StringBuilder uncachedBuilder = new System.Text.StringBuilder();

        // The distinct characters of lines with no copy yet, or null when every one has one.
        private static string UncachedChars(string[] lines, Font unityFont, int sizePx, FontStyle fontStyle)
        {
            int fontId = unityFont.GetInstanceID();
            uncachedBuilder.Length = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i];
                for (int c = 0; c < s.Length; c++)
                {
                    char ch = s[c];
                    if (!GlyphCache.TryGet(fontId, sizePx, (int)fontStyle, ch, out _)
                        && uncachedBuilder.ToString().IndexOf(ch) < 0)
                    {
                        uncachedBuilder.Append(ch);
                    }
                }
            }
            return uncachedBuilder.Length == 0 ? null : uncachedBuilder.ToString();
        }

        private enum GlyphResult { Ok, NotInAtlas, Rebuilt, NotInCopy }

        // The glyph copy for c, read out of the atlas copy the first time. Anything but Ok means
        // the atlas could not be trusted for it this frame (retry); nothing is cached then.
        private static GlyphResult GetGlyph(Font unityFont, int sizePx, FontStyle fontStyle, char c, AtlasCopy atlas,
            out CachedGlyph glyph)
        {
            int fontId = unityFont.GetInstanceID();
            if (GlyphCache.TryGet(fontId, sizePx, (int)fontStyle, c, out glyph)) return GlyphResult.Ok;
            if (atlas == null) return GlyphResult.NotInCopy;   // not expected: Rebuild copies when anything is uncached
            if (!unityFont.GetCharacterInfo(c, out CharacterInfo ci, sizePx, fontStyle)) return GlyphResult.NotInAtlas;
            // Text layout (CalcSize) can rebuild the atlas; then ci and the copy disagree.
            if (GenerationOf(unityFont) != atlas.generation) return GlyphResult.Rebuilt;

            int w = ci.maxX - ci.minX, h = ci.maxY - ci.minY;
            glyph = new CachedGlyph
            {
                visible = w > 0 && h > 0,
                x0 = ci.minX, x1 = ci.maxX, y0 = ci.minY, y1 = ci.maxY,
                advance = ci.advance
            };
            if (glyph.visible)
            {
                var alpha = new byte[w * h];
                for (int gy = 0; gy < h; gy++)
                {
                    float fy = (gy + 0.5f) / h;   // 0 = top
                    int row = (h - 1 - gy) * w;  // stored bottom-up
                    for (int gx = 0; gx < w; gx++)
                    {
                        float fx = (gx + 0.5f) / w;
                        // Bilinear over the four UV corners handles rotated/flipped atlas glyphs.
                        Vector2 uv = Vector2.Lerp(Vector2.Lerp(ci.uvTopLeft, ci.uvTopRight, fx),
                                                  Vector2.Lerp(ci.uvBottomLeft, ci.uvBottomRight, fx), fy);
                        int ax = Mathf.Clamp((int)(uv.x * atlas.width), 0, atlas.width - 1);
                        int ay = Mathf.Clamp((int)(uv.y * atlas.height), 0, atlas.height - 1);
                        alpha[row + gx] = atlas.pixels[ay * atlas.width + ax].a;
                    }
                }
                glyph.width = w;
                glyph.height = h;
                glyph.alpha = alpha;
                glyph.sxA = 0f;
                glyph.sxB = w;
                glyph.syA = h;   // top edge (rows count up from the bottom)
                glyph.syB = 0f;
                if (!glyph.HasInk())
                {
                    glyph = null;
                    return GlyphResult.NotInCopy;
                }
            }
            GlyphCache.Add(fontId, sizePx, (int)fontStyle, c, glyph);
            return GlyphResult.Ok;
        }

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

            int missingChars = 0;
            bool fontLacksChar = false;   // a missing character the font has no glyph for at all
            bool rebuilt = false;
            int staleGlyphs = 0;
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
                        GlyphResult result = GetGlyph(unityFont, fontSizePx, fontStyle, s[c], atlas, out CachedGlyph cg);
                        if (result != GlyphResult.Ok)
                        {
                            // This used to be skipped silently, which is how labels lost letters.
                            if (result == GlyphResult.NotInAtlas)
                            {
                                missingChars++;
                                if (!unityFont.HasCharacter(s[c])) fontLacksChar = true;
                            }
                            else if (result == GlyphResult.Rebuilt) rebuilt = true;
                            else staleGlyphs++;
                            continue;
                        }
                        if (cg.visible)
                        {
                            glyphs.Add(new Glyph
                            {
                                left = penX + cg.x0,
                                top = baseline - cg.y1,
                                right = penX + cg.x1,
                                bottom = baseline - cg.y0,
                                source = cg
                            });
                        }
                        penX += cg.advance;
                    }
                }
                y += lineStep;
            }

            // Glyphs read fine this time are already cached, so a retry only waits on the rest.
            if (missingChars > 0 || rebuilt || staleGlyphs > 0)
            {
                string why = rebuilt ? "atlas rebuilt during the bake"
                    : missingChars > 0 ? $"{missingChars} character(s) not in the atlas"
                    : $"{staleGlyphs} glyph(s) blank in the atlas copy";
                return RetryOrGiveUp(lines, font, color, scale, world, unityFont, why, allText,
                    permanent: !rebuilt && staleGlyphs == 0 && fontLacksChar);
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

            for (int g = 0; g < glyphs.Count; g++)
            {
                Glyph gl = glyphs[g];
                float gw = gl.right - gl.left, gh = gl.bottom - gl.top;
                if (gw <= 0f || gh <= 0f) continue;
                CachedGlyph src = gl.source;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(gl.left - ox));
                int x1 = Mathf.Min(W, Mathf.CeilToInt(gl.right - ox));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(gl.top - oy));
                int y1 = Mathf.Min(H, Mathf.CeilToInt(gl.bottom - oy));
                for (int yy = y0; yy < y1; yy++)
                {
                    // This label pixel's span across the glyph, as fractions of its height.
                    float fy0 = Mathf.Clamp01((yy + oy - gl.top) / gh);
                    float fy1 = Mathf.Clamp01((yy + 1f + oy - gl.top) / gh);
                    if (fy1 <= fy0) continue;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        float fx0 = Mathf.Clamp01((xx + ox - gl.left) / gw);
                        float fx1 = Mathf.Clamp01((xx + 1f + ox - gl.left) / gw);
                        if (fx1 <= fx0) continue;
                        // Scale by how much of this pixel the glyph actually covers at its edges.
                        float a = src.Coverage(fx0, fx1, fy0, fy1)
                            * ((fx1 - fx0) * gw) * ((fy1 - fy0) * gh) * textColor.a;
                        if (a <= 0f) continue;
                        if (a > 1f) a = 1f;
                        ref Color dst = ref px[yy * W + xx];
                        float outA = a + dst.a * (1f - a);
                        float r = (textColor.r * a + dst.r * dst.a * (1f - a)) / outA;
                        float gg = (textColor.g * a + dst.g * dst.a * (1f - a)) / outA;
                        float bb = (textColor.b * a + dst.b * dst.a * (1f - a)) / outA;
                        dst = new Color(r, gg, bb, outA);
                    }
                }
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
                    $"atlas={(atlas == null ? "not needed" : $"{atlas.width}x{atlas.height} readable={atlas.readable}")} " +
                    $"glyphs={glyphs.Count}/{allText.Length} cached={GlyphCache.Count} " +
                    $"tex={W}x{H} bounds={bounds} bg={(Color)bg}");
            }
            return true;
        }

        // A bake that would be missing letters: re-request the glyphs and retry next frame, or
        // after MaxStaleRetries mark the label Unbakeable so it is drawn the GUI way. Unless the
        // font itself lacks a character (permanent), the bake is tried again after RetryAfterFrames.
        private bool RetryOrGiveUp(string[] lines, GameFont font, Color color, float scale, bool world,
            Font unityFont, string why, string allText, bool permanent)
        {
            if (staleRetries < MaxStaleRetries)
            {
                staleRetries++;
                rerequest = true;
                InvalidateAtlas(unityFont);
                if (Prefs.DevMode && totalBuilds < 10)
                {
                    Log.Message($"(Signs) label bake deferred: {why} (retry {staleRetries})");
                }
                return false;
            }
            bool wasRetry = retrying;
            DestroyTexture();
            bounds = new Rect(0f, 0f, 0f, 0f);
            SetBuiltKeys(lines, font, color, scale, world);
            unbakeable = true;
            if (!permanent) retryAtFrame = Time.frameCount + RetryAfterFrames;
            if (Prefs.DevMode && !wasRetry)   // once per label, not every retry
            {
                Log.Message($"(Signs) label bake gave up: {why} for \"{allText}\"; drawing it as a GUI label" +
                    (permanent ? " (the font has no glyph for a character)" : $", retrying every {RetryAfterFrames} frames"));
            }
            return false;
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
            unbakeable = false;
            retrying = false;
            retryAtFrame = -1;
        }
    }
}
