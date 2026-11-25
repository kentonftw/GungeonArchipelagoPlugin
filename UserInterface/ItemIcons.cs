using System;
using System.Collections.Generic;
using ArchiGungeon.DebugTools;
using ArchiGungeon.Data;
using UnityEngine;
using Color = UnityEngine.Color;

namespace ArchiGungeon.UserInterface
{
    /// <summary>
    /// The game's own icons for items, as small readable textures the menu can draw. Uses the Ammonomicon's icon
    /// for an item where it has one, otherwise the item's sprite. Game textures aren't readable, so each icon is
    /// copied out through a RenderTexture once, a few per frame, and cached.
    /// </summary>
    public static class ItemIcons
    {
        private static readonly Dictionary<int, Texture2D> cache = new Dictionary<int, Texture2D>();
        private static readonly HashSet<int> requested = new HashSet<int>();
        private static readonly Queue<int> pending = new Queue<int>();
        private static bool triedAmmonomicon = false;
        private static tk2dSpriteCollectionData ammonomiconIcons;

        /// <summary>The icon for a pickup ID, or null while it's still being made (or if it can't be).</summary>
        public static Texture2D Get(int pickupId)
        {
            if (pickupId < 0)
            {
                return null;
            }
            Texture2D icon;
            if (cache.TryGetValue(pickupId, out icon))
            {
                return icon;
            }
            if (requested.Add(pickupId))
            {
                pending.Enqueue(pickupId);
            }
            return null;
        }

        /// <summary>Makes a few queued icons. Call from Update, outside of GUI drawing.</summary>
        public static void Tick()
        {
            for (int i = 0; i < 6 && pending.Count > 0; i++)
            {
                int id = pending.Dequeue();
                Texture2D icon = null;
                try
                {
                    icon = Make(id);
                }
                catch (Exception e)
                {
                    ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Icon for item {id} failed: {e.Message}");
                }
                cache[id] = icon;
            }
        }

        /// <summary>Draws an icon centred in box at a whole-number scale (as big as fits, up to maxScale).</summary>
        public static void Draw(Texture2D icon, Rect box, int maxScale, bool bottom = false)
        {
            if (icon == null || Event.current.type != EventType.Repaint)
            {
                return;
            }
            int scale = Mathf.Max(1, Mathf.Min(maxScale, Mathf.Min((int)(box.width / icon.width), (int)(box.height / icon.height))));
            float w = icon.width * scale;
            float h = icon.height * scale;
            if (w > box.width || h > box.height)
            {
                // Too big even at 1x: shrink to fit
                float fit = Mathf.Min(box.width / icon.width, box.height / icon.height);
                w = icon.width * fit;
                h = icon.height * fit;
            }
            float y = bottom ? box.yMax - h : box.y + (box.height - h) / 2f;
            GUI.DrawTexture(new Rect(Mathf.Round(box.x + (box.width - w) / 2f), Mathf.Round(y), w, h), icon);
        }

        private static tk2dSpriteCollectionData AmmonomiconIcons()
        {
            if (ammonomiconIcons == null && !triedAmmonomicon)
            {
                triedAmmonomicon = true;
                try
                {
                    AmmonomiconController controller = AmmonomiconController.HasInstance ? AmmonomiconController.Instance : AmmonomiconController.ForceInstance;
                    ammonomiconIcons = controller != null ? controller.EncounterIconCollection : null;
                }
                catch (Exception e)
                {
                    ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Couldn't get the Ammonomicon icons: {e.Message}");
                }
            }
            return ammonomiconIcons;
        }

        private static Texture2D Make(int pickupId)
        {
            PickupObject pickup = PickupObjectDatabase.GetById(pickupId);
            if (pickup == null)
            {
                return null;
            }

            tk2dSpriteDefinition definition = null;
            string iconName = pickup.encounterTrackable != null && pickup.encounterTrackable.journalData != null ? pickup.encounterTrackable.journalData.AmmonomiconSprite : null;
            tk2dSpriteCollectionData icons = AmmonomiconIcons();
            if (icons != null && !string.IsNullOrEmpty(iconName))
            {
                int spriteId = icons.GetSpriteIdByName(iconName, -1);
                if (spriteId >= 0)
                {
                    definition = icons.spriteDefinitions[spriteId];
                }
            }
            if (definition == null)
            {
                tk2dBaseSprite sprite = pickup.GetComponent<tk2dBaseSprite>();
                if (sprite != null && sprite.Collection != null && sprite.spriteId >= 0 && sprite.spriteId < sprite.Collection.spriteDefinitions.Length)
                {
                    definition = sprite.Collection.spriteDefinitions[sprite.spriteId];
                }
            }
            return definition != null ? Extract(definition) : null;
        }

        // Copies one sprite out of its atlas. Uses the sprite's corner UVs (bottom-left, bottom-right, top-left), so
        // sprites the packer stored rotated come out the right way up.
        private static Texture2D Extract(tk2dSpriteDefinition definition)
        {
            Material material = definition.material ?? definition.materialInst;
            Texture texture = material != null ? material.mainTexture : null;
            if (texture == null || definition.uvs == null || definition.uvs.Length < 4)
            {
                return null;
            }

            Vector2 texSize = new Vector2(texture.width, texture.height);
            Vector2 uv0 = definition.uvs[0], uv1 = definition.uvs[1], uv2 = definition.uvs[2];
            Vector2 right = Vector2.Scale(uv1 - uv0, texSize);
            Vector2 up = Vector2.Scale(uv2 - uv0, texSize);
            int outW = Mathf.RoundToInt(right.magnitude);
            int outH = Mathf.RoundToInt(up.magnitude);
            if (outW <= 0 || outH <= 0 || outW > 256 || outH > 256)
            {
                return null;
            }

            // Bounding box of the sprite in the atlas, in pixels
            float minU = Mathf.Min(Mathf.Min(definition.uvs[0].x, definition.uvs[1].x), Mathf.Min(definition.uvs[2].x, definition.uvs[3].x));
            float maxU = Mathf.Max(Mathf.Max(definition.uvs[0].x, definition.uvs[1].x), Mathf.Max(definition.uvs[2].x, definition.uvs[3].x));
            float minV = Mathf.Min(Mathf.Min(definition.uvs[0].y, definition.uvs[1].y), Mathf.Min(definition.uvs[2].y, definition.uvs[3].y));
            float maxV = Mathf.Max(Mathf.Max(definition.uvs[0].y, definition.uvs[1].y), Mathf.Max(definition.uvs[2].y, definition.uvs[3].y));
            int px = Mathf.FloorToInt(minU * texSize.x + 0.01f);
            int py = Mathf.FloorToInt(minV * texSize.y + 0.01f);
            int pw = Mathf.Max(1, Mathf.RoundToInt((maxU - minU) * texSize.x));
            int ph = Mathf.Max(1, Mathf.RoundToInt((maxV - minV) * texSize.y));

            Color32[] region;
            RenderTexture previous = RenderTexture.active;
            RenderTexture rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                Texture2D read = new Texture2D(pw, ph, TextureFormat.ARGB32, false);
                read.ReadPixels(new Rect(px, py, pw, ph), 0, 0);
                read.Apply();
                region = read.GetPixels32();
                UnityEngine.Object.Destroy(read);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }

            // Sample each output pixel's centre through the sprite's own UV axes
            Color32[] output = new Color32[outW * outH];
            for (int oy = 0; oy < outH; oy++)
            {
                for (int ox = 0; ox < outW; ox++)
                {
                    float fx = (ox + 0.5f) / outW;
                    float fy = (oy + 0.5f) / outH;
                    Vector2 uv = uv0 + (uv1 - uv0) * fx + (uv2 - uv0) * fy;
                    int sx = Mathf.Clamp(Mathf.FloorToInt(uv.x * texSize.x) - px, 0, pw - 1);
                    int sy = Mathf.Clamp(Mathf.FloorToInt(uv.y * texSize.y) - py, 0, ph - 1);
                    output[oy * outW + ox] = region[sy * pw + sx];
                }
            }

            Texture2D icon = new Texture2D(outW, outH, TextureFormat.ARGB32, false);
            icon.filterMode = FilterMode.Point;
            icon.wrapMode = TextureWrapMode.Clamp;
            icon.hideFlags = HideFlags.HideAndDontSave;
            icon.SetPixels32(output);
            icon.Apply();
            return icon;
        }
    }
}
