using System;
using System.IO;
using System.Linq;
using System.Text;
using ArchiGungeon.DebugTools;
using ArchiGungeon.Data;
using UnityEngine;

namespace ArchiGungeon.UserInterface
{
    /// <summary>
    /// One-off discovery for building the menu out of the game's own UI: writes every dfGUI atlas sprite and
    /// font, and how the game's menus are put together, to BepInEx/config/ArchipelaGunUIDump_*.txt.
    /// Dumps the HUD/pause menu once the game UI exists, and the Ammonomicon once it has been opened.
    /// </summary>
    public static class UIDump
    {
        private static bool dumpedAssets = false;
        private static bool dumpedHud = false;
        private static bool dumpedAmmonomicon = false;
        private static bool dumpedImages = false;
        private static float nextCheck = 0f;

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < nextCheck || (dumpedAssets && dumpedHud && dumpedAmmonomicon && dumpedImages))
            {
                return;
            }
            nextCheck = Time.realtimeSinceStartup + 3f;

            try
            {
                if (!dumpedHud && GameUIRoot.HasInstance)
                {
                    dumpedHud = true;
                    Write("Hud", DumpControls(GameUIRoot.Instance.gameObject));
                }

                if (!dumpedAssets && dumpedHud)
                {
                    dumpedAssets = true;
                    Write("Assets", DumpAssets());
                }

                // The Ammonomicon's managers exist (as loaded prefabs) once the game has loaded them,
                // whether or not the book has been opened
                if (!dumpedAmmonomicon && dumpedAssets)
                {
                    StringBuilder book = new StringBuilder();
                    foreach (dfGUIManager manager in Resources.FindObjectsOfTypeAll<dfGUIManager>())
                    {
                        if (manager.name.Contains("Ammonomicon") || manager.name.Contains("Page"))
                        {
                            book.Append(DumpControls(manager.gameObject));
                        }
                    }
                    if (book.Length > 0)
                    {
                        dumpedAmmonomicon = true;
                        Write("Ammonomicon", book.ToString());
                    }
                }

                if (!dumpedImages && dumpedAmmonomicon)
                {
                    dumpedImages = true;
                    DumpImages();
                }
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"UI dump failed: {e}");
            }
        }

        private static void Write(string part, string text)
        {
            string path = Path.Combine(BepInEx.Paths.ConfigPath, $"ArchipelaGunUIDump_{part}.txt");
            File.WriteAllText(path, text);
            ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Wrote {path}");
        }

        // Saves each UI atlas (and the textures the Ammonomicon draws directly) as PNGs, plus every sprite's
        // pixel rectangle, so the menu can be designed from what the pieces actually look like
        private static void DumpImages()
        {
            string folder = Path.Combine(BepInEx.Paths.ConfigPath, "ArchipelaGunUIDump_Images");
            Directory.CreateDirectory(folder);
            StringBuilder regions = new StringBuilder();

            foreach (dfAtlas atlas in Resources.FindObjectsOfTypeAll<dfAtlas>())
            {
                if (atlas.Texture == null)
                {
                    continue;
                }
                SavePng(atlas.Texture, Path.Combine(folder, Safe(atlas.name) + ".png"));
                int w = atlas.Texture.width, h = atlas.Texture.height;
                regions.AppendLine($"--- {atlas.name} {w}x{h}");
                foreach (dfAtlas.ItemInfo item in atlas.Items.OrderBy(i => i.name))
                {
                    Rect r = item.region;
                    // x, y from the top-left in pixels, then width and height
                    regions.AppendLine($"  {item.name} {Mathf.RoundToInt(r.x * w)} {Mathf.RoundToInt((1f - r.yMax) * h)} {Mathf.RoundToInt(r.width * w)} {Mathf.RoundToInt(r.height * h)}");
                }
            }

            foreach (dfTextureSprite sprite in Resources.FindObjectsOfTypeAll<dfTextureSprite>())
            {
                Texture texture = sprite.Texture;
                if (texture != null && (sprite.name.Contains("Page") || sprite.name.Contains("Book") || sprite.name.Contains("BG")
                    || sprite.transform.root.name.Contains("Ammonomicon")))
                {
                    SavePng(texture, Path.Combine(folder, "tex_" + Safe(sprite.name) + "_" + Safe(texture.name) + ".png"));
                }
            }

            File.WriteAllText(Path.Combine(folder, "regions.txt"), regions.ToString());
            ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Wrote UI images to {folder}");
        }

        // Game textures usually aren't readable, so copy through a RenderTexture first
        private static void SavePng(Texture texture, string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    return;
                }
                RenderTexture rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                RenderTexture previous = RenderTexture.active;
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                Texture2D copy = new Texture2D(texture.width, texture.height, TextureFormat.ARGB32, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                copy.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(copy));
                UnityEngine.Object.Destroy(copy);
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Couldn't save {path}: {e.Message}");
            }
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.Replace(' ', '_');
        }

        private static string DumpAssets()
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine("=== GUI managers");
            foreach (dfGUIManager manager in Resources.FindObjectsOfTypeAll<dfGUIManager>())
            {
                sb.AppendLine($"{manager.name}: FixedHeight={manager.FixedHeight} UIScale={manager.UIScale} " +
                    $"DefaultAtlas={(manager.DefaultAtlas != null ? manager.DefaultAtlas.name : "-")} " +
                    $"DefaultFont={(manager.DefaultFont != null ? manager.DefaultFont.name : "-")}");
            }

            sb.AppendLine();
            sb.AppendLine("=== Fonts");
            foreach (dfFontBase font in Resources.FindObjectsOfTypeAll<dfFontBase>())
            {
                dfFont bitmap = font as dfFont;
                sb.AppendLine($"{font.name} [{font.GetType().Name}] size={font.FontSize} line={font.LineHeight}" +
                    (bitmap != null ? $" atlas={(bitmap.Atlas != null ? bitmap.Atlas.name : "-")} sprite={bitmap.Sprite} glyphs={(bitmap.Glyphs != null ? bitmap.Glyphs.Count : 0)}" : ""));
            }

            sb.AppendLine();
            sb.AppendLine("=== Atlases and sprites (name size border)");
            foreach (dfAtlas atlas in Resources.FindObjectsOfTypeAll<dfAtlas>())
            {
                Texture2D texture = atlas.Texture;
                sb.AppendLine($"--- {atlas.name} texture={(texture != null ? $"{texture.width}x{texture.height}" : "-")} items={atlas.Count}");
                foreach (dfAtlas.ItemInfo item in atlas.Items.OrderBy(i => i.name))
                {
                    RectOffset b = item.border;
                    sb.AppendLine($"  {item.name} {item.sizeInPixels.x}x{item.sizeInPixels.y}" +
                        (b != null && (b.left + b.right + b.top + b.bottom) > 0 ? $" border={b.left},{b.right},{b.top},{b.bottom}" : ""));
                }
            }
            return sb.ToString();
        }

        private static string DumpControls(GameObject root)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"=== {root.name}");
            foreach (dfControl control in root.GetComponentsInChildren<dfControl>(true))
            {
                int depth = 0;
                for (Transform t = control.transform; t != null && t != root.transform; t = t.parent)
                {
                    depth++;
                }
                sb.Append(new string(' ', depth * 2));
                sb.Append($"{control.name} [{control.GetType().Name}] size={control.Size.x}x{control.Size.y} pos={control.RelativePosition.x},{control.RelativePosition.y}");
                sb.Append($" visible={control.IsVisible} color={Hex(control.Color)}");

                dfSprite sprite = control as dfSprite;
                if (sprite != null)
                {
                    sb.Append($" sprite={sprite.SpriteName} atlas={(sprite.Atlas != null ? sprite.Atlas.name : "-")}");
                }
                dfLabel label = control as dfLabel;
                if (label != null)
                {
                    sb.Append($" font={(label.Font != null ? label.Font.name : "-")} scale={label.TextScale} bg={label.BackgroundSprite}" +
                        $" outline={label.OutlineSize} text=\"{Short(label.Text)}\"");
                }
                dfButton button = control as dfButton;
                if (button != null)
                {
                    sb.Append($" font={(button.Font != null ? button.Font.name : "-")} scale={button.TextScale} bg={button.BackgroundSprite}" +
                        $" hover={button.HoverSprite} pressed={button.PressedSprite} focus={button.FocusSprite}" +
                        $" text=\"{Short(button.Text)}\" textColor={Hex(button.TextColor)} hoverText={Hex(button.HoverTextColor)}");
                }
                dfTextbox textbox = control as dfTextbox;
                if (textbox != null)
                {
                    sb.Append($" font={(textbox.Font != null ? textbox.Font.name : "-")} bg={textbox.BackgroundSprite}");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string Hex(Color32 c)
        {
            return $"#{c.r:X2}{c.g:X2}{c.b:X2}{c.a:X2}";
        }

        private static string Short(string text)
        {
            if (text == null) return "";
            text = text.Replace("\n", "\\n");
            return text.Length > 40 ? text.Substring(0, 40) + "..." : text;
        }
    }
}
