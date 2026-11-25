using System;
using System.Collections.Generic;
using System.Linq;
using ArchiGungeon.Data;
using ArchiGungeon.DebugTools;
using UnityEngine;
using Color = UnityEngine.Color;

namespace ArchiGungeon.UserInterface
{
    /// <summary>
    /// The game's own UI art for the Ammonomicon-style menu: the open book texture, sprites from the game's UI
    /// atlases (drawn whole or 9-sliced) and the game's bitmap fonts. Everything is drawn at whole-number pixel
    /// scales so it stays as crisp as the game's menus. Ready stays false until the game has loaded them.
    /// </summary>
    public static class BookArt
    {
        public static bool Ready { get; private set; }

        public const string BookTextureName = "ammonomicon_intro_035";
        public const int BookWidth = 480;
        public const int BookHeight = 270;

        // Page areas inside the book texture (texture pixels, from the top-left)
        public static readonly RectInt LeftPage = new RectInt(72, 22, 138, 224);
        public static readonly RectInt RightPage = new RectInt(268, 22, 140, 224);

        public static Texture2D Book { get; private set; }
        public static dfAtlas GameUI { get; private set; }
        public static dfAtlas Ammonomicon { get; private set; }
        public static PixelFont Font { get; private set; }

        private static float nextAttempt = 0f;

        public static void TryInit()
        {
            if (Ready || Time.realtimeSinceStartup < nextAttempt)
            {
                return;
            }
            nextAttempt = Time.realtimeSinceStartup + 3f;

            try
            {
                if (Book == null)
                {
                    Book = Resources.FindObjectsOfTypeAll<Texture2D>().FirstOrDefault(t => t != null && t.name == BookTextureName);
                }
                dfAtlas[] atlases = Resources.FindObjectsOfTypeAll<dfAtlas>();
                GameUI = GameUI ?? atlases.FirstOrDefault(a => a != null && a.name == "GameUIAtlas");
                Ammonomicon = Ammonomicon ?? atlases.FirstOrDefault(a => a != null && a.name == "Ammonomicon Atlas");
                if (Font == null)
                {
                    dfFont daikon = Resources.FindObjectsOfTypeAll<dfFont>().FirstOrDefault(f => f != null && f.name == "OurFont_DaikonBitmap");
                    Font = daikon != null ? PixelFont.From(daikon) : null;
                }

                Ready = Book != null && GameUI != null && Ammonomicon != null && Font != null;
                ArchDebugPrint.DebugLog(DebugCategory.UserInterface,
                    $"Book art: book={Book != null} gameUI={GameUI != null} ammonomicon={Ammonomicon != null} font={Font != null}");
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Book art setup failed: {e.Message}");
            }
        }

        /// <summary>Largest whole-number scale at which the book fits the screen.</summary>
        public static int PixelScale
        {
            get { return Mathf.Max(1, Mathf.Min(Screen.height / BookHeight, Screen.width / BookWidth)); }
        }

        #region Sprites

        // Pixel rectangle (from the top-left) and UVs of an atlas sprite
        private static bool TryGet(dfAtlas atlas, string name, out dfAtlas.ItemInfo item)
        {
            item = atlas != null ? atlas[name] : null;
            return item != null;
        }

        public static Vector2 SpriteSize(dfAtlas atlas, string name)
        {
            dfAtlas.ItemInfo item;
            return TryGet(atlas, name, out item) ? item.sizeInPixels : Vector2.zero;
        }

        /// <summary>Draws a sprite stretched to dest.</summary>
        public static void Sprite(dfAtlas atlas, string name, Rect dest, Color tint)
        {
            dfAtlas.ItemInfo item;
            if (Event.current.type != EventType.Repaint || !TryGet(atlas, name, out item))
            {
                return;
            }
            Color previous = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(dest, atlas.Texture, item.region);
            GUI.color = previous;
        }

        /// <summary>Draws a sprite at its own size times scale, top-left at position.</summary>
        public static void Sprite(dfAtlas atlas, string name, Vector2 position, int scale)
        {
            Vector2 size = SpriteSize(atlas, name);
            Sprite(atlas, name, new Rect(position.x, position.y, size.x * scale, size.y * scale), Color.white);
        }

        /// <summary>9-slice: corners keep their size (times scale), edges and centre stretch.</summary>
        public static void Sliced(dfAtlas atlas, string name, Rect dest, int scale, Color tint)
        {
            dfAtlas.ItemInfo item;
            if (Event.current.type != EventType.Repaint || !TryGet(atlas, name, out item))
            {
                return;
            }

            RectOffset b = item.border ?? new RectOffset();
            Texture tex = atlas.Texture;
            float tw = tex.width, th = tex.height;
            Rect r = item.region;
            float sw = item.sizeInPixels.x, sh = item.sizeInPixels.y;

            // Source slice edges in UV (u left->right, v bottom->top) and destination edges in screen space
            float[] u = { r.x, r.x + b.left / tw, r.xMax - b.right / tw, r.xMax };
            float[] v = { r.yMax, r.yMax - b.top / th, r.y + b.bottom / th, r.y };
            float left = Mathf.Min(b.left * scale, dest.width / 2f), right = Mathf.Min(b.right * scale, dest.width / 2f);
            float top = Mathf.Min(b.top * scale, dest.height / 2f), bottom = Mathf.Min(b.bottom * scale, dest.height / 2f);
            float[] x = { dest.x, dest.x + left, dest.xMax - right, dest.xMax };
            float[] y = { dest.y, dest.y + top, dest.yMax - bottom, dest.yMax };

            Color previous = GUI.color;
            GUI.color = tint;
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    Rect d = new Rect(x[col], y[row], x[col + 1] - x[col], y[row + 1] - y[row]);
                    if (d.width <= 0f || d.height <= 0f) continue;
                    Rect uv = new Rect(u[col], v[row + 1], u[col + 1] - u[col], v[row] - v[row + 1]);
                    GUI.DrawTextureWithTexCoords(d, tex, uv);
                }
            }
            GUI.color = previous;
        }

        #endregion
    }

    /// <summary>
    /// A dfGUI bitmap font drawn through IMGUI at whole-number scales. Understands &lt;color=#RRGGBB&gt;, &lt;/color&gt;,
    /// &lt;b&gt; and &lt;/b&gt;; bold is drawn one font pixel wider.
    /// </summary>
    public class PixelFont
    {
        private Texture2D texture;
        private Rect region;
        private int lineHeight;
        private readonly Dictionary<char, dfFont.GlyphDefinition> glyphs = new Dictionary<char, dfFont.GlyphDefinition>();

        public static PixelFont From(dfFont font)
        {
            dfAtlas.ItemInfo sprite = font.Atlas != null ? font.Atlas[font.Sprite] : null;
            if (sprite == null || font.Atlas.Texture == null || font.Glyphs == null)
            {
                return null;
            }
            PixelFont result = new PixelFont { texture = font.Atlas.Texture, region = sprite.region, lineHeight = Mathf.Max(1, font.LineHeight) };
            foreach (dfFont.GlyphDefinition glyph in font.Glyphs)
            {
                if (glyph != null && glyph.id > 0 && glyph.id < char.MaxValue)
                {
                    result.glyphs[(char)glyph.id] = glyph;
                }
            }
            return result.glyphs.Count > 0 ? result : null;
        }

        public int Line(int scale)
        {
            return lineHeight * scale;
        }

        private struct Run { public string Text; public Color Color; public bool Bold; }
        private class TextLine { public List<Run> Runs = new List<Run>(); public int Width; }

        private bool TryGlyph(char c, out dfFont.GlyphDefinition glyph)
        {
            return glyphs.TryGetValue(c, out glyph) || glyphs.TryGetValue(char.ToUpperInvariant(c), out glyph);
        }

        private int Advance(char c, int scale)
        {
            dfFont.GlyphDefinition glyph;
            if (TryGlyph(c, out glyph))
            {
                return glyph.xadvance * scale;
            }
            return c == ' ' ? (lineHeight / 3) * scale : 0;
        }

        private static List<Run> Parse(string text, Color baseColor)
        {
            List<Run> runs = new List<Run>();
            Stack<Color> stack = new Stack<Color>();
            Color color = baseColor;
            bool bold = false;
            System.Text.StringBuilder buffer = new System.Text.StringBuilder();
            Action flush = () =>
            {
                if (buffer.Length > 0) { runs.Add(new Run { Text = buffer.ToString(), Color = color, Bold = bold }); buffer.Length = 0; }
            };

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '<')
                {
                    int close = text.IndexOf('>', i);
                    if (close > i)
                    {
                        string tag = text.Substring(i + 1, close - i - 1);
                        Color parsed;
                        if (tag.StartsWith("color=") && ColorUtility.TryParseHtmlString(tag.Substring(6), out parsed))
                        { flush(); stack.Push(color); color = parsed; i = close; continue; }
                        if (tag == "/color") { flush(); color = stack.Count > 0 ? stack.Pop() : baseColor; i = close; continue; }
                        if (tag == "b" || tag == "/b") { flush(); bold = tag == "b"; i = close; continue; }
                    }
                }
                buffer.Append(text[i]);
            }
            flush();
            return runs;
        }

        private List<TextLine> Wrap(List<Run> runs, int scale, float maxWidth)
        {
            List<TextLine> lines = new List<TextLine>();
            TextLine line = new TextLine();
            foreach (Run run in runs)
            {
                string[] parts = run.Text.Split('\n');
                for (int p = 0; p < parts.Length; p++)
                {
                    if (p > 0) { lines.Add(line); line = new TextLine(); }
                    string part = parts[p];
                    int start = 0;
                    for (int k = 0; k <= part.Length; k++)
                    {
                        if (k < part.Length && part[k] != ' ') continue;
                        string word = part.Substring(start, Math.Min(k + 1, part.Length) - start);
                        start = k + 1;
                        if (word.Length == 0) continue;
                        int w = 0, wTrim = 0;
                        foreach (char c in word) w += Advance(c, scale);
                        foreach (char c in word.TrimEnd(' ')) wTrim += Advance(c, scale);
                        if (run.Bold) { w += scale; wTrim += scale; }
                        if (maxWidth > 0f && line.Width > 0 && line.Width + wTrim > maxWidth)
                        {
                            lines.Add(line); line = new TextLine();
                        }
                        line.Runs.Add(new Run { Text = word, Color = run.Color, Bold = run.Bold });
                        line.Width += w;
                    }
                }
            }
            lines.Add(line);
            return lines;
        }

        public Vector2 Measure(string text, int scale, float maxWidth)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new Vector2(0f, Line(scale));
            }
            List<TextLine> lines = Wrap(Parse(text, Color.white), scale, maxWidth);
            return new Vector2(lines.Max(l => l.Width), lines.Count * Line(scale));
        }

        /// <summary>Draws text inside rect. align: 0 left, 1 centre, 2 right. Vertically centred if middle.</summary>
        public void Draw(Rect rect, string text, Color baseColor, int scale, int align, bool wrap, bool middle = false)
        {
            if (string.IsNullOrEmpty(text) || Event.current.type != EventType.Repaint)
            {
                return;
            }

            List<TextLine> lines = Wrap(Parse(text, baseColor), scale, wrap ? rect.width : 0f);
            float y = Mathf.Round(middle ? rect.y + (rect.height - lines.Count * Line(scale)) / 2f : rect.y);
            float tw = texture.width, th = texture.height;
            Color previous = GUI.color;

            foreach (TextLine line in lines)
            {
                float x = Mathf.Round(align == 1 ? rect.x + (rect.width - line.Width) / 2f : align == 2 ? rect.xMax - line.Width : rect.x);
                foreach (Run run in line.Runs)
                {
                    GUI.color = new Color(run.Color.r, run.Color.g, run.Color.b, run.Color.a * previous.a);
                    foreach (char c in run.Text)
                    {
                        dfFont.GlyphDefinition g;
                        if (!TryGlyph(c, out g)) { x += Advance(c, scale); continue; }
                        if (g.width > 0 && g.height > 0)
                        {
                            Rect uv = new Rect(region.x + g.x / tw, region.yMax - (g.y + g.height) / th, g.width / tw, g.height / th);
                            Rect dest = new Rect(x + g.xoffset * scale, y + g.yoffset * scale, g.width * scale, g.height * scale);
                            GUI.DrawTextureWithTexCoords(dest, texture, uv);
                            if (run.Bold)
                            {
                                dest.x += scale;
                                GUI.DrawTextureWithTexCoords(dest, texture, uv);
                            }
                        }
                        x += g.xadvance * scale;
                    }
                    if (run.Bold) x += scale;
                }
                y += Line(scale);
            }
            GUI.color = previous;
        }
    }
}
