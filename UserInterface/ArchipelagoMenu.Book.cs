using System.Collections.Generic;
using Archipelago.MultiClient.Net.Models;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.ItemArchipelago;
using UnityEngine;
using Color = UnityEngine.Color;

namespace ArchiGungeon.UserInterface
{
    /// <summary>
    /// The menu drawn as an Ammonomicon: the game's open book, its bookmarks as tabs, lists on the left page and
    /// details on the right, in the game's own pixel font and UI art (see BookArt). Uses the same data and
    /// actions as the plain menu, which is still used if the game's art can't be found.
    /// </summary>
    public partial class ArchipelagoMenu
    {
        // Ink colours, picked from the Ammonomicon's pages
        private static readonly Color BookInk = new Color32(52, 48, 66, 255);
        private static readonly Color BookMuted = new Color32(112, 104, 108, 255);
        private static readonly Color BookRed = new Color32(156, 34, 38, 255);
        private static readonly Color BookGold = new Color32(172, 100, 12, 255);
        private static readonly Color BookBlue = new Color32(42, 76, 150, 255);
        private static readonly Color BookGreen = new Color32(46, 112, 54, 255);
        private static readonly Color PlateInk = new Color32(228, 222, 206, 255);

        private static readonly string[] BookmarkSprites = { "bookmark_npc", "bookmark_equipment", "bookmark_items", "bookmark_enemies", "bookmark_death" };

        private int P;                 // book pixel scale
        private Vector2 bookOrigin;    // screen position of the book texture's top-left
        private float areaWidth;       // width of the page currently being drawn
        private Texture2D bookSolid;
        private GUIStyle invisibleField;
        private int selectedItem = -1;
        private Vector2 bookLeftScroll, bookRightScroll;

        private int TitleScale { get { return P; } }
        private int HeadScale { get { return Mathf.Max(1, P - 1); } }
        private int BodyScale { get { return Mathf.Max(1, P - 2); } }

        private Rect BookRect(float x, float y, float w, float h)
        {
            return new Rect(bookOrigin.x + x * P, bookOrigin.y + y * P, w * P, h * P);
        }

        private bool UseBook
        {
            get { return useBookMenu.Value && BookArt.Ready; }
        }

        private void DrawBook()
        {
            P = BookArt.PixelScale;
            bookOrigin = new Vector2(Mathf.Round((Screen.width - BookArt.BookWidth * P) / 2f), Mathf.Round((Screen.height - BookArt.BookHeight * P) / 2f));
            if (bookSolid == null)
            {
                bookSolid = GungeonSkinSolid(Color.white);
            }
            if (invisibleField == null)
            {
                invisibleField = new GUIStyle(GUI.skin.textField) { fontSize = 18 };
                invisibleField.normal.background = null;
                invisibleField.focused.background = null;
                invisibleField.hover.background = null;
                invisibleField.active.background = null;
                Color clear = new Color(0f, 0f, 0f, 0f);
                invisibleField.normal.textColor = clear;
                invisibleField.focused.textColor = clear;
                invisibleField.hover.textColor = clear;
            }
            Color previousCursor = GUI.skin.settings.cursorColor;
            Color previousSelection = GUI.skin.settings.selectionColor;
            GUI.skin.settings.cursorColor = new Color(0f, 0f, 0f, 0f);
            GUI.skin.settings.selectionColor = new Color(0.6f, 0.4f, 0.2f, 0.35f);

            // Dim the game, then the book
            Solid(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.65f));
            // Bookmarks first so the cover hides where they join the book
            DrawBookmarks();
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(BookRect(0, 0, BookArt.BookWidth, BookArt.BookHeight), BookArt.Book);
            }
            DrawBookmarkTag();

            // Left page: title, status, then the tab's main content
            RectInt left = BookArt.LeftPage;
            RectInt right = BookArt.RightPage;
            BeginPage(left);
            BookTitle();
            switch (currentTab)
            {
                case Tab.Connect: BookConnectLeft(); break;
                case Tab.Tracker: BookTrackerLeft(); break;
                case Tab.Items: BookItemsLeft(); break;
                case Tab.Hints: BookHintsLeft(); break;
                case Tab.Console: BookConsoleLeft(); break;
            }
            EndPage();

            BeginPage(new RectInt(right.x, right.y, right.width, right.height - 20));
            switch (currentTab)
            {
                case Tab.Connect: BookConnectRight(); break;
                case Tab.Tracker: BookTrackerRight(); break;
                case Tab.Items: BookItemsRight(); break;
                case Tab.Hints: BookHintsRight(); break;
                case Tab.Console: BookConsoleRight(); break;
            }
            EndPage();

            // Close, bottom of the right page
            BeginPage(new RectInt(right.x, right.yMax - 18, right.width, 18));
            GUILayout.BeginHorizontal();
            BText($"{menuKey.Value} or Esc to close", BookMuted, BodyScale, false);
            GUILayout.FlexibleSpace();
            if (BButton("Close", false, 0f))
            {
                ArchipelagoGUI.Instance?.OnClose();
            }
            GUILayout.EndHorizontal();
            EndPage();

            GUI.skin.settings.cursorColor = previousCursor;
            GUI.skin.settings.selectionColor = previousSelection;
        }

        private static Texture2D GungeonSkinSolid(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private void Solid(Rect rect, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, bookSolid);
            GUI.color = previous;
        }

        #region Book widgets

        private void BeginPage(RectInt page)
        {
            Rect r = BookRect(page.x, page.y, page.width, page.height);
            areaWidth = r.width;
            GUILayout.BeginArea(r);
        }

        private void EndPage()
        {
            GUILayout.EndArea();
        }

        private void BText(string text, Color color, int scale, bool wrap = true, int align = 0, float width = 0f)
        {
            float w = width > 0f ? width : areaWidth;
            Vector2 size = BookArt.Font.Measure(text ?? "", scale, wrap ? w : 0f);
            Rect r = wrap || width > 0f ? GUILayoutUtility.GetRect(w, size.y, GUILayout.Width(w)) : GUILayoutUtility.GetRect(size.x, size.y, GUILayout.ExpandWidth(false));
            BookArt.Font.Draw(r, text, color, scale, align, wrap);
        }

        private void BSpace(int bookPixels)
        {
            GUILayout.Space(bookPixels * P);
        }

        // Navy label plate, like the death screen's "GUNGEONEER" / "AREA"
        private void BPlate(string text)
        {
            int scale = BodyScale;
            Vector2 size = BookArt.Font.Measure(text, scale, 0f);
            Rect r = GUILayoutUtility.GetRect(size.x + 8 * P, BookArt.Font.Line(scale) + 4 * P, GUILayout.ExpandWidth(false));
            BookArt.Sliced(BookArt.Ammonomicon, "death_page_text_boxes_001", r, P, Color.white);
            BookArt.Font.Draw(r, text, PlateInk, scale, 1, false, true);
            BSpace(2);
        }

        private bool BButton(string text, bool primary, float width)
        {
            int scale = BodyScale;
            Vector2 size = BookArt.Font.Measure(text, scale, 0f);
            float w = width > 0f ? width : size.x + 12 * P;
            Rect r = GUILayoutUtility.GetRect(w, BookArt.Font.Line(scale) + 6 * P, GUILayout.Width(w));
            bool hover = r.Contains(Event.current.mousePosition) && GUI.enabled;
            bool pressed = hover && Input.GetMouseButton(0);

            if (primary)
            {
                BookArt.Sliced(BookArt.Ammonomicon, "death_page_failure_box_001", r, P, hover ? new Color(1.2f, 1.2f, 1.3f) : Color.white);
                BookArt.Font.Draw(r, text, hover ? Color.white : PlateInk, scale, 1, false, true);
            }
            else
            {
                string sprite = pressed ? "big_box_page_flat_001" : hover ? "big_box_page_raised_selected_001" : "big_box_page_raised_001";
                BookArt.Sliced(BookArt.Ammonomicon, sprite, r, P, Color.white);
                Rect textRect = pressed ? new Rect(r.x, r.y + P, r.width, r.height) : new Rect(r.x, r.y - P, r.width, r.height);
                BookArt.Font.Draw(textRect, text, GUI.enabled ? BookInk : BookMuted, scale, 1, false, true);
            }
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // Two-choice switch (Off / On); the chosen one is drawn pressed
        private int BSwitch(int selected, string[] labels, float width)
        {
            int result = selected;
            GUILayout.BeginHorizontal(GUILayout.Width(width));
            for (int i = 0; i < labels.Length; i++)
            {
                string label = i == selected ? $"<color=#{ColorUtility.ToHtmlStringRGB(BookRed)}>{labels[i]}</color>" : labels[i];
                if (BButton(label, false, width / labels.Length - 2 * P))
                {
                    result = i;
                }
            }
            GUILayout.EndHorizontal();
            return result;
        }

        // Text box: the speech-bubble frame with our own pixel text over an invisible Unity text field
        private string BField(string value, string controlName, bool password = false, float width = 0f)
        {
            int scale = BodyScale;
            float w = width > 0f ? width : areaWidth;
            Rect r = GUILayoutUtility.GetRect(w, BookArt.Font.Line(scale) + 6 * P, GUILayout.Width(w));
            BookArt.Sliced(BookArt.GameUI, "speech_bubble_border", r, P, Color.white);

            GUI.SetNextControlName(controlName);
            string result = GUI.TextField(r, value ?? "", invisibleField);

            string shown = password ? new string('*', result.Length) : result;
            Rect inner = new Rect(r.x + 4 * P, r.y, r.width - 8 * P, r.height);
            BookArt.Font.Draw(inner, shown, BookInk, scale, 0, false, true);

            bool focused = GUI.GetNameOfFocusedControl() == controlName;
            if (focused && (int)(Time.unscaledTime * 2f) % 2 == 0)
            {
                float caretX = inner.x + BookArt.Font.Measure(shown, scale, 0f).x + P;
                float lineH = BookArt.Font.Line(scale);
                Solid(new Rect(caretX, r.y + (r.height - lineH) / 2f + P, P, lineH - 2 * P), BookInk);
            }
            BSpace(2);
            return result;
        }

        private void BLine()
        {
            BSpace(2);
            Rect r = GUILayoutUtility.GetRect(areaWidth, 5 * P, GUILayout.Width(areaWidth));
            BookArt.Sprite(BookArt.Ammonomicon, "death_page_section_line_filligree_001", r, Color.white);
            BSpace(3);
        }

        // Label on the left, value on the right
        private void BRow(string label, string value, Color valueColor)
        {
            int scale = BodyScale;
            Rect r = GUILayoutUtility.GetRect(areaWidth, BookArt.Font.Line(scale), GUILayout.Width(areaWidth));
            BookArt.Font.Draw(r, label, BookInk, scale, 0, false);
            BookArt.Font.Draw(r, value, valueColor, scale, 2, false);
        }

        private void BBar(int done, int total)
        {
            Rect r = GUILayoutUtility.GetRect(areaWidth, 3 * P, GUILayout.Width(areaWidth));
            Solid(r, new Color32(120, 110, 108, 255));
            float fraction = total > 0 ? Mathf.Clamp01((float)done / total) : 0f;
            if (fraction > 0f)
            {
                Solid(new Rect(r.x, r.y, Mathf.Round(r.width * fraction / P) * P, r.height), BookRed);
            }
            BSpace(3);
        }

        private void BookTitle()
        {
            Rect banner = GUILayoutUtility.GetRect(areaWidth, BookArt.Font.Line(HeadScale) + 6 * P, GUILayout.Width(areaWidth));
            BookArt.Sliced(BookArt.Ammonomicon, "death_page_failure_box_001", banner, P, Color.white);
            BookArt.Font.Draw(banner, "ArchipelaGun", PlateInk, HeadScale, 1, false, true);
            BSpace(3);

            if (SessionHandler.IsConnected)
            {
                BText($"Connected as <color=#{ColorUtility.ToHtmlStringRGB(BookGreen)}>{SessionHandler.ConnectedSlotName}</color>", BookInk, BodyScale, true, 1);
            }
            else
            {
                BText(isConnecting ? "Connecting..." : "Not connected", isConnecting ? BookGold : BookRed, BodyScale, true, 1);
            }
            BLine();
        }

        private static readonly string[] BookmarkNames = { "Connect", "Tracker", "Items", "Hints", "Console" };
        private int hoveredBookmark = -1;
        private Rect hoveredBookmarkRect;

        // Name tag to the left of the hovered bookmark, kept on screen
        private void DrawBookmarkTag()
        {
            if (hoveredBookmark < 0) return;
            string name = BookmarkNames[hoveredBookmark];
            Rect r = hoveredBookmarkRect;
            int scale = BodyScale;
            Vector2 size = BookArt.Font.Measure(name, scale, 0f);
            float tagW = size.x + 8 * P;
            float tagH = BookArt.Font.Line(scale) + 4 * P;
            Rect tag = new Rect(Mathf.Max(0f, r.x - tagW - P), r.y + Mathf.Round((r.height - tagH) / 2f), tagW, tagH);
            BookArt.Sliced(BookArt.Ammonomicon, "death_page_text_boxes_001", tag, P, Color.white);
            BookArt.Font.Draw(tag, name, PlateInk, scale, 1, false, true);
        }

        private void DrawBookmarks()
        {
            hoveredBookmark = -1;
            for (int i = 0; i < BookmarkSprites.Length; i++)
            {
                // Down the book's left edge (the cover starts at x 56), tucked 2px under the cover
                Rect r = BookRect(56 + 2 - 28, 26 + i * 25, 28, 22);
                bool hover = r.Contains(Event.current.mousePosition);
                bool selected = (int)currentTab == i;
                string sprite = BookmarkSprites[i] + (selected ? "_select_001" : (hover ? "_hover_001" : "_001"));
                BookArt.Sprite(BookArt.Ammonomicon, sprite, r, Color.white);

                if (hover)
                {
                    hoveredBookmark = i;
                    hoveredBookmarkRect = r;
                }

                if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                {
                    currentTab = (Tab)i;
                    GUIUtility.keyboardControl = 0;
                    nextTrackerRefresh = 0f;
                    selectedItem = -1;
                    bookLeftScroll = Vector2.zero;
                    bookRightScroll = Vector2.zero;
                    if (currentTab == Tab.Console)
                    {
                        scrollConsoleToBottom = true;
                    }
                }
            }
        }

        private void BookNotConnected()
        {
            BText("You're not connected yet. Connect to see this page.", BookInk, BodyScale);
            BSpace(4);
            if (BButton("Go to Connect", false, 0f))
            {
                currentTab = Tab.Connect;
            }
        }

        private static string Hex(Color c)
        {
            return ColorUtility.ToHtmlStringRGB(c);
        }

        private static Color BookItemColor(Archipelago.MultiClient.Net.Enums.ItemFlags flags)
        {
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.Advancement) != 0) return BookGold;
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.NeverExclude) != 0) return BookBlue;
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.Trap) != 0) return BookRed;
            return BookInk;
        }

        #endregion

        #region Connect

        private void BookConnectLeft()
        {
            BPlate("Server");
            GUILayout.BeginHorizontal();
            float portWidth = 34 * P;
            serverInput = BField(serverInput, "bookServer", false, areaWidth - portWidth - 4 * P);
            GUILayout.Space(4 * P);
            portInput = BField(portInput, "bookPort", false, portWidth);
            GUILayout.EndHorizontal();
            BText("Or paste the whole address, like archipelago.gg:38281.", BookMuted, BodyScale);
            BSpace(3);

            BPlate("Slot name");
            slotInput = BField(slotInput, "bookSlot");
            BSpace(1);
            BPlate("Password");
            passwordInput = BField(passwordInput, "bookPassword", true);
            BText("Leave empty if the room has none.", BookMuted, BodyScale);
            BSpace(4);

            GUI.enabled = !isConnecting;
            GUILayout.BeginHorizontal();
            if (!SessionHandler.IsConnected)
            {
                if (BButton(isConnecting ? "Connecting..." : "Connect", true, 0f))
                {
                    StartCoroutine(ConnectNextFrame());
                }
            }
            else
            {
                if (BButton("Disconnect", false, 0f))
                {
                    SessionHandler.DisconnectFromSession();
                }
                GUILayout.Space(3 * P);
                if (BButton("Reconnect", false, 0f))
                {
                    StartCoroutine(ReconnectNextFrame());
                }
            }
            GUILayout.EndHorizontal();
            GUI.enabled = true;
        }

        private void BookConnectRight()
        {
            if (!string.IsNullOrEmpty(SessionHandler.LastConnectionError) && !SessionHandler.IsConnected)
            {
                BPlate("Couldn't connect");
                BText(SessionHandler.LastConnectionError, BookRed, BodyScale);
                BLine();
            }
            if (!string.IsNullOrEmpty(SessionHandler.VersionWarning))
            {
                BPlate("Version mismatch");
                BText(SessionHandler.VersionWarning, BookRed, BodyScale);
                BLine();
            }

            if (!SessionHandler.IsConnected)
            {
                BPlate("How to join");
                BText("Enter the server and port from your room, your slot name from your player options file, and the room's password if it has one. Then press Connect.", BookInk, BodyScale);
                BSpace(3);
                BText("You can connect from the title screen, the Breach or during a run. Items you've received are handed out when the next floor loads.", BookMuted, BodyScale);
                return;
            }

            BPlate("Options");
            float switchWidth = 60 * P;
            GUILayout.BeginHorizontal();
            BText("DeathLink", BookInk, BodyScale, false);
            GUILayout.FlexibleSpace();
            int deathLink = SessionHandler.DeathLinkOn ? 1 : 0;
            int newDeathLink = BSwitch(deathLink, new[] { "Off", "On" }, switchWidth);
            if (newDeathLink != deathLink) SessionHandler.SetDeathLinkMode(newDeathLink);
            GUILayout.EndHorizontal();
            BSpace(2);

            GUILayout.BeginHorizontal();
            BText("Enemy shuffle", BookInk, BodyScale, false);
            GUILayout.FlexibleSpace();
            int shuffle = SessionHandler.EnemyShuffleOn ? 1 : 0;
            int newShuffle = BSwitch(shuffle, new[] { "Off", "On" }, switchWidth);
            if (newShuffle != shuffle) SessionHandler.SetEnemyShuffleMode(newShuffle);
            GUILayout.EndHorizontal();
            BLine();

            BPlate("Items");
            if (BButton("Retrieve items", false, 0f))
            {
                SessionHandler.RetrieveItemsFromServer();
            }
            BText("Gives you any received items you haven't been handed yet this run.", BookMuted, BodyScale);
        }

        #endregion

        #region Tracker

        private void BookTrackerLeft()
        {
            if (!SessionHandler.IsConnected) { BookNotConnected(); return; }

            bookLeftScroll = GUILayout.BeginScrollView(bookLeftScroll, GUIStyle.none, GUIStyle.none);
            BPlate("Unlocks");
            if (unlockRows.Count == 0)
            {
                BText("No unlock items in this seed.", BookMuted, BodyScale);
            }
            foreach (TrackerRow row in unlockRows)
            {
                BRow(row.Label, row.Value, ToBookColor(row.Color));
            }
            BLine();
            BPlate("Still needed for your goal");
            BText(completionText, BookInk, BodyScale);
            if (receivedItems.Count > 0)
            {
                ItemInfo last = receivedItems[receivedItems.Count - 1];
                BLine();
                BText($"Last received: <color=#{Hex(BookItemColor(last.Flags))}>{last.ItemName}</color> from {SafePlayerName(last)}", BookMuted, BodyScale);
            }
            GUILayout.EndScrollView();
        }

        private void BookTrackerRight()
        {
            if (!SessionHandler.IsConnected) { return; }

            bookRightScroll = GUILayout.BeginScrollView(bookRightScroll, GUIStyle.none, GUIStyle.none);
            BPlate("Checks done");
            foreach (ProgressRow row in checkRows)
            {
                BRow(row.Label, row.Value, BookInk);
                BBar(row.Done, row.Total);
            }
            if (goalRows.Count > 0)
            {
                BLine();
                BPlate("Next milestones");
                foreach (ProgressRow row in goalRows)
                {
                    BRow(row.Label, row.Value, BookInk);
                    BBar(row.Done, row.Total);
                }
            }
            GUILayout.EndScrollView();
        }

        // The plain menu's row colours are for a dark background; map them to inks
        private static Color ToBookColor(Color c)
        {
            if (c == Green) return BookGreen;
            if (c == Gold) return BookGold;
            if (c == Red) return BookRed;
            if (c == Muted) return BookMuted;
            if (c == Blue) return BookBlue;
            return BookInk;
        }

        #endregion

        #region Items

        private string hoveredItemTip = null;

        private void BookItemsLeft()
        {
            if (!SessionHandler.IsConnected) { BookNotConnected(); return; }

            BText($"{receivedItems.Count} received, newest first.", BookMuted, BodyScale);
            BSpace(2);
            hoveredItemTip = null;
            bookLeftScroll = GUILayout.BeginScrollView(bookLeftScroll, GUIStyle.none, GUIStyle.none);
            int scale = BodyScale;
            for (int i = receivedItems.Count - 1; i >= 0; i--)
            {
                ItemInfo item = receivedItems[i];
                Rect r = GUILayoutUtility.GetRect(areaWidth, BookArt.Font.Line(scale) + P, GUILayout.Width(areaWidth));
                bool hover = r.Contains(Event.current.mousePosition);
                if (i == selectedItem || hover)
                {
                    Solid(r, new Color(0.35f, 0.25f, 0.2f, i == selectedItem ? 0.22f : 0.12f));
                }
                BookArt.Font.Draw(new Rect(r.x + P, r.y + P / 2, r.width - 2 * P, r.height), item.ItemName ?? $"Item {item.ItemId}", BookItemColor(item.Flags), scale, 0, false);
                if (hover && i < receivedTooltips.Count)
                {
                    hoveredItemTip = receivedTooltips[i];
                }
                if (GUI.Button(r, GUIContent.none, GUIStyle.none))
                {
                    selectedItem = i;
                }
            }
            GUILayout.EndScrollView();
        }

        private void BookItemsRight()
        {
            if (!SessionHandler.IsConnected) { return; }

            string tip = hoveredItemTip;
            if (tip == null && selectedItem >= 0 && selectedItem < receivedTooltips.Count)
            {
                tip = receivedTooltips[selectedItem];
            }

            if (string.IsNullOrEmpty(tip))
            {
                BPlate("Item details");
                BText("Hover over an item on the left page to read about it here. Click one to keep it open.", BookMuted, BodyScale);
                BSpace(4);
                BText($"<color=#{Hex(BookGold)}>Gold</color> items are progression, <color=#{Hex(BookBlue)}>blue</color> are useful and <color=#{Hex(BookRed)}>red</color> are traps.", BookInk, BodyScale);
                return;
            }

            // First line is the item's name; the rest is its description
            int newline = tip.IndexOf('\n');
            string title = newline > 0 ? tip.Substring(0, newline) : tip;
            string body = newline > 0 ? tip.Substring(newline + 1) : "";
            BText(title, BookRed, HeadScale);
            BLine();
            bookRightScroll = GUILayout.BeginScrollView(bookRightScroll, GUIStyle.none, GUIStyle.none);
            BText(body, BookInk, BodyScale);
            GUILayout.EndScrollView();
        }

        #endregion

        #region Hints

        private void BookHintsLeft()
        {
            if (!SessionHandler.IsConnected) { BookNotConnected(); return; }

            List<HintRow> rows;
            lock (hintLock)
            {
                rows = hintRows;
            }

            BPlate("Your hints");
            bookLeftScroll = GUILayout.BeginScrollView(bookLeftScroll, GUIStyle.none, GUIStyle.none);
            if (rows.Count == 0)
            {
                BText("No hints yet.", BookMuted, BodyScale);
            }
            foreach (HintRow row in rows)
            {
                string state = row.Found ? $"<color=#{Hex(BookGreen)}>Found</color>" : $"<color=#{Hex(BookGold)}>Not found</color>";
                BText(state, BookInk, BodyScale);
                BText(row.Text, row.Progression ? BookGold : BookInk, BodyScale);
                BSpace(3);
            }
            GUILayout.EndScrollView();
        }

        private void BookHintsRight()
        {
            if (!SessionHandler.IsConnected) { return; }

            BPlate("Ask for a hint");
            BText("Type an item's name. Hints cost hint points, and the server's answer appears on the Console page.", BookMuted, BodyScale);
            BSpace(2);
            bool submit = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == "bookHint";
            hintInput = BField(hintInput, "bookHint");
            if ((BButton("Ask", true, 0f) || submit) && hintInput.Trim().Length > 0)
            {
                SessionHandler.DataSender.SendChatMessage("!hint " + hintInput.Trim());
                AddLog($"> !hint {hintInput.Trim()}");
                hintInput = "";
                if (submit) Event.current.Use();
            }
        }

        #endregion

        #region Console

        private void BookConsoleLeft()
        {
            string[] lines;
            lock (logLock)
            {
                lines = logLines.ToArray();
            }

            if (scrollConsoleToBottom && Event.current.type == EventType.Layout)
            {
                bookLeftScroll.y = float.MaxValue;
                scrollConsoleToBottom = false;
            }

            bookLeftScroll = GUILayout.BeginScrollView(bookLeftScroll, GUIStyle.none, GUIStyle.none);
            foreach (string line in lines)
            {
                BText(line, BookInk, BodyScale);
            }
            GUILayout.EndScrollView();
        }

        private void BookConsoleRight()
        {
            BPlate("Send");
            // Reuses the plain menu's console keys (Enter, up/down history) by sharing its control name
            HandleConsoleKeys();
            consoleInput = BField(consoleInput, "consoleInput");
            GUILayout.BeginHorizontal();
            if (BButton("Send", true, 0f))
            {
                SubmitConsole();
            }
            GUILayout.Space(3 * P);
            if (BButton("Help", false, 0f))
            {
                consoleInput = "help";
                SubmitConsole();
            }
            GUILayout.EndHorizontal();
            BSpace(4);
            BText("Mod commands like connect, deathlink and retrieve run here. Anything else, including !hint and !release, goes to the server chat.", BookMuted, BodyScale);
            BSpace(3);
            BText("Up and down bring back earlier commands.", BookMuted, BodyScale);
        }

        #endregion
    }
}
