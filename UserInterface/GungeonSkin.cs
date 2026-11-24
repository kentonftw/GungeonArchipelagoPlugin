using System;
using UnityEngine;
using Color = UnityEngine.Color;

namespace ArchiGungeon.UserInterface
{
    /// <summary>
    /// Pixel-art textures for the menu, drawn in code in the colours of the game's Ammonomicon and death screen:
    /// a red book cover around parchment pages, dark plates for buttons, tan tags for values and fields.
    /// Each art pixel is drawn as a Unit x Unit block so it stays chunky; all are 9-sliced through GUIStyle.border.
    /// </summary>
    public static class GungeonSkin
    {
        public const int Unit = 3;

        // Palette (picked from the death screen)
        public static readonly Color Outline = Rgb(34, 24, 30);
        public static readonly Color Cover = Rgb(126, 30, 44);
        public static readonly Color CoverLight = Rgb(160, 52, 62);
        public static readonly Color CoverDark = Rgb(84, 18, 30);
        public static readonly Color Page = Rgb(206, 200, 186);
        public static readonly Color PageShade = Rgb(186, 179, 162);
        public static readonly Color PageLine = Rgb(120, 104, 98);
        public static readonly Color Plate = Rgb(46, 44, 68);
        public static readonly Color PlateLight = Rgb(78, 76, 108);
        public static readonly Color PlateHover = Rgb(64, 62, 94);
        public static readonly Color Tag = Rgb(236, 220, 178);
        public static readonly Color TagShade = Rgb(212, 192, 146);
        public static readonly Color TagEdge = Rgb(112, 82, 58);

        // Text colours
        public static readonly Color Ink = Rgb(40, 30, 40);
        public static readonly Color InkMuted = Rgb(102, 88, 86);
        public static readonly Color InkRed = Rgb(152, 30, 36);
        public static readonly Color InkGold = Rgb(172, 106, 0);
        public static readonly Color InkGreen = Rgb(36, 118, 50);
        public static readonly Color InkBlue = Rgb(36, 74, 160);
        public static readonly Color PlateText = Rgb(232, 224, 204);

        public static Color Rgb(int r, int g, int b)
        {
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }

        /// <summary>A size x size art-pixel tile, scaled up by Unit; the border is in art pixels.</summary>
        private static Texture2D Make(int size, Func<int, int, int, Color> pixel)
        {
            int full = size * Unit;
            Texture2D texture = new Texture2D(full, full, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.hideFlags = HideFlags.HideAndDontSave;

            for (int ay = 0; ay < size; ay++)
            {
                for (int ax = 0; ax < size; ax++)
                {
                    // Distance (in art pixels) from the nearest edge, and art y counted from the top
                    int top = size - 1 - ay;
                    int edge = Math.Min(Math.Min(ax, size - 1 - ax), Math.Min(ay, top));
                    Color c = pixel(ax, top, edge);
                    for (int py = 0; py < Unit; py++)
                    {
                        for (int px = 0; px < Unit; px++)
                        {
                            texture.SetPixel(ax * Unit + px, ay * Unit + py, c);
                        }
                    }
                }
            }
            texture.Apply();
            return texture;
        }

        private static RectOffset Border(int artPixels)
        {
            int p = artPixels * Unit;
            return new RectOffset(p, p, p, p);
        }

        // Book: outline, red cover with a highlight, a dark seam, then parchment
        public static Texture2D BookTexture()
        {
            return Make(16, (x, y, e) =>
            {
                if (e == 0) return Outline;
                if (e == 1) return CoverLight;
                if (e <= 3) return Cover;
                if (e == 4) return CoverDark;
                if (e == 5) return Outline;
                return Page;
            });
        }
        public static RectOffset BookBorder { get { return Border(7); } }

        // Inset panel on the page: a thin brown line around slightly darker parchment
        public static Texture2D PanelTexture()
        {
            return Make(8, (x, y, e) => e == 0 ? PageLine : PageShade);
        }
        public static RectOffset PanelBorder { get { return Border(2); } }

        // Dark plate, like the "GUNGEONEER" / "AREA" labels: outline, light top edge, dark fill
        public static Texture2D PlateTexture(Color fill)
        {
            return Make(8, (x, y, e) =>
            {
                if (e == 0) return Outline;
                if (y == 1) return PlateLight;
                return fill;
            });
        }
        public static RectOffset PlateBorder { get { return Border(2); } }

        // Tan tag, like the death screen's value boxes: brown edge, cream fill, shaded bottom
        public static Texture2D TagTexture(Color fill)
        {
            return Make(8, (x, y, e) =>
            {
                if (e == 0) return TagEdge;
                if (y == 6) return TagShade;
                return fill;
            });
        }
        public static RectOffset TagBorder { get { return Border(2); } }

        public static Texture2D Solid(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.filterMode = FilterMode.Point;
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }
    }
}
