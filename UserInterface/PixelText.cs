using System;
using System.Collections.Generic;
using System.Linq;
using ArchiGungeon.DebugTools;
using ArchiGungeon.Data;
using UnityEngine;
using Color = UnityEngine.Color;

namespace ArchiGungeon.UserInterface
{
    /// <summary>
    /// Draws text in the game's own pixel font for the IMGUI menu. IMGUI can't use the game's fonts
    /// directly (they're dfGUI bitmap fonts), so this reads each glyph's place in the font atlas and draws it
    /// as a textured quad. Understands the &lt;color=#RRGGBB&gt; and &lt;b&gt; tags the menu uses.
    /// If no font is found, Available stays false and the menu falls back to Unity's default font.
    /// </summary>
    public static class PixelText
    {
        public static bool Available { get; private set; }
        public static string FontName { get; private set; } = "";

        private static dfFont font;
        private static Texture2D texture;
        private static Rect spriteRegion;          // the font's region in the atlas, in UV space
        private static float lineHeight;           // font units
        private static readonly Dictionary<char, dfFont.GlyphDefinition> glyphs = new Dictionary<char, dfFont.GlyphDefinition>();
        private static float nextAttempt = 0f;

        /// <summary>Finds the game's UI font. Retries every few seconds until the game's UI exists.</summary>
        public static void TryInit(string preferredName)
        {
            if (Available || Time.realtimeSinceStartup < nextAttempt)
            {
                return;
            }
            nextAttempt = Time.realtimeSinceStartup + 3f;

            try
            {
                dfFont[] fonts = Resources.FindObjectsOfTypeAll<dfFont>().Where(f => f != null && f.IsValid && f.Atlas != null).ToArray();
                if (fonts.Length == 0)
                {
                    return;
                }

                ArchDebugPrint.DebugLog(DebugCategory.UserInterface, "Game fonts: " + string.Join(", ", fonts.Select(f => $"{f.name} ({f.LineHeight})").ToArray()));

                dfFont chosen = null;
                if (!string.IsNullOrEmpty(preferredName))
                {
                    chosen = fonts.FirstOrDefault(f => string.Equals(f.name, preferredName, StringComparison.OrdinalIgnoreCase));
                }
                if (chosen == null)
                {
                    dfGUIManager manager = UnityEngine.Object.FindObjectOfType<dfGUIManager>();
                    chosen = manager != null ? manager.DefaultFont as dfFont : null;
                }
                if (chosen == null)
                {
                    chosen = fonts.OrderByDescending(f => f.Glyphs != null ? f.Glyphs.Count : 0).First();
                }

                Load(chosen);
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Pixel font setup failed: {e.Message}");
            }
        }

        private static void Load(dfFont chosen)
        {
            dfAtlas.ItemInfo sprite = chosen.Atlas[chosen.Sprite];
            Texture2D atlasTexture = chosen.Atlas.Texture;
            if (sprite == null || atlasTexture == null || chosen.Glyphs == null)
            {
                return;
            }

            glyphs.Clear();
            foreach (dfFont.GlyphDefinition glyph in chosen.Glyphs)
            {
                if (glyph != null && glyph.id >= 0 && glyph.id < char.MaxValue)
                {
                    glyphs[(char)glyph.id] = glyph;
                }
            }

            font = chosen;
            texture = atlasTexture;
            spriteRegion = sprite.region;
            lineHeight = Mathf.Max(1, chosen.LineHeight);
            FontName = chosen.name;
            Available = glyphs.Count > 0;
            ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Menu font: {FontName}, {glyphs.Count} glyphs, line height {lineHeight}");
        }

        /// <summary>Scale that makes a line about this many pixels tall.</summary>
        public static float ScaleFor(float linePixels)
        {
            return Available ? linePixels / lineHeight : 1f;
        }

        public static float LineHeight(float scale)
        {
            return lineHeight * scale;
        }

        #region Layout

        private struct Run
        {
            public string Text;
            public Color Color;
            public bool Bold;
        }

        private struct Line
        {
            public List<Run> Runs;
            public float Width;
        }

        // Splits on tags into coloured runs. Unknown tags are kept as text.
        private static List<Run> Parse(string text, Color baseColor)
        {
            List<Run> runs = new List<Run>();
            Stack<Color> colors = new Stack<Color>();
            Color current = baseColor;
            bool bold = false;
            int i = 0;
            System.Text.StringBuilder buffer = new System.Text.StringBuilder();

            Action flush = () =>
            {
                if (buffer.Length > 0)
                {
                    runs.Add(new Run { Text = buffer.ToString(), Color = current, Bold = bold });
                    buffer.Length = 0;
                }
            };

            while (i < text.Length)
            {
                if (text[i] == '<')
                {
                    int close = text.IndexOf('>', i);
                    if (close > i)
                    {
                        string tag = text.Substring(i + 1, close - i - 1);
                        Color parsed;
                        if (tag.StartsWith("color=") && ColorUtility.TryParseHtmlString(tag.Substring(6), out parsed))
                        {
                            flush(); colors.Push(current); current = parsed; i = close + 1; continue;
                        }
                        if (tag == "/color")
                        {
                            flush(); current = colors.Count > 0 ? colors.Pop() : baseColor; i = close + 1; continue;
                        }
                        if (tag == "b" || tag == "/b")
                        {
                            flush(); bold = tag == "b"; i = close + 1; continue;
                        }
                    }
                }
                buffer.Append(text[i]);
                i++;
            }
            flush();
            return runs;
        }

        private static float Advance(char c, float scale)
        {
            dfFont.GlyphDefinition glyph;
            if (glyphs.TryGetValue(c, out glyph) || glyphs.TryGetValue(char.ToUpperInvariant(c), out glyph))
            {
                return glyph.xadvance * scale;
            }
            return c == ' ' ? lineHeight * 0.4f * scale : 0f;
        }

        private static float WordWidth(string word, float scale, bool bold)
        {
            float width = 0f;
            foreach (char c in word)
            {
                width += Advance(c, scale);
            }
            return width + (bold ? scale : 0f);
        }

        // Word-wraps runs into lines no wider than maxWidth (<= 0 means no wrapping)
        private static List<Line> Wrap(List<Run> runs, float scale, float maxWidth)
        {
            List<Line> lines = new List<Line>();
            Line line = new Line { Runs = new List<Run>(), Width = 0f };

            foreach (Run run in runs)
            {
                string[] parts = run.Text.Split('\n');
                for (int p = 0; p < parts.Length; p++)
                {
                    if (p > 0)
                    {
                        lines.Add(line);
                        line = new Line { Runs = new List<Run>(), Width = 0f };
                    }

                    // Keep spaces attached to the word before them, so wrapping drops them at line ends
                    List<string> words = new List<string>();
                    string part = parts[p];
                    int start = 0;
                    for (int k = 0; k < part.Length; k++)
                    {
                        if (part[k] == ' ')
                        {
                            words.Add(part.Substring(start, k - start + 1));
                            start = k + 1;
                        }
                    }
                    if (start < part.Length)
                    {
                        words.Add(part.Substring(start));
                    }

                    foreach (string word in words)
                    {
                        float w = WordWidth(word, scale, run.Bold);
                        float wTrimmed = WordWidth(word.TrimEnd(' '), scale, run.Bold);
                        if (maxWidth > 0f && line.Width > 0f && line.Width + wTrimmed > maxWidth)
                        {
                            lines.Add(line);
                            line = new Line { Runs = new List<Run>(), Width = 0f };
                        }
                        line.Runs.Add(new Run { Text = word, Color = run.Color, Bold = run.Bold });
                        line.Width += w;
                    }
                }
            }
            lines.Add(line);
            return lines;
        }

        #endregion

        public static Vector2 Measure(string text, float scale, float maxWidth)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new Vector2(0f, LineHeight(scale));
            }
            List<Line> lines = Wrap(Parse(text, Color.white), scale, maxWidth);
            float width = lines.Max(l => l.Width);
            return new Vector2(width, lines.Count * LineHeight(scale));
        }

        public static void Draw(Rect rect, string text, Color baseColor, float scale, TextAnchor alignment, bool wrap)
        {
            if (!Available || string.IsNullOrEmpty(text) || Event.current.type != EventType.Repaint)
            {
                return;
            }

            List<Line> lines = Wrap(Parse(text, baseColor), scale, wrap ? rect.width : 0f);
            float totalHeight = lines.Count * LineHeight(scale);

            float y = rect.y;
            if (alignment == TextAnchor.MiddleLeft || alignment == TextAnchor.MiddleCenter || alignment == TextAnchor.MiddleRight)
            {
                y = rect.y + (rect.height - totalHeight) / 2f;
            }
            else if (alignment == TextAnchor.LowerLeft || alignment == TextAnchor.LowerCenter || alignment == TextAnchor.LowerRight)
            {
                y = rect.yMax - totalHeight;
            }

            Color previous = GUI.color;
            float texW = texture.width;
            float texH = texture.height;

            foreach (Line line in lines)
            {
                float x = rect.x;
                if (alignment == TextAnchor.UpperCenter || alignment == TextAnchor.MiddleCenter || alignment == TextAnchor.LowerCenter)
                {
                    x = rect.x + (rect.width - line.Width) / 2f;
                }
                else if (alignment == TextAnchor.UpperRight || alignment == TextAnchor.MiddleRight || alignment == TextAnchor.LowerRight)
                {
                    x = rect.xMax - line.Width;
                }

                foreach (Run run in line.Runs)
                {
                    GUI.color = new Color(run.Color.r, run.Color.g, run.Color.b, run.Color.a * previous.a);
                    foreach (char c in run.Text)
                    {
                        dfFont.GlyphDefinition glyph;
                        if (!glyphs.TryGetValue(c, out glyph) && !glyphs.TryGetValue(char.ToUpperInvariant(c), out glyph))
                        {
                            x += Advance(c, scale);
                            continue;
                        }

                        if (glyph.width > 0 && glyph.height > 0)
                        {
                            // Glyph coordinates are pixels from the top-left of the font's sprite in the atlas
                            float u = spriteRegion.x + glyph.x / texW;
                            float v = spriteRegion.yMax - (glyph.y + glyph.height) / texH;
                            Rect uv = new Rect(u, v, glyph.width / texW, glyph.height / texH);
                            Rect dest = new Rect(Mathf.Round(x + glyph.xoffset * scale), Mathf.Round(y + glyph.yoffset * scale),
                                glyph.width * scale, glyph.height * scale);
                            GUI.DrawTextureWithTexCoords(dest, texture, uv);
                            if (run.Bold)
                            {
                                dest.x += Mathf.Max(1f, scale);
                                GUI.DrawTextureWithTexCoords(dest, texture, uv);
                            }
                        }
                        x += glyph.xadvance * scale;
                    }
                    if (run.Bold)
                    {
                        x += scale;
                    }
                }
                y += LineHeight(scale);
            }

            GUI.color = previous;
        }
    }
}
