using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.Data;
using ArchiGungeon.ItemArchipelago;
using BepInEx.Configuration;
using UnityEngine;
using Color = UnityEngine.Color;

namespace ArchiGungeon.UserInterface
{
    /// <summary>
    /// The ArchipelaGun window: Connect, Tracker, Items, Hints and Console tabs, drawn with Unity's IMGUI.
    /// Opened and closed with the menu key (F8 by default). This is the only way to connect.
    /// </summary>
    public class ArchipelagoMenu : MonoBehaviour
    {
        public static ArchipelagoMenu Instance { get; private set; }

        private enum Tab { Connect, Tracker, Items, Hints, Console }
        private static readonly string[] TabNames = { "Connect", "Tracker", "Items", "Hints", "Console" };

        // ---- settings (BepInEx config file)
        private static ConfigEntry<KeyCode> menuKey;
        private static ConfigEntry<bool> showStatusIndicator;
        private static ConfigEntry<float> uiScale;
        private static ConfigEntry<bool> pauseWhileOpen;

        // ---- window state
        public bool IsOpen { get; private set; }
        private Tab currentTab = Tab.Connect;
        private Rect windowRect;
        private bool windowPlaced = false;
        private bool cursorWasVisible;
        private bool pausedGame = false;
        private const int WindowID = 0x4A7C;

        // ---- connect tab
        private string serverInput = "archipelago.gg";
        private string portInput = "38281";
        private string slotInput = "";
        private string passwordInput = "";
        private bool isConnecting = false;

        // ---- console tab
        private static readonly object logLock = new object();
        private static readonly List<string> logLines = new List<string>();
        private const int MaxLogLines = 400;
        private string consoleInput = "";
        private readonly List<string> commandHistory = new List<string>();
        private int historyIndex = -1;
        private bool scrollConsoleToBottom = true;

        // ---- hints tab
        private static readonly object hintLock = new object();
        private static List<HintRow> hintRows = new List<HintRow>();
        private object trackedHintSession = null;
        private string hintInput = "";

        // ---- death screen recap (F8 there shows/hides it; no clicks, since the game's death screen uses
        // Space/Esc and its own buttons underneath)
        private bool deathScreenOpen = false;
        private bool recapVisible = false;
        private float nextDeathScreenCheck = 0f;
        private Vector2 recapScroll;

        // ---- scroll positions
        private Vector2 trackerScroll, itemsScroll, hintsScroll, consoleScroll;

        // ---- tracker cache, refreshed about once a second
        private float nextTrackerRefresh = 0f;
        private readonly List<TrackerRow> unlockRows = new List<TrackerRow>();
        private readonly List<ProgressRow> checkRows = new List<ProgressRow>();
        private readonly List<ProgressRow> goalRows = new List<ProgressRow>();
        private string completionText = "";
        private List<ItemInfo> receivedItems = new List<ItemInfo>();
        private List<string> receivedTooltips = new List<string>();

        // ---- styles
        private bool stylesBuilt = false;
        private GUIStyle windowStyle, tabStyle, tabActiveStyle, labelStyle, mutedStyle, headerStyle,
            valueStyle, buttonStyle, fieldStyle, boxStyle, barBackStyle, barFillStyle, statusStyle, errorStyle, consoleStyle, tooltipStyle, rowHighlightStyle;

        private static readonly Color Gold = new Color(0.96f, 0.82f, 0.25f);
        private static readonly Color Green = new Color(0.45f, 0.85f, 0.45f);
        private static readonly Color Red = new Color(0.95f, 0.4f, 0.35f);
        private static readonly Color Blue = new Color(0.45f, 0.65f, 1f);
        private static readonly Color Muted = new Color(0.65f, 0.65f, 0.68f);

        private struct TrackerRow { public string Label; public string Value; public Color Color; }
        private struct ProgressRow { public string Label; public int Done; public int Total; public string Value; }
        private struct HintRow { public string Text; public bool Found; public bool Progression; }

        #region Setup

        public static void Initialize(GameObject host, ConfigFile config)
        {
            menuKey = config.Bind("Menu", "MenuKey", KeyCode.F8, "Key that opens and closes the ArchipelaGun menu.");
            showStatusIndicator = config.Bind("Menu", "ShowConnectionStatus", true, "Show a small connection status label at the top of the screen.");
            uiScale = config.Bind("Menu", "UIScale", 1f, "Extra size multiplier for the menu, on top of automatic resolution scaling.");
            pauseWhileOpen = config.Bind("Menu", "PauseWhileOpen", true, "Pause the game while the menu is open during a run.");

            if (Instance == null)
            {
                Instance = host.AddComponent<ArchipelagoMenu>();
            }
        }

        private void Start()
        {
            if (ConnectionDataWriter.CheckPreviousConnectionExists())
            {
                PlayerConnectionInfo saved = ConnectionDataWriter.SavedConnectionSettings;
                serverInput = saved.IP ?? serverInput;
                portInput = saved.Port ?? portInput;
                slotInput = saved.PlayerName ?? "";
                passwordInput = saved.Password ?? "";
            }

            AddLog("<color=#7FE07F>ArchipelaGun</color> menu. Type <color=#F4D03F>help</color> for console commands.");
        }

        #endregion

        #region Open / close

        public void Open()
        {
            if (IsOpen)
            {
                return;
            }

            IsOpen = true;
            currentTab = SessionHandler.IsConnected ? Tab.Tracker : Tab.Connect;
            nextTrackerRefresh = 0f;
            scrollConsoleToBottom = true;

            cursorWasVisible = Cursor.visible;
            Cursor.visible = true;

            if (pauseWhileOpen.Value && GameManager.HasInstance && !GameManager.Instance.IsFoyer)
            {
                BraveTime.RegisterTimeScaleMultiplier(0f, gameObject);
                pausedGame = true;
            }
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            Cursor.visible = cursorWasVisible;
            GUIUtility.keyboardControl = 0;

            if (pausedGame)
            {
                BraveTime.ClearMultiplier(gameObject);
                pausedGame = false;
            }
        }

        /// <summary>Called after a successful login.</summary>
        public void OnConnected()
        {
            isConnecting = false;
            currentTab = Tab.Tracker;
            nextTrackerRefresh = 0f;
        }

        private void Update()
        {
            if (Time.unscaledTime >= nextDeathScreenCheck)
            {
                nextDeathScreenCheck = Time.unscaledTime + 0.5f;
                bool open = UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>() != null;
                if (open && !deathScreenOpen)
                {
                    nextTrackerRefresh = 0f; // fresh data for the recap
                }
                if (!open)
                {
                    recapVisible = false;
                }
                deathScreenOpen = open;
            }

            if (deathScreenOpen && !IsOpen)
            {
                if (menuKey != null && Input.GetKeyDown(menuKey.Value))
                {
                    recapVisible = !recapVisible;
                }
                if (Time.unscaledTime >= nextTrackerRefresh)
                {
                    nextTrackerRefresh = Time.unscaledTime + 1f;
                    RefreshTracker();
                }
            }
            else if (menuKey != null && Input.GetKeyDown(menuKey.Value))
            {
                if (IsOpen)
                {
                    ArchipelagoGUI.Instance?.OnClose();
                }
                else
                {
                    ArchipelagoGUI.Instance?.OnOpen();
                }
            }

            KeepHintsTracked();

            // The game hides the system cursor during play; keep it visible while the window is up
            if (IsOpen && !Cursor.visible)
            {
                Cursor.visible = true;
            }

            if (IsOpen && Time.unscaledTime >= nextTrackerRefresh)
            {
                nextTrackerRefresh = Time.unscaledTime + 1f;
                RefreshTracker();
            }
        }

        private void OnDestroy()
        {
            if (pausedGame)
            {
                BraveTime.ClearMultiplier(gameObject);
            }
        }

        #endregion

        #region Drawing

        private float Scale
        {
            get { return Mathf.Max(0.5f, Screen.height / 1080f) * Mathf.Clamp(uiScale.Value, 0.5f, 3f); }
        }

        private void OnGUI()
        {
            BuildStyles();

            float scale = Scale;
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float virtualWidth = Screen.width / scale;
            float virtualHeight = Screen.height / scale;

            if (!IsOpen)
            {
                if (deathScreenOpen && SessionHandler.IsConnected)
                {
                    DrawDeathRecap(virtualWidth, virtualHeight);
                }
                else
                {
                    DrawStatusIndicator(virtualWidth);
                }
                GUI.matrix = previousMatrix;
                return;
            }

            if (!windowPlaced)
            {
                float w = Mathf.Min(900f, virtualWidth - 40f);
                float h = Mathf.Min(620f, virtualHeight - 40f);
                windowRect = new Rect((virtualWidth - w) / 2f, (virtualHeight - h) / 2f, w, h);
                windowPlaced = true;
            }

            HandleGlobalKeys();
            windowRect = GUI.Window(WindowID, windowRect, DrawWindow, GUIContent.none, windowStyle);

            // Keep the window on screen if the resolution changed
            windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, virtualWidth - windowRect.width));
            windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, virtualHeight - windowRect.height));

            GUI.matrix = previousMatrix;
        }

        private void HandleGlobalKeys()
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                ArchipelagoGUI.Instance?.OnClose();
            }
        }

        private void DrawStatusIndicator(float virtualWidth)
        {
            if (!showStatusIndicator.Value || GameManager.Instance == null)
            {
                return;
            }

            bool connected = SessionHandler.IsConnected;
            string text = connected ? "AP ● Connected" : $"AP ● Offline ({menuKey.Value} to connect)";
            statusStyle.normal.textColor = connected ? Green : Red;
            GUI.Label(new Rect(virtualWidth / 2f - 130f, 4f, 260f, 22f), text, statusStyle);
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            // Title bar
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>ArchipelaGun</b>", headerStyle);
            GUILayout.FlexibleSpace();
            if (SessionHandler.IsConnected)
            {
                GUILayout.Label(Colored($"● Connected as {SessionHandler.ConnectedSlotName}", Green), labelStyle);
            }
            else
            {
                GUILayout.Label(Colored(isConnecting ? "● Connecting…" : "● Not connected", isConnecting ? Gold : Red), labelStyle);
            }
            GUILayout.Space(12f);
            if (GUILayout.Button("Close", buttonStyle, GUILayout.Width(70f)))
            {
                ArchipelagoGUI.Instance?.OnClose();
            }
            GUILayout.EndHorizontal();

            // Tabs
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TabNames.Length; i++)
            {
                bool active = (int)currentTab == i;
                if (GUILayout.Button(TabNames[i], active ? tabActiveStyle : tabStyle, GUILayout.Width(110f)))
                {
                    currentTab = (Tab)i;
                    GUIUtility.keyboardControl = 0;
                    nextTrackerRefresh = 0f;
                    if (currentTab == Tab.Console)
                    {
                        scrollConsoleToBottom = true;
                    }
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);

            switch (currentTab)
            {
                case Tab.Connect: DrawConnectTab(); break;
                case Tab.Tracker: DrawTrackerTab(); break;
                case Tab.Items: DrawItemsTab(); break;
                case Tab.Hints: DrawHintsTab(); break;
                case Tab.Console: DrawConsoleTab(); break;
            }

            GUILayout.EndVertical();

            // Drag by the top strip
            GUI.DragWindow(new Rect(0, 0, 10000, 40));
        }

        #endregion

        #region Connect tab

        private void DrawConnectTab()
        {
            GUILayout.BeginVertical(boxStyle);

            GUILayout.Label("Server", mutedStyle);
            GUILayout.BeginHorizontal();
            serverInput = GUILayout.TextField(serverInput, fieldStyle, GUILayout.ExpandWidth(true));
            GUILayout.Space(8f);
            GUILayout.Label(":", labelStyle, GUILayout.Width(10f));
            portInput = GUILayout.TextField(portInput, 6, fieldStyle, GUILayout.Width(90f));
            GUILayout.EndHorizontal();
            GUILayout.Label("You can also paste the whole address, like archipelago.gg:38281.", mutedStyle);

            GUILayout.Space(6f);
            GUILayout.Label("Slot name", mutedStyle);
            slotInput = GUILayout.TextField(slotInput, fieldStyle);

            GUILayout.Space(6f);
            GUILayout.Label("Password (leave empty if there isn't one)", mutedStyle);
            passwordInput = GUILayout.PasswordField(passwordInput, '*', fieldStyle);

            GUILayout.Space(10f);
            GUILayout.BeginHorizontal();
            GUI.enabled = !isConnecting;
            if (!SessionHandler.IsConnected)
            {
                if (GUILayout.Button(isConnecting ? "Connecting…" : "Connect", buttonStyle, GUILayout.Width(160f)))
                {
                    StartCoroutine(ConnectNextFrame());
                }
            }
            else
            {
                if (GUILayout.Button("Disconnect", buttonStyle, GUILayout.Width(160f)))
                {
                    SessionHandler.DisconnectFromSession();
                }
                if (GUILayout.Button("Reconnect", buttonStyle, GUILayout.Width(160f)))
                {
                    StartCoroutine(ReconnectNextFrame());
                }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(SessionHandler.LastConnectionError) && !SessionHandler.IsConnected)
            {
                GUILayout.Space(8f);
                GUILayout.Label(SessionHandler.LastConnectionError, errorStyle);
            }

            if (!string.IsNullOrEmpty(SessionHandler.VersionWarning))
            {
                GUILayout.Space(8f);
                GUILayout.Label(SessionHandler.VersionWarning, errorStyle);
            }

            GUILayout.EndVertical();

            if (SessionHandler.IsConnected)
            {
                GUILayout.Space(10f);
                GUILayout.BeginVertical(boxStyle);
                GUILayout.Label("<b>Options</b>", labelStyle);

                GUILayout.BeginHorizontal();
                GUILayout.Label("DeathLink", labelStyle, GUILayout.Width(160f));
                int deathLink = SessionHandler.DeathLinkOn ? 1 : 0;
                int newDeathLink = GUILayout.Toolbar(deathLink, new[] { "Off", "On" }, buttonStyle, GUILayout.Width(200f));
                if (newDeathLink != deathLink)
                {
                    SessionHandler.SetDeathLinkMode(newDeathLink);
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Enemy shuffle", labelStyle, GUILayout.Width(160f));
                int shuffle = SessionHandler.EnemyShuffleOn ? 1 : 0;
                int newShuffle = GUILayout.Toolbar(shuffle, new[] { "Off", "On" }, buttonStyle, GUILayout.Width(200f));
                if (newShuffle != shuffle)
                {
                    SessionHandler.SetEnemyShuffleMode(newShuffle);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(6f);
                if (GUILayout.Button("Retrieve items from server", buttonStyle, GUILayout.Width(260f)))
                {
                    SessionHandler.RetrieveItemsFromServer();
                }
                GUILayout.Label("Gives you any received items you haven't been handed yet this run.", mutedStyle);
                GUILayout.EndVertical();
            }
        }

        private IEnumerator ConnectNextFrame()
        {
            string host;
            string port;
            if (!ParseAddress(serverInput, portInput, out host, out port))
            {
                SessionHandler.SetConnectionError("Enter a server address and a port number, like archipelago.gg and 38281.");
                yield break;
            }

            if (string.IsNullOrEmpty(slotInput.Trim()))
            {
                SessionHandler.SetConnectionError("Enter your slot name. It's the name from your player options file.");
                yield break;
            }

            serverInput = host;
            portInput = port;
            isConnecting = true;
            SessionHandler.SetConnectionError(null);

            // Let the "Connecting…" label draw before the (blocking) login
            yield return null;
            yield return null;

            try
            {
                SessionHandler.ArchipelagoConnect(host, port, slotInput.Trim(), passwordInput);
            }
            finally
            {
                isConnecting = false;
            }
        }

        private IEnumerator ReconnectNextFrame()
        {
            isConnecting = true;
            yield return null;
            yield return null;

            try
            {
                SessionHandler.ReconnectSession();
            }
            finally
            {
                isConnecting = false;
            }
        }

        /// <summary>Accepts "host", "host:port", "ws://host:port/" or a separate port box.</summary>
        public static bool ParseAddress(string server, string portBox, out string host, out string port)
        {
            host = (server ?? "").Trim();
            port = (portBox ?? "").Trim();

            foreach (string prefix in new[] { "wss://", "ws://", "https://", "http://" })
            {
                if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    host = host.Substring(prefix.Length);
                }
            }
            host = host.TrimEnd('/');

            int colon = host.LastIndexOf(':');
            if (colon > 0 && colon < host.Length - 1)
            {
                string tail = host.Substring(colon + 1);
                int parsedTail;
                if (int.TryParse(tail, out parsedTail))
                {
                    port = tail;
                    host = host.Substring(0, colon);
                }
            }

            int parsedPort;
            return host.Length > 0 && int.TryParse(port, out parsedPort) && parsedPort > 0 && parsedPort < 65536;
        }

        #endregion

        #region Tracker tab

        private void RefreshTracker()
        {
            unlockRows.Clear();
            checkRows.Clear();
            goalRows.Clear();
            completionText = "";

            if (!SessionHandler.IsConnected)
            {
                return;
            }

            try
            {
                if (ProgressionUnlocks.ChamberLocksEnabled)
                {
                    int access = ProgressionUnlocks.ChamberAccessCount;
                    unlockRows.Add(new TrackerRow
                    {
                        Label = "Chamber access",
                        Value = $"{access} / {ProgressionUnlocks.MaxChamberAccess}",
                        Color = access >= ProgressionUnlocks.MaxChamberAccess ? Green : Color.white,
                    });
                }

                if (ProgressionUnlocks.SellCreepsFavorEnabled)
                {
                    bool favor = SessionHandler.CountReceivedItem(ProgressionUnlocks.SellCreepsFavorID) > 0;
                    unlockRows.Add(new TrackerRow { Label = "Sell Creep's Favor", Value = favor ? "Found" : "Not found", Color = favor ? Green : Muted });
                }

                if (ProgressionUnlocks.BulletPartsEnabled)
                {
                    int parts = ProgressionUnlocks.BulletPartCount;
                    unlockRows.Add(new TrackerRow { Label = "Bullet parts", Value = $"{parts} / 4", Color = parts >= 4 ? Green : Color.white });
                }

                if (ExtraChecks.TokensRequired > 0)
                {
                    int tokens = ExtraChecks.TokenCount;
                    unlockRows.Add(new TrackerRow { Label = "Archipelago Tokens", Value = $"{tokens} / {ExtraChecks.TokensRequired}",
                        Color = tokens >= ExtraChecks.TokensRequired ? Green : Gold });
                }

                if (ExtraChecks.BossHuntCount > 0)
                {
                    int bosses = ExtraChecks.BossesKilled;
                    unlockRows.Add(new TrackerRow { Label = "Boss Hunt", Value = $"{bosses} / {ExtraChecks.BossHuntCount}",
                        Color = bosses >= ExtraChecks.BossHuntCount ? Green : Gold });
                }

                if (ProgressionUnlocks.PersistentUpgradesEnabled)
                {
                    AddUpgradeRow("Max health", PersistentUpgrades.MaxHealthID, 3, "+{0}");
                    AddUpgradeRow("Armor", PersistentUpgrades.ArmorID, 3, "+{0}");
                    AddUpgradeRow("Blanks", PersistentUpgrades.BlankID, 3, "+{0}");
                    AddUpgradeRow("Starting casings", PersistentUpgrades.CasingsID, 4, "+{0}");
                    AddUpgradeRow("Starting keys", PersistentUpgrades.KeyID, 3, "+{0}");
                }

                AddCheckGroup("AP Items", 8755000, 8755199);
                AddCheckGroup("Achievements", 8755200, 8755899);
                AddCheckGroup("Enemy kills", 8755900, 8755999);
                AddCheckGroup("Bosses", 8756000, 8756099);
                AddCheckGroup("Master Rounds", 8756100, 8756199);
                AddCheckGroup("Shrines", 8756200, 8756299);
                AddCheckGroup("NPC rescues", 8756300, 8756399);

                foreach (CountGoalManager.GoalProgress progress in CountGoalManager.GetGoalProgress())
                {
                    goalRows.Add(new ProgressRow
                    {
                        Label = progress.Label,
                        Done = progress.Count,
                        Total = progress.NextGoal,
                        Value = $"{progress.Count} / {progress.NextGoal}" + (progress.GoalsLeft > 1 ? $"   ({progress.GoalsLeft} left)" : ""),
                    });
                }

                List<string> unmet = ArchipelagoCompletion.GetAllUnmetCountsForGoals();
                completionText = unmet.Count == 0 ? "Everything needed is done. Finish a run to send your goal." : string.Join(", ", unmet.ToArray());

                receivedItems = SessionHandler.GetReceivedItemsSnapshot();

                // Copy numbers count each item type in the order received, matching how they're handed out
                receivedTooltips = new List<string>(receivedItems.Count);
                Dictionary<long, int> copies = new Dictionary<long, int>();
                foreach (ItemInfo item in receivedItems)
                {
                    int copy;
                    copies.TryGetValue(item.ItemId, out copy);
                    copies[item.ItemId] = copy + 1;
                    receivedTooltips.Add(ItemTooltips.Build(item, copy));
                }
            }
            catch (Exception e)
            {
                ArchiGungeon.DebugTools.ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Tracker refresh failed: {e.Message}");
            }
        }

        private void AddUpgradeRow(string label, long id, int max, string format)
        {
            int count = SessionHandler.CountReceivedItem(id);
            unlockRows.Add(new TrackerRow
            {
                Label = label,
                Value = string.Format(format, count) + $"  of {max}",
                Color = count > 0 ? Color.white : Muted,
            });
        }

        private void AddCheckGroup(string label, long firstID, long lastID)
        {
            var locations = SessionHandler.Session.Locations;
            int total = locations.AllLocations.Count(id => id >= firstID && id <= lastID);
            if (total == 0)
            {
                return;
            }
            int done = locations.AllLocationsChecked.Count(id => id >= firstID && id <= lastID);
            checkRows.Add(new ProgressRow { Label = label, Done = done, Total = total, Value = $"{done} / {total}" });
        }

        private void DrawTrackerTab()
        {
            if (!SessionHandler.IsConnected)
            {
                DrawNotConnected();
                return;
            }

            trackerScroll = GUILayout.BeginScrollView(trackerScroll);
            GUILayout.BeginHorizontal();

            // Left column: unlocks and goal
            GUILayout.BeginVertical(boxStyle, GUILayout.Width(windowRect.width * 0.45f));
            GUILayout.Label("<b>Unlocks</b>", labelStyle);
            if (unlockRows.Count == 0)
            {
                GUILayout.Label("No unlock items in this seed.", mutedStyle);
            }
            foreach (TrackerRow row in unlockRows)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Label, labelStyle);
                GUILayout.FlexibleSpace();
                GUILayout.Label(Colored(row.Value, row.Color), valueStyle);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(12f);
            GUILayout.Label("<b>Still needed for your goal</b>", labelStyle);
            GUILayout.Label(completionText, mutedStyle);
            GUILayout.EndVertical();

            GUILayout.Space(10f);

            // Right column: checks
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("<b>Checks done</b>", labelStyle);
            foreach (ProgressRow row in checkRows)
            {
                DrawProgressRow(row);
            }

            if (goalRows.Count > 0)
            {
                GUILayout.Space(10f);
                GUILayout.Label("<b>Next milestones</b>", labelStyle);
                foreach (ProgressRow row in goalRows)
                {
                    DrawProgressRow(row);
                }
            }
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            if (receivedItems.Count > 0)
            {
                ItemInfo last = receivedItems[receivedItems.Count - 1];
                GUILayout.Space(8f);
                GUILayout.Label($"Last received: {Colored(last.ItemName, ItemColor(last.Flags))} from {SafePlayerName(last)}", mutedStyle);
            }

            GUILayout.EndScrollView();
        }

        private void DrawProgressRow(ProgressRow row)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(row.Label, labelStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(row.Value, valueStyle);
            GUILayout.EndHorizontal();

            Rect bar = GUILayoutUtility.GetRect(10f, 6f, GUILayout.ExpandWidth(true));
            GUI.Box(bar, GUIContent.none, barBackStyle);
            float fraction = row.Total > 0 ? Mathf.Clamp01((float)row.Done / row.Total) : 0f;
            if (fraction > 0f)
            {
                GUI.Box(new Rect(bar.x, bar.y, bar.width * fraction, bar.height), GUIContent.none, barFillStyle);
            }
            GUILayout.Space(6f);
        }

        #endregion

        #region Items tab

        private void DrawItemsTab()
        {
            if (!SessionHandler.IsConnected)
            {
                DrawNotConnected();
                return;
            }

            GUILayout.Label($"{receivedItems.Count} items received, newest first. Gold is progression, blue is useful. Hover an item for details.", mutedStyle);
            itemsScroll = GUILayout.BeginScrollView(itemsScroll, boxStyle);

            // Hover is detected by hand: Unity's GUI.tooltip didn't show anything in-game
            string hoveredTip = null;
            for (int i = receivedItems.Count - 1; i >= 0; i--)
            {
                ItemInfo item = receivedItems[i];
                string tip = i < receivedTooltips.Count ? receivedTooltips[i] : "";
                GUILayout.BeginHorizontal();
                GUILayout.Label(Colored(item.ItemName ?? $"Item {item.ItemId}", ItemColor(item.Flags)), labelStyle, GUILayout.Width(windowRect.width * 0.4f));
                GUILayout.Label($"from {SafePlayerName(item)}", mutedStyle, GUILayout.Width(windowRect.width * 0.2f));
                GUILayout.Label(item.LocationName ?? "", mutedStyle);
                GUILayout.EndHorizontal();

                // The row's rect and the mouse are both in the scroll view's content space here
                if (Event.current.type == EventType.Repaint && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                {
                    hoveredTip = tip;
                    GUI.Box(GUILayoutUtility.GetLastRect(), GUIContent.none, rowHighlightStyle);
                }
            }

            GUILayout.EndScrollView();
            DrawTooltip(hoveredTip);
        }

        #endregion

        #region Hints tab

        // Hints arrive on the network thread; their names are resolved there and stored as plain rows
        private void KeepHintsTracked()
        {
            var session = SessionHandler.IsConnected ? SessionHandler.Session : null;
            if (session == null || ReferenceEquals(session, trackedHintSession))
            {
                if (session == null)
                {
                    trackedHintSession = null;
                }
                return;
            }

            trackedHintSession = session;
            try
            {
                session.DataStorage.TrackHints(hints =>
                {
                    List<HintRow> rows = new List<HintRow>();
                    foreach (Hint hint in hints)
                    {
                        try
                        {
                            string receiverGame = session.Players.GetPlayerInfo(hint.ReceivingPlayer).Game;
                            string finderGame = session.Players.GetPlayerInfo(hint.FindingPlayer).Game;
                            string item = session.Items.GetItemName(hint.ItemId, receiverGame) ?? $"Item {hint.ItemId}";
                            string location = session.Locations.GetLocationNameFromId(hint.LocationId, finderGame) ?? $"Location {hint.LocationId}";
                            string receiver = session.Players.GetPlayerAlias(hint.ReceivingPlayer);
                            string finder = session.Players.GetPlayerAlias(hint.FindingPlayer);

                            rows.Add(new HintRow
                            {
                                Text = $"{item} for {receiver} is at {location} in {finder}'s world",
                                Found = hint.Found,
                                Progression = (hint.ItemFlags & ItemFlags.Advancement) != 0,
                            });
                        }
                        catch (Exception)
                        {
                            // Skip a hint whose names can't be resolved
                        }
                    }

                    rows.Sort((a, b) => a.Found.CompareTo(b.Found));
                    lock (hintLock)
                    {
                        hintRows = rows;
                    }
                }, true);
            }
            catch (Exception e)
            {
                AddLog($"Couldn't load hints: {e.Message}");
            }
        }

        private void DrawHintsTab()
        {
            if (!SessionHandler.IsConnected)
            {
                DrawNotConnected();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Hint for an item", labelStyle, GUILayout.Width(150f));
            GUI.SetNextControlName("hintInput");
            hintInput = GUILayout.TextField(hintInput, fieldStyle, GUILayout.ExpandWidth(true));
            bool submit = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "hintInput";
            if ((GUILayout.Button("Ask", buttonStyle, GUILayout.Width(80f)) || submit) && hintInput.Trim().Length > 0)
            {
                SessionHandler.DataSender.SendChatMessage("!hint " + hintInput.Trim());
                AddLog($"> !hint {hintInput.Trim()}");
                hintInput = "";
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Uses hint points. The server's answer shows in the Console tab.", mutedStyle);

            List<HintRow> rows;
            lock (hintLock)
            {
                rows = hintRows;
            }

            hintsScroll = GUILayout.BeginScrollView(hintsScroll, boxStyle);
            if (rows.Count == 0)
            {
                GUILayout.Label("No hints yet.", mutedStyle);
            }
            foreach (HintRow row in rows)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Found ? Colored("Found", Green) : Colored("Not found", Gold), valueStyle, GUILayout.Width(100f));
                GUILayout.Label(row.Progression ? Colored(row.Text, Gold) : row.Text, labelStyle);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
        }

        #endregion

        #region Console tab

        /// <summary>Thread-safe: server messages arrive on the network thread.</summary>
        public static void AddLog(string line)
        {
            if (line == null)
            {
                return;
            }

            lock (logLock)
            {
                foreach (string part in line.Split('\n'))
                {
                    logLines.Add(part);
                }
                while (logLines.Count > MaxLogLines)
                {
                    logLines.RemoveAt(0);
                }
            }

            if (Instance != null)
            {
                Instance.scrollConsoleToBottom = true;
            }
        }

        private void DrawConsoleTab()
        {
            string[] lines;
            lock (logLock)
            {
                lines = logLines.ToArray();
            }

            if (scrollConsoleToBottom && Event.current.type == EventType.Layout)
            {
                consoleScroll.y = float.MaxValue;
                scrollConsoleToBottom = false;
            }

            consoleScroll = GUILayout.BeginScrollView(consoleScroll, boxStyle, GUILayout.ExpandHeight(true));
            foreach (string line in lines)
            {
                GUILayout.Label(line, consoleStyle);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            HandleConsoleKeys();

            GUILayout.BeginHorizontal();
            GUI.SetNextControlName("consoleInput");
            consoleInput = GUILayout.TextField(consoleInput, fieldStyle, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Send", buttonStyle, GUILayout.Width(80f)))
            {
                SubmitConsole();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Commands like help, connect and deathlink run here. Anything starting with ! or other text goes to the server chat.", mutedStyle);
        }

        private void HandleConsoleKeys()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || GUI.GetNameOfFocusedControl() != "consoleInput")
            {
                return;
            }

            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                SubmitConsole();
                e.Use();
            }
            else if (e.keyCode == KeyCode.UpArrow && commandHistory.Count > 0)
            {
                historyIndex = Mathf.Max(0, (historyIndex < 0 ? commandHistory.Count : historyIndex) - 1);
                consoleInput = commandHistory[historyIndex];
                e.Use();
            }
            else if (e.keyCode == KeyCode.DownArrow && historyIndex >= 0)
            {
                historyIndex++;
                consoleInput = historyIndex < commandHistory.Count ? commandHistory[historyIndex] : "";
                if (historyIndex >= commandHistory.Count)
                {
                    historyIndex = -1;
                }
                e.Use();
            }
        }

        private void SubmitConsole()
        {
            string text = consoleInput.Trim();
            consoleInput = "";
            historyIndex = -1;

            if (text.Length == 0)
            {
                return;
            }

            if (commandHistory.Count == 0 || commandHistory[commandHistory.Count - 1] != text)
            {
                commandHistory.Add(text);
            }

            AddLog($"<color=#F4D03F>> {text}</color>");
            ArchipelagoGUI.Instance?.RunCommand(text);
        }

        #endregion

        #region Death screen recap

        private void DrawDeathRecap(float virtualWidth, float virtualHeight)
        {
            if (!recapVisible)
            {
                statusStyle.normal.textColor = Gold;
                GUI.Label(new Rect(virtualWidth / 2f - 200f, 4f, 400f, 24f), $"{menuKey.Value}: this run's Archipelago recap", statusStyle);
                return;
            }

            float width = Mathf.Min(620f, virtualWidth - 40f);
            float height = Mathf.Min(560f, virtualHeight - 60f);
            Rect panel = new Rect((virtualWidth - width) / 2f, (virtualHeight - height) / 2f, width, height);
            GUI.Box(panel, GUIContent.none, windowStyle);

            GUILayout.BeginArea(new Rect(panel.x + 16f, panel.y + 12f, panel.width - 32f, panel.height - 24f));
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>This run</b>", headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{menuKey.Value} hides · hover a line", mutedStyle, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            GUILayout.Space(6f);

            string hoveredTip = null;
            recapScroll = GUILayout.BeginScrollView(recapScroll, boxStyle);

            List<KeyValuePair<string, string>> found = APPickUpItem.GetFoundThisRun();
            GUILayout.Label($"<b>AP Items you picked up ({found.Count})</b>", labelStyle);
            if (found.Count == 0)
            {
                GUILayout.Label("None this run.", mutedStyle);
            }
            foreach (KeyValuePair<string, string> entry in found)
            {
                GUILayout.Label(entry.Key, labelStyle);
                if (Event.current.type == EventType.Repaint && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                {
                    hoveredTip = entry.Key + (entry.Value.Length > 0 ? "\n" + entry.Value : "") + "\n\nSent to its owner when you picked it up.";
                    GUI.Box(GUILayoutUtility.GetLastRect(), GUIContent.none, rowHighlightStyle);
                }
            }

            GUILayout.Space(10f);
            GUILayout.Label($"<b>Items you were given ({receivedItems.Count})</b>", labelStyle);
            for (int i = receivedItems.Count - 1; i >= 0; i--)
            {
                ItemInfo item = receivedItems[i];
                GUILayout.Label(Colored(item.ItemName ?? $"Item {item.ItemId}", ItemColor(item.Flags)) + RecapResult(item, i), labelStyle);
                if (Event.current.type == EventType.Repaint && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                {
                    hoveredTip = i < receivedTooltips.Count ? receivedTooltips[i] : "";
                    GUI.Box(GUILayoutUtility.GetLastRect(), GUIContent.none, rowHighlightStyle);
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            // The tooltip is drawn in screen space here, not inside a window
            if (!string.IsNullOrEmpty(hoveredTip) && Event.current.type == EventType.Repaint)
            {
                const float tipWidth = 380f;
                float tipHeight = tooltipStyle.CalcHeight(new GUIContent(hoveredTip), tipWidth);
                Vector2 mouse = Event.current.mousePosition;
                float x = Mathf.Min(mouse.x + 18f, virtualWidth - tipWidth - 8f);
                float y = mouse.y + 18f;
                if (y + tipHeight > virtualHeight - 8f)
                {
                    y = Mathf.Max(8f, mouse.y - tipHeight - 8f);
                }
                GUI.Box(new Rect(x, y, tipWidth, tipHeight), hoveredTip, tooltipStyle);
            }
        }

        // " → Plasma Cell" for random-tier items, from the tooltip's "This run it gave" line
        private string RecapResult(ItemInfo item, int index)
        {
            if (index >= receivedTooltips.Count)
            {
                return "";
            }
            const string marker = "This run it gave: ";
            string tip = receivedTooltips[index];
            int at = tip.IndexOf(marker);
            if (at < 0)
            {
                return "";
            }
            int end = tip.IndexOf('\n', at);
            string name = end < 0 ? tip.Substring(at + marker.Length) : tip.Substring(at + marker.Length, end - at - marker.Length);
            return "  →  " + name;
        }

        #endregion

        #region Helpers

        // IMGUI sets GUI.tooltip for the hovered control; draw it as a box beside the mouse, kept inside the window
        private void DrawTooltip(string tip)
        {
            if (string.IsNullOrEmpty(tip) || Event.current.type != EventType.Repaint)
            {
                return;
            }

            const float width = 380f;
            float height = tooltipStyle.CalcHeight(new GUIContent(tip), width);
            Vector2 mouse = Event.current.mousePosition;
            float x = Mathf.Min(mouse.x + 18f, windowRect.width - width - 8f);
            float y = mouse.y + 18f;
            if (y + height > windowRect.height - 8f)
            {
                y = Mathf.Max(8f, mouse.y - height - 8f);
            }
            GUI.Box(new Rect(x, y, width, height), tip, tooltipStyle);
        }

        private void DrawNotConnected()
        {
            GUILayout.BeginVertical(boxStyle);
            GUILayout.Label("You're not connected yet.", labelStyle);
            GUILayout.Label("Connect on the Connect tab to see your progress here.", mutedStyle);
            if (GUILayout.Button("Go to Connect", buttonStyle, GUILayout.Width(180f)))
            {
                currentTab = Tab.Connect;
            }
            GUILayout.EndVertical();
        }

        private static string SafePlayerName(ItemInfo item)
        {
            return item.Player != null ? item.Player.Name : "unknown";
        }

        private static Color ItemColor(ItemFlags flags)
        {
            if ((flags & ItemFlags.Advancement) != 0) return Gold;
            if ((flags & ItemFlags.NeverExclude) != 0) return Blue;
            if ((flags & ItemFlags.Trap) != 0) return Red;
            return Color.white;
        }

        private static string Colored(string text, Color color)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
        }

        private static Texture2D SolidTexture(Color color)
        {
            Texture2D texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            return texture;
        }

        private void BuildStyles()
        {
            if (stylesBuilt)
            {
                return;
            }
            stylesBuilt = true;

            Texture2D windowBack = SolidTexture(new Color(0.09f, 0.09f, 0.11f, 0.97f));
            Texture2D panelBack = SolidTexture(new Color(0.14f, 0.14f, 0.17f, 1f));
            Texture2D buttonBack = SolidTexture(new Color(0.22f, 0.22f, 0.27f, 1f));
            Texture2D buttonHover = SolidTexture(new Color(0.3f, 0.3f, 0.36f, 1f));
            Texture2D activeBack = SolidTexture(new Color(0.2f, 0.42f, 0.25f, 1f));
            Texture2D fieldBack = SolidTexture(new Color(0.05f, 0.05f, 0.07f, 1f));
            Texture2D barBack = SolidTexture(new Color(0.25f, 0.25f, 0.3f, 1f));
            Texture2D barFill = SolidTexture(new Color(0.4f, 0.8f, 0.45f, 1f));

            windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(16, 16, 12, 14) };
            windowStyle.normal.background = windowBack;
            windowStyle.onNormal.background = windowBack;

            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = true, wordWrap = true };
            labelStyle.normal.textColor = Color.white;

            mutedStyle = new GUIStyle(labelStyle) { fontSize = 14 };
            mutedStyle.normal.textColor = Muted;

            valueStyle = new GUIStyle(labelStyle) { wordWrap = false, alignment = TextAnchor.MiddleRight };
            consoleStyle = new GUIStyle(labelStyle) { fontSize = 14, margin = new RectOffset(4, 4, 0, 0) };

            errorStyle = new GUIStyle(labelStyle) { fontSize = 15 };
            errorStyle.normal.textColor = Red;

            headerStyle = new GUIStyle(labelStyle) { fontSize = 22, wordWrap = false };
            headerStyle.normal.textColor = Green;

            statusStyle = new GUIStyle(labelStyle) { fontSize = 14, alignment = TextAnchor.MiddleCenter, wordWrap = false };

            boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(12, 12, 10, 10), margin = new RectOffset(0, 0, 0, 0) };
            boxStyle.normal.background = panelBack;

            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 16, padding = new RectOffset(10, 10, 6, 6) };
            buttonStyle.normal.background = buttonBack;
            buttonStyle.hover.background = buttonHover;
            buttonStyle.active.background = activeBack;
            buttonStyle.onNormal.background = activeBack;
            buttonStyle.onHover.background = activeBack;
            buttonStyle.normal.textColor = Color.white;
            buttonStyle.hover.textColor = Color.white;
            buttonStyle.onNormal.textColor = Color.white;
            buttonStyle.onHover.textColor = Color.white;

            tabStyle = new GUIStyle(buttonStyle);
            tabActiveStyle = new GUIStyle(buttonStyle);
            tabActiveStyle.normal.background = activeBack;
            tabActiveStyle.hover.background = activeBack;

            fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 16, padding = new RectOffset(8, 8, 6, 6) };
            fieldStyle.normal.background = fieldBack;
            fieldStyle.focused.background = fieldBack;
            fieldStyle.hover.background = fieldBack;
            fieldStyle.normal.textColor = Color.white;
            fieldStyle.focused.textColor = Color.white;

            tooltipStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 14, wordWrap = true, richText = true, alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(10, 10, 8, 8),
            };
            tooltipStyle.normal.background = SolidTexture(new Color(0.03f, 0.03f, 0.05f, 0.97f));
            tooltipStyle.normal.textColor = Color.white;

            rowHighlightStyle = new GUIStyle { normal = { background = SolidTexture(new Color(1f, 1f, 1f, 0.08f)) } };

            barBackStyle = new GUIStyle { normal = { background = barBack } };
            barFillStyle = new GUIStyle { normal = { background = barFill } };
        }

        #endregion
    }
}
