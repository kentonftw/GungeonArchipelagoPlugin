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
        private static float nextCheck = 0f;

        public static void Tick()
        {
            if (Time.realtimeSinceStartup < nextCheck || (dumpedAssets && dumpedHud && dumpedAmmonomicon))
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

                if (!dumpedAmmonomicon)
                {
                    AmmonomiconController ammonomicon = UnityEngine.Object.FindObjectOfType<AmmonomiconController>();
                    if (ammonomicon != null && ammonomicon.GetComponentsInChildren<dfControl>(true).Length > 0)
                    {
                        dumpedAmmonomicon = true;
                        Write("Ammonomicon", DumpControls(ammonomicon.gameObject));
                        // The book's pages live under their own managers too
                        StringBuilder pages = new StringBuilder();
                        foreach (AmmonomiconPageRenderer page in UnityEngine.Object.FindObjectsOfType<AmmonomiconPageRenderer>())
                        {
                            pages.Append(DumpControls(page.gameObject));
                        }
                        Write("AmmonomiconPages", pages.ToString());
                    }
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
