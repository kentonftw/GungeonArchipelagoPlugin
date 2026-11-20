using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using System.Collections;
using ArchiGungeon.ItemArchipelago;
using ArchiGungeon.GungeonEventHandlers;
using ArchiGungeon.UserInterface;
using ArchiGungeon.EnemyHandlers;
using ArchiGungeon.DebugTools;
using ArchiGungeon.Character;
using ArchiGungeon.Data;

namespace ArchiGungeon.ArchipelagoServer
{

    public class SessionHandler : MonoBehaviour
    {
        public static ArchipelagoSession Session { get; protected set; }
        private static string RoomSeed { get; set; }

        #region Initializing Variables
        private static Dictionary<string, object> PlayerSlotSettings { get; set; } = new Dictionary<string, object>(); // player settings, use to initialize data
        private static PlayerConnectionInfo PlayerServerInfo { get; set; }

        private static Dictionary<PlayerCompletionGoals, string> GameCompletionGoalKeys { get; } = new Dictionary<PlayerCompletionGoals, string>()
        {
            { PlayerCompletionGoals.Dragun, "Dragun" },
            { PlayerCompletionGoals.Lich, "Lich" },
            { PlayerCompletionGoals.PastsBase, "Pasts" },
            { PlayerCompletionGoals.PastsFull, "Pasts" },
            { PlayerCompletionGoals.SecretChamber, "BaseSecret" },
            { PlayerCompletionGoals.AdvancedGungeon, "AdvancedGungeon" },
            { PlayerCompletionGoals.FarewellArms, "FarewellArms" },
        };

        public static bool IsGoalsTextBoxOpen { get; private set; } = false;

        #endregion

        #region Item Handling Variables
        // Mirror of the server's received-items list for the current slot, in server order, one entry per item.
        // Kept in sync by count (see SyncReceivedItemsFromServer), so reconnects don't add items twice.
        private static List<ItemInfo> allItemsReceivedFromServer = new List<ItemInfo>();
        private static List<long> itemsHandledThisRun = new List<long>();
        private static List<long> item_add_queue = new();

        // OnItemReceived runs on the network thread; everything touching the two lists above locks this.
        private static readonly object itemLock = new object();

        // Seed + slot name of the received-items list, so connecting to another game starts fresh.
        private static string receivedItemsSlotKey = string.Empty;

        private static bool IsProgressItemsGiven { get; set; } = false;
        public static bool IsValidToSpawnItems { get; private set; } = false;

        // ---- shown by the ArchipelaGun menu
        public static bool IsConnected
        {
            get { return Session != null && Session.Socket != null && Session.Socket.Connected; }
        }
        public static string ConnectedSlotName { get; private set; } = "";

        /// <summary>Identifies this seed and slot, for rolls that should stay the same every run.</summary>
        public static string SeedKey
        {
            get { return $"{RoomSeed}:{ConnectedSlotName}"; }
        }
        public static string LastConnectionError { get; private set; } = null;
        public static string VersionWarning { get; private set; } = null;
        public static bool DeathLinkOn { get { return IsDeathLinkEnabled; } }
        public static bool EnemyShuffleOn { get; private set; } = false;

        /// <summary>Set once the per-run setup has run; cleared when a new run starts.</summary>
        public static bool RunSetupDoneThisRun { get; private set; } = false;

        public static bool IsInRunWithPlayer()
        {
            return GameManager.HasInstance && !GameManager.Instance.IsFoyer && GameManager.Instance.PrimaryPlayer != null
                && !GameManager.Instance.IsLoadingLevel;
        }

        public static void SetConnectionError(string message)
        {
            LastConnectionError = message;
        }

        /// <summary>Copy of everything received from the server, oldest first.</summary>
        public static List<ItemInfo> GetReceivedItemsSnapshot()
        {
            lock (itemLock)
            {
                return new List<ItemInfo>(allItemsReceivedFromServer);
            }
        }
        #endregion

        #region Randomizer Parameter Variables
        public static DeathLinkService DeathLinkService { get; protected set; }
        // The service exists even with DeathLink off, so sending has to check this
        private static bool IsDeathLinkEnabled { get; set; } = false;
        // Set by the network thread, handled on the game thread in TickCheckItemQueue
        private static string pendingDeathlinkCause = null;

        private static bool HandlingDeathlinkEvent { get; set; } = false;
        #endregion



        #region Connection Handling
        // CONNECTION HANDLING ===============================

        public static void ArchipelagoConnect(string ip, string port, string name, string password = "")
        {
            TrapSpawnHandler.SetCanSpawn(false);
            ConsumableSpawnHandler.SetCanSpawn(false);


            // check for session already in progress
            if (Session != null && Session.Socket.Connected)
            {
                ArchipelagoGUI.ConsoleLog("You are already connected!");
                return;
            }

            ArchipelagoGUI.ConsoleLog($"Connecting to {ip}:{port} as {name}");
            LastConnectionError = null;
            VersionWarning = null;
            LoginResult loginResult;

            int parsedPort;
            if (!int.TryParse(port, out parsedPort))
            {
                LastConnectionError = $"'{port}' isn't a port number. Use the number after the colon in the server address, like 38281.";
                ArchipelagoGUI.ConsoleLog(LastConnectionError);
                return;
            }

            // Handle try login
            try
            {
                loginResult = LoginToArchipelago(ip, port, name, password: password);
            }
            catch (Exception ex)
            {
                loginResult = new LoginFailure(ex.GetBaseException().Message);
            }

            if (!loginResult.Successful)
            {
                Session = null;
                ParseLoginError(ip, port, name, loginResult);
                return;
            }

            // Success after this point
            //LoginSuccessful loginSuccessful = (LoginSuccessful)loginResult;

            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Login successful, handling initializing via Slot Data");
            ResetReceivedItemsIfNewSlot(name);
            PullPlayerYAMLSlotData();

            BindToArchipelagoEvents();
            CheckToCreateDeathlink();
            CheckToRandomizeEnemies();


            PlayerServerInfo = new PlayerConnectionInfo(ip, port, name, password, RoomSeed);
            ConnectionDataWriter.SaveArchipelagoConnectionSettings(PlayerServerInfo);

            // Send anything found while disconnected first, so the location lists below already count it
            DataSender.SendPendingChecksAndGoal();
            InitializeAPLocationChecks();

            TrapSpawnHandler.SetCanSpawn(true);
            ConsumableSpawnHandler.SetCanSpawn(true);

            ConnectedSlotName = name;
            ArchipelagoGUI.ConsoleLog("Connected to Archipelago server.");

            // Show the tracker; the old console closed itself here
            ArchipelagoMenu.Instance?.OnConnected();
            CallRetrieveDataCoroutine();

            return;
        }

        public static void CallRetrieveDataCoroutine()
        {
            if (!TimedServerCalls.IsRetrieveDataCoroutineRunning)
            {
                CheckCoroutineHelperValid();
                CoroutineHelperObject.StartCoroutine(CoroutineHelperObject.RetrieveDataAfterSlotDataPullDelay());
            }
        }

        private static LoginResult LoginToArchipelago(string ip, string port, string name, string password = "")
        {
            Session = ArchipelagoSessionFactory.CreateSession(ip, int.Parse(port));
            LoginResult loginResult;

            if (password == null || password == "")
            {
                loginResult = Session.TryConnectAndLogin("Enter The Gungeon", name, ItemsHandlingFlags.IncludeOwnItems);
            }

            else
            {
                loginResult = Session.TryConnectAndLogin("Enter The Gungeon", name, ItemsHandlingFlags.IncludeOwnItems, password: password);
            }

            return loginResult;
        }

        
        private static void ParseLoginError(string ip, string port, string name, LoginResult loginResult)
        {
            LoginFailure loginFailure = (LoginFailure)loginResult;

            string text = "Failed to Connect to " + ip + ":" + port + " as " + name;
            string[] errors = loginFailure.Errors;

            foreach (string text2 in errors)
            {
                text = text + "\n    " + text2;
            }
            ConnectionRefusedError[] errorCodes = loginFailure.ErrorCodes;
            foreach (ConnectionRefusedError connectionRefusedError in errorCodes)
            {
                text += $"\n    {connectionRefusedError}";
            }

            ArchipelagoGUI.ConsoleLog(text);
            LastConnectionError = DescribeLoginFailure(ip, port, name, loginFailure);

            return;
        }

        private static string DescribeLoginFailure(string ip, string port, string name, LoginFailure failure)
        {
            foreach (ConnectionRefusedError code in failure.ErrorCodes)
            {
                switch (code)
                {
                    case ConnectionRefusedError.InvalidSlot:
                        return $"There's no player named '{name}' in this game. Slot names are case-sensitive; check your player options file.";
                    case ConnectionRefusedError.InvalidGame:
                        return $"'{name}' isn't an Enter the Gungeon slot in this game.";
                    case ConnectionRefusedError.InvalidPassword:
                        return "Wrong password, or this server needs one. Enter it in the Password box.";
                    case ConnectionRefusedError.SlotAlreadyTaken:
                        return $"Someone is already connected as '{name}'.";
                    case ConnectionRefusedError.IncompatibleVersion:
                        return "The server's Archipelago version isn't compatible with this mod.";
                    case ConnectionRefusedError.InvalidItemsHandling:
                        return "The server rejected this client's settings. Try updating the mod.";
                }
            }

            string details = string.Join(" ", failure.Errors ?? new string[0]);
            string lower = details.ToLowerInvariant();
            if (lower.Contains("timed out") || lower.Contains("timeout") || lower.Contains("refused") || lower.Contains("unable to connect")
                || lower.Contains("no such host") || lower.Contains("could not be resolved") || lower.Contains("not connect"))
            {
                return $"Couldn't reach a server at {ip}:{port}. Check the address and port, and that the server is running. " +
                    "Rooms on archipelago.gg change port when they restart.";
            }

            return $"Couldn't connect to {ip}:{port} as {name}. {details}";
        }


        private static void PullPlayerYAMLSlotData()
        {
            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Pulling slot data");

            PlayerSlotSettings = Session.DataStorage.GetSlotData();
            RoomSeed = Session.RoomState.Seed;
            SetPlayerCompletionGoalsDataFromSlotData();
            ProgressionUnlocks.LoadSlotSettings(PlayerSlotSettings);
            CheckAPWorldVersion();

            foreach (string key in PlayerSlotSettings.Keys)
            {
                ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"KEY: {key} -- VALUE: {PlayerSlotSettings[key]}");
            }


            return;
        }

        private static void SetPlayerCompletionGoalsDataFromSlotData()
        {
            // Re-read on every connect, so clear first: avoids duplicates and another seed's goals.
            ArchipelagoCompletion.ArchipelagoRequiredGoals.Clear();

            foreach (PlayerCompletionGoals possiblePlayerGoal in (PlayerCompletionGoals[])Enum.GetValues(typeof(PlayerCompletionGoals)))
            {

                // check all goals and see if player has key for goal set to true/1
                if (possiblePlayerGoal == PlayerCompletionGoals.PastsFull || possiblePlayerGoal == PlayerCompletionGoals.PastsBase)
                {
                    int chosenPlayerPastsCase = Convert.ToInt32(PlayerSlotSettings[GameCompletionGoalKeys[PlayerCompletionGoals.PastsFull]]);

                    if (chosenPlayerPastsCase == 1 && possiblePlayerGoal == PlayerCompletionGoals.PastsBase)
                    {
                        ArchipelagoCompletion.AddToCompletionGoalsToCheck(PlayerCompletionGoals.PastsBase);
                    }
                    else if (chosenPlayerPastsCase == 2 && possiblePlayerGoal == PlayerCompletionGoals.PastsFull)
                    {
                        ArchipelagoCompletion.AddToCompletionGoalsToCheck(PlayerCompletionGoals.PastsFull);
                    }
                }

                else if (Convert.ToInt32(PlayerSlotSettings[GameCompletionGoalKeys[possiblePlayerGoal]]) == 1)
                {
                    ArchipelagoCompletion.AddToCompletionGoalsToCheck(possiblePlayerGoal);
                }
            }

            return;
        }

        // TODO: implement when APWorld updates
        private static void CheckAPWorldVersion()
        {
            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Checking AP World compatability");
            if (PlayerSlotSettings.ContainsKey("APWorld"))
            {
                if ((string)PlayerSlotSettings["APWorld"] == ArchipelaGunPlugin.AP_WORLD_VERSION)
                {
                    ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, "AP World matches!");
                    return;
                }

            }

            object seedVersion;
            PlayerSlotSettings.TryGetValue("APWorld", out seedVersion);
            VersionWarning = $"This seed was made with APWorld {seedVersion ?? "(unknown)"}, but this mod expects {ArchipelaGunPlugin.AP_WORLD_VERSION}. " +
                "Some checks or items may not work. Regenerate with the matching .apworld.";

            ArchipelagoGUI.ConsoleLog("============= INCOMPATIBLE AP WORLD VERSION ==============");

            ArchipelagoGUI.ConsoleLog($"ArchipelaGun Mod looking for APWorld {ArchipelaGunPlugin.AP_WORLD_VERSION}");
            ArchipelagoGUI.ConsoleLog("Lotsa stuff will break with mismatching versions!");

            ArchipelagoGUI.ConsoleLog("============= INCOMPATIBLE AP WORLD VERSION ==============");

            return;
        }

        public static void DisconnectFromSession()
        {
            if (Session == null)
            {
                ArchipelagoGUI.ConsoleLog("Not connected to a session!");
                return;
            }
            else if (Session.Socket.Connected == false)
            {
                ArchipelagoGUI.ConsoleLog("Not connected to a session!");
                return;
            }


            ArchipelagoGUI.ConsoleLog("Disconnected from server");

            Session.Socket.Disconnect();
            Session = null;
            ConnectedSlotName = "";
            EnemyShuffleOn = false;

            EnemySwapping.ClearAllShuffleLists();

            return;
        }

        public static void ReconnectSession()
        {
            if (Session == null)
            {
                ServerConnectWithLocalData();
                //ArchipelagoGUI.ConsoleLog("Not connected to a session!");
                return;
            }
            else if (Session.Socket.Connected == false)
            {
                ServerConnectWithLocalData();
                //ArchipelagoGUI.ConsoleLog("Not connected to a session!");
                return;
            }

            ArchipelagoGUI.ConsoleLog("Cycling connection to Archipelago");

            DisconnectFromSession();
            ArchipelagoConnect(PlayerServerInfo.IP, PlayerServerInfo.Port, PlayerServerInfo.PlayerName, PlayerServerInfo.Password);

            return;

        }

        public static void ServerConnectWithLocalData()
        {
            if (ConnectionDataWriter.CheckPreviousConnectionExists() == true)
            {
                PlayerConnectionInfo localConnectData = ConnectionDataWriter.SavedConnectionSettings;
                ArchipelagoConnect(localConnectData.IP, localConnectData.Port, localConnectData.PlayerName, localConnectData.Password);
            }
            else
            {
                ArchipelagoGUI.ConsoleLog("No data for previous connection could be found!");
            }
                return;
        }

        #endregion

        #region Archipelago Initializing
        private static void BindToArchipelagoEvents()
        {
            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Binding to Session delegate events");

            // binds

            Session.Items.ItemReceived += DataReceiver.OnItemReceived;
            Session.MessageLog.OnMessageReceived += message => ArchipelagoMenu.AddLog(message.ToString());

            //Session.Socket.PacketReceived += DataReceiver.OnPacketReceived;
            //Session.MessageLog.OnMessageReceived += DataReceiver.OnMessageReceived;

            return;
        }


        // =====================================================================
        // LOCATION CHECKS =========================================================================
        // ===================================================
        private static void InitializeAPLocationChecks()
        {
            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Creating Location Checks");

            int userAchievementCase = Convert.ToInt32(PlayerSlotSettings["AchievementChecks"]);
            CountGoalManager.SetGoalList(userAchievementCase);

            // Older APWorlds, or this option off: those locations don't exist in the seed
            if (!ProgressionUnlocks.EnemyKillChecksEnabled)
            {
                CountGoalManager.RemoveStatGoals(CountStats.EnemyKills);
            }

            List<CountStats> countStatLocationChecks = CountGoalManager.GetListOfStatsWithGoals();
            foreach (CountStats stats in countStatLocationChecks)
            {
                AchievementLocationCheckHandler.SetStatLocationIDsFromGoalList(stats);
            }

            int itemLocationCheckCase = Convert.ToInt32(PlayerSlotSettings["APItemChecks"]);


            int baseChecks = APPickUpItem.GetBaseAPItemAmount(itemLocationCheckCase);
            int extraLocations = Convert.ToInt32(PlayerSlotSettings["ExtraLocations"]);
            int totalChest = baseChecks + extraLocations;

            APPickUpItem.RegisterAPItemLocations(totalChest);


            //List<long> allServerLocations = Session.Locations.AllLocations.ToList();
            //ArchipelagoGUI.ConsoleLog($"There are {allServerLocations.Count} total locations in gungeon");

            return;
        }

        // RUN START ===========================================================

        public static void CheckForSlotDataInstantiation()
        {
            if (Session == null)
            {

                return;
            }

            if (Session.Socket.Connected == false)
            {
                return;
            }

            // You can connect from the title screen or the Breach. Paradox Mode and item spawning need a player
            // in a run; the first floor load of the next run calls this again.
            if (!IsInRunWithPlayer())
            {
                ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, "Not in a run yet; run setup waits for the first floor");
                return;
            }

            RunSetupDoneThisRun = true;

            CheckToInitializeParadoxMode();

            if(!TimedServerCalls.IsDelayedItemInitCoroutineRunning)
            {
                CheckCoroutineHelperValid();
                CoroutineHelperObject.StartCoroutine(CoroutineHelperObject.HandleDelayedItemInit());
            }
            
            return;

        }

        #endregion


        #region Save Data Management

        public static void InitializeSaveData()
        {
            if (Session == null)
            {
                ArchipelagoGUI.ConsoleLog("ERROR: Not connected to Archipelago!");
                return;
            }

            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Handling save data initialization");
            SaveDataManagement.TryPreviousSaveLoad(playerInfo: PlayerServerInfo);

            return;
        }


        #endregion


        #region Item Handling

        // ITEM RETRIEVAL ===============================

        public static void ResetVariablesToStartOfRun()
        {
            ArchDebugPrint.DebugLog(DebugCategory.PluginStartup, "Resetting variables for new run");

            //SpawnedItemLog.ClearSpawnedItemLog();
            lock (itemLock)
            {
                itemsHandledThisRun.Clear();
                // Every received item is queued again by the run-start pass, so drop anything still waiting.
                item_add_queue.Clear();
            }
            IsReverseCurseSetForRun = false;
            RunSetupDoneThisRun = false;
            RandomizedByQualityItems.ResetRollCounters();
            APPickUpItem.ResetForNewRun();
            IsProgressItemsGiven = false;
            IsValidToSpawnItems = false;
            HandlingDeathlinkEvent = false;
            ProgressionUnlocks.ResetForNewRun();

            return;
        }

        // Last count the server reported for each item ID, used while offline.
        private static readonly Dictionary<long, int> lastServerItemCounts = new Dictionary<long, int>();

        /// <summary>
        /// How many copies of an item ID have been received (persists across runs). Counts from the server's
        /// list while connected, otherwise from the last server count, otherwise from the local mirror.
        /// </summary>
        public static int CountReceivedItem(long itemId)
        {
            if (Session != null && Session.Socket.Connected)
            {
                try
                {
                    int serverCount = 0;
                    foreach (ItemInfo item in Session.Items.AllItemsReceived)
                    {
                        if (item.ItemId == itemId)
                        {
                            serverCount++;
                        }
                    }

                    lastServerItemCounts[itemId] = serverCount;
                    return serverCount;
                }
                catch (InvalidOperationException)
                {
                    // List changed while counting (item arriving on the network thread); fall through.
                }
            }

            int cachedCount;
            if (lastServerItemCounts.TryGetValue(itemId, out cachedCount))
            {
                return cachedCount;
            }

            return CountUniqueLocalItems(itemId);
        }

        private static int CountUniqueLocalItems(long itemId)
        {
            int count = 0;
            lock (itemLock)
            {
                foreach (ItemInfo item in allItemsReceivedFromServer)
                {
                    if (item != null && item.ItemId == itemId)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        public static bool IsPastGoalEnabled()
        {
            string key = GameCompletionGoalKeys[PlayerCompletionGoals.PastsFull];
            return PlayerSlotSettings.ContainsKey(key) && Convert.ToInt32(PlayerSlotSettings[key]) != 0;
        }

        public static void RetrieveItemsFromServer()
        {
            TrapSpawnHandler.SetCanSpawn(false);
            ConsumableSpawnHandler.SetCanSpawn(false);

            if (Session == null)
            {
                ArchipelagoGUI.ConsoleLog("ERROR: Not connected to Archipelago!");
                TrapSpawnHandler.SetCanSpawn(true);
                ConsumableSpawnHandler.SetCanSpawn(true);
                return;
            }

            ArchipelagoGUI.ConsoleLog($"Retrieving server items!");
            SyncReceivedItemsFromServer(queueNewItems: true);

            TrapSpawnHandler.SetCanSpawn(true);
            ConsumableSpawnHandler.SetCanSpawn(true);

            return;
        }

        /// <summary>
        /// Appends any items the server has that the local list doesn't yet, matching by position in the
        /// server's list. The server resends every item on (re)connect; those are already here and are skipped.
        /// </summary>
        private static void SyncReceivedItemsFromServer(bool queueNewItems)
        {
            if (Session == null)
            {
                return;
            }

            List<ItemInfo> newItems = new List<ItemInfo>();

            lock (itemLock)
            {
                var serverItems = Session.Items.AllItemsReceived;
                for (int i = allItemsReceivedFromServer.Count; i < serverItems.Count; i++)
                {
                    allItemsReceivedFromServer.Add(serverItems[i]);
                    newItems.Add(serverItems[i]);
                }
            }

            if (queueNewItems)
            {
                foreach (ItemInfo item in newItems)
                {
                    AddItemToLocalGungeon(item);
                }
            }
        }

        private static void ResetReceivedItemsIfNewSlot(string slotName)
        {
            string slotKey = $"{Session.RoomState.Seed}:{slotName}";
            if (slotKey == receivedItemsSlotKey)
            {
                return;
            }

            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"New seed or slot, clearing received items");

            lock (itemLock)
            {
                allItemsReceivedFromServer.Clear();
                item_add_queue.Clear();
            }
            lastServerItemCounts.Clear();
            DataSender.ClearPendingChecksAndGoal();
            receivedItemsSlotKey = slotKey;
        }

        private static void RetrieveItemsFromLocalData()
        {
            TrapSpawnHandler.SetCanSpawn(false);
            ConsumableSpawnHandler.SetCanSpawn(false);

            ArchipelagoGUI.ConsoleLog($"Retrieving items based on local data!");

            List<ItemInfo> itemsToGive = new List<ItemInfo>();
            lock (itemLock)
            {
                foreach (ItemInfo item in allItemsReceivedFromServer)
                {
                    if (!itemsHandledThisRun.Contains(item.ItemId))
                    {
                        itemsToGive.Add(item);
                    }
                }
            }

            foreach (ItemInfo item in itemsToGive)
            {
                AddItemToLocalGungeon(item);
            }

            TrapSpawnHandler.SetCanSpawn(true);
            ConsumableSpawnHandler.SetCanSpawn(true);

            return;
        }

        
        public static void AddItemToLocalGungeon(ItemInfo itemInfo)
        {
            if(TrapSpawnHandler.IsSpawnValid == false)
            {
                if(itemInfo.ItemId >= 8754200 && itemInfo.ItemId < 8754300)
                {
                    ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, $"Skipping item on Retrieve command: {itemInfo.ItemName}");

                    lock (itemLock)
                    {
                        itemsHandledThisRun.Add(itemInfo.ItemId);
                    }

                    return;
                }
            }

            //GungeonControl.GiveGungeonItem(itemInfo.ItemId);

            ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, $"Receiving item: {itemInfo.ItemName}");

            lock (itemLock)
            {
                item_add_queue.Add(itemInfo.ItemId);
            }

            return;
        }

        public static void HandleDelayedItemInitialize()
        {
            // Pick up anything missed without queueing it here, then queue every received item once for this run.
            SyncReceivedItemsFromServer(queueNewItems: false);
            RetrieveItemsFromLocalData();

            CheckForProgressItems();

            IsValidToSpawnItems = true;
            PersistentUpgrades.ApplyPending();

            // Lost when save data moved to local files (b0aba54); without it Reverse Curse mode never cursed.
            // Runs after the queue is filled, so received Reverse Curse Reversals are given after the curse.
            CheckReverseCurse();
        }


        private static void CheckForProgressItems()
        {
            if (IsProgressItemsGiven)
            {
                return;
            }

            // Only once all four bullet parts are received when the Bullet Parts option is on
            ProgressionUnlocks.GiveBulletIfReady();

            if (Convert.ToInt32(PlayerSlotSettings[GameCompletionGoalKeys[PlayerCompletionGoals.AdvancedGungeon]]) == 1)
            {
                ProgressionItemSpawnHandler.GiveRatNotes();
            }

            IsProgressItemsGiven = true;
        }

        public static void TickCheckItemQueue()
        {
            TickPendingDeathlink();

            if (!IsValidToSpawnItems)
            {
                return;
            }

            long itemId;
            lock (itemLock)
            {
                if (item_add_queue.Count == 0)
                {
                    return;
                }
                itemId = item_add_queue[0];
                item_add_queue.RemoveAt(0);
            }

            ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Handling item ID: {itemId}");

            try
            {
                ArchipelagoGungeonBridge.GiveGungeonItem(itemId);
                lock (itemLock)
                {
                    itemsHandledThisRun.Add(itemId);
                }
            }
            catch (Exception ex)
            {
                ArchipelagoGUI.ConsoleLog("Exception caused during handling giving Archipelago item. Please contact dev: " + ex);
            }

            return;
        }

        #endregion


        #region Deathlink
        // DEATHLINK =====================

        private static void TickPendingDeathlink()
        {
            string cause = pendingDeathlinkCause;
            if (cause == null)
            {
                return;
            }
            pendingDeathlinkCause = null;

            // Nothing to kill outside a run
            if (GameManager.Instance == null || GameManager.Instance.IsFoyer || GungeonPlayerEventListener.GetFirstAlivePlayer() == null)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, "Deathlink received outside a run, ignoring");
                HandlingDeathlinkEvent = false;
                return;
            }

            try
            {
                ArchipelagoGungeonBridge.DeathlinkKillPlayer(cause);
            }
            catch (Exception ex)
            {
                HandlingDeathlinkEvent = false;
                ArchipelagoGUI.ConsoleLog("Exception while handling Deathlink: " + ex.Message);
            }
        }

        private static void CheckToCreateDeathlink()
        {
            // deathlink
            if (Convert.ToInt32(PlayerSlotSettings["DeathLink"]) == 1)
            {
                InitializeDeathlink(true);
                ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, "Deathlink ON");
            }
            else
            {
                InitializeDeathlink(false);
                ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, "Deathlink off");
            }
        }

        private static void InitializeDeathlink(bool isDeathlinkEnabled)
        {
            DeathLinkService = Session.CreateDeathLinkService();
            IsDeathLinkEnabled = isDeathlinkEnabled;

            if (isDeathlinkEnabled)
            {
                DeathLinkService.EnableDeathLink();
            }

            DeathLinkService.OnDeathLinkReceived += DataReceiver.OnDeathlink;

            return;
        }

        public static void SetDeathLinkMode(int modeToSet)
        {
            if (DeathLinkService == null || Session == null)
            {
                ArchipelagoGUI.ConsoleLog("Not connected to a session!");
                return;
            }

            switch (modeToSet)
            {
                case 0:
                    ArchipelagoGUI.ConsoleLog("Deathlink has been DISABLED");
                    DeathLinkService.DisableDeathLink();
                    IsDeathLinkEnabled = false;
                    return;
                case 1:
                case 2:
                    ArchipelagoGUI.ConsoleLog("Deathlink has been ENABLED");
                    DeathLinkService.EnableDeathLink();
                    IsDeathLinkEnabled = true;
                    return;
                default:
                    ArchipelagoGUI.ConsoleLog("Invalid deathlink mode setting!");
                    return;
            }
        }
        #endregion

        #region Enemies
        // ENEMIES =========================================================

        private static void CheckToRandomizeEnemies()
        {

            if (Convert.ToInt32(PlayerSlotSettings["RandomEnemies"]) == 1)
            {
                InitializeEnemySwapper();
                EnemyShuffleOn = true;
                ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, "Random enemies");
            }
            else
            {
                EnemyShuffleOn = false;
                ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, "Enemies not randomized");
            }
            return;
        }


        // ENEMY SWAPPER ==========================================


        private static void InitializeEnemySwapper()
        {
            System.Security.Cryptography.MD5 md5Hasher = System.Security.Cryptography.MD5.Create();
            var hashed = md5Hasher.ComputeHash(Encoding.UTF8.GetBytes(RoomSeed));
            int seed = BitConverter.ToInt32(hashed, 0);

            EnemySwapping.MakeNormalShuffleEnemies(seed);
            return;
        }

        public static void SetEnemyShuffleMode(int modeEntry)
        {
            if (Session == null)
            {
                ArchipelagoGUI.ConsoleLog("Not connected to a session!");
                return;
            }

            if (modeEntry == 0)
            {
                ArchipelagoGUI.ConsoleLog("Enemy shuffle has been DISABLED");
                EnemySwapping.ClearAllShuffleLists();
                EnemyShuffleOn = false;
            }
            else if (modeEntry == 1)
            {
                ArchipelagoGUI.ConsoleLog("Enemy shuffle has been ENABLED");
                InitializeEnemySwapper();
                EnemyShuffleOn = true;
            }
            else
            {
                ArchipelagoGUI.ConsoleLog("Invalid enemy shuffle mode setting!");
            }
            return;
        }

        #endregion

        #region Paradox Mode
        // PARADOX MODE =========================================================

        private static bool CheckToInitializeParadoxMode()
        {

            if (Convert.ToInt32(PlayerSlotSettings["Paradox"]) == 1)
            {
                PlayerController playerController = GungeonPlayerEventListener.GetFirstAlivePlayer();
                if (playerController == null)
                {
                    return false;
                }

                //ETGModConsole.Instance?.ParseCommand("character paradox");
                //LootEngine.SpawnItem(archipelItem, playerController.CenterPosition, Vector2.zero, 0);

                CharSwap.StartParadoxMode(playerController);

                return true;
            }

            return false;
        }

        #endregion

        #region Reverse Curse

        // REVERSE CURSE =================================================

        private static bool IsReverseCurseSetForRun { get; set; } = false;
        private static void CheckReverseCurse()
        {
            if(IsReverseCurseSetForRun)
            {
                return;
            }

            // Not in the Breach: the curse belongs on the character that's actually in the run
            if (GameManager.Instance == null || GameManager.Instance.IsFoyer || GungeonPlayerEventListener.GetFirstAlivePlayer() == null)
            {
                return;
            }

            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Checking for reverse curse");
            int reverseCurseModeValue = Convert.ToInt32(PlayerSlotSettings["ReverseCurse"]);


            // Check for past kills
            if (reverseCurseModeValue == 1 && CountGoalManager.GetCountStat(CountStats.PastKills) > 0)
            {
                ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Giving curse items after past check");
                ArchipelagoGungeonBridge.GiveReverseCurse(8);
            }
            else if (reverseCurseModeValue == 2)
            {
                ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState, $"Giving curse items without past check");
                ArchipelagoGungeonBridge.GiveReverseCurse(8);
            }

            IsReverseCurseSetForRun = true;

            return;
        }

        #endregion


        #region User Output
        // USER OUTPUT HELPER ===============================

        public static void OutputGameGoalStatus()
        {
            // TODO have a separate game progress text window
            if (Session == null)
            {
                ArchipelagoGUI.ConsoleLog("ERROR: Not connected to Archipelago!");
                return;
            }

            DataSender.CheckForGameCompletion();

            return;
        }

        public static void ShowGoalsTextbox()
        {
            if(IsGoalsTextBoxOpen || GungeonPlayerEventListener.GetFirstAlivePlayer() == null)
            {
                return;
            }

            List<string> completionList = ArchipelagoCompletion.GetAllUnmetCountsForGoals();
            List<string> locationList = CountGoalManager.GetFormattedListOfRemainingGoals();

            string completionListAsString = String.Join(", ", completionList.ToArray());
            string locationListAsString = String.Join(", ", locationList.ToArray());

            string textboxOutput = $"To kill: {completionListAsString} \n\n" +
                $"Location checks: {locationListAsString}";

            TextBoxHandler.ShowArchipelagoLetterBox(GungeonPlayerEventListener.GetFirstAlivePlayer(), textboxOutput);
            IsGoalsTextBoxOpen = true;

            return;

        }

        public static void HideGoalsTextbox()
        {
            IsGoalsTextBoxOpen = false;
            TextBoxHandler.ClearAllTextboxes();
            return;
        }

        #endregion


        #region Server Communication

        // ============================ DATA SENDER ==========================================

        public class DataSender
        {

            #region High Priority Send Functions
            // High priority generally meaning, randomizer hard stops if these fail communication
            // Checks and goal completion found while disconnected, sent on the next connect to the same slot.
            // Before this they threw (Session was null) inside the game code that found them, and were lost.
            private static readonly List<long> pendingLocationChecks = new List<long>();
            private static bool isGoalCompletionPending = false;

            private static bool IsConnected
            {
                get { return Session != null && Session.Socket.Connected; }
            }

            public static void SendFoundLocationCheck(long idToSend)
            {
                SendFoundLocationCheck(new long[] { idToSend });
                return;
            }

            public static void SendFoundLocationCheck(long[] idArray)
            {
                if (idArray == null || idArray.Length == 0)
                {
                    return;
                }

                if (IsConnected)
                {
                    try
                    {
                        Session.Locations.CompleteLocationChecks(idArray);
                        return;
                    }
                    catch (Exception ex)
                    {
                        ArchDebugPrint.DebugLog(DebugCategory.ServerSend, $"Sending checks failed, keeping them for later: {ex.Message}");
                    }
                }

                lock (pendingLocationChecks)
                {
                    pendingLocationChecks.AddRange(idArray);
                }
                ArchipelagoGUI.ConsoleLog($"Not connected: {idArray.Length} check(s) will be sent when you reconnect");
                return;
            }

            public static void SendPendingChecksAndGoal()
            {
                if (!IsConnected)
                {
                    return;
                }

                long[] toSend;
                lock (pendingLocationChecks)
                {
                    toSend = pendingLocationChecks.ToArray();
                    pendingLocationChecks.Clear();
                }

                if (toSend.Length > 0)
                {
                    ArchipelagoGUI.ConsoleLog($"Sending {toSend.Length} check(s) found while disconnected");
                    SendFoundLocationCheck(toSend);
                }

                if (isGoalCompletionPending)
                {
                    isGoalCompletionPending = false;
                    SendGameCompletion();
                }
            }

            public static void ClearPendingChecksAndGoal()
            {
                lock (pendingLocationChecks)
                {
                    pendingLocationChecks.Clear();
                }
                isGoalCompletionPending = false;
            }

            public static void SendDeathlink(string playerName = "Gungeoneer", string causeOfDeath = "Died to Gungeon")
            {
                if (DeathLinkService == null || Session == null)
                {
                    ArchipelagoGUI.ConsoleLog("Tried to send Deathlink but not connected!");
                    return;
                }

                if (!IsDeathLinkEnabled || !(Session.Socket.Connected))
                {
                    return;
                }

                if(HandlingDeathlinkEvent)
                {
                    return;
                }

                HandlingDeathlinkEvent = true;
                ArchDebugPrint.DebugLog(DebugCategory.ServerSend, $"Sending deathlink");

                string deathName = PlayerServerInfo.PlayerName;

                DeathLinkService.SendDeathLink(new DeathLink(deathName, causeOfDeath));
                return;
            }

            public static void CheckForGameCompletion()
            {
                ArchDebugPrint.DebugLog(DebugCategory.GameCompletion, "Checking for game completion");

                List<string> unmetGoalStatCounts = ArchipelagoCompletion.GetAllUnmetCountsForGoals();

                if (unmetGoalStatCounts.Count > 0)
                {
                    ArchDebugPrint.DebugLog(DebugCategory.GameCompletion, $"Remaining goals: ");

                    foreach(string goal in unmetGoalStatCounts)
                    {
                        ArchDebugPrint.DebugLog(DebugCategory.GameCompletion, goal);
                    }

                    return;
                }

                ArchDebugPrint.DebugLog(DebugCategory.GameCompletion, $"Goal checks passed! Sending completion event");

                SendGameCompletion();
                return;
            }

            private static void SendGameCompletion()
            {
                if (!IsConnected)
                {
                    isGoalCompletionPending = true;
                    ArchipelagoGUI.ConsoleLog("Goal complete! It will be sent when you reconnect");
                    return;
                }

                StatusUpdatePacket statusUpdatePacket = new StatusUpdatePacket();
                statusUpdatePacket.Status = ArchipelagoClientState.ClientGoal;
                Session.Socket.SendPacket(statusUpdatePacket);
                return;
            }

            #endregion

     

            #region Low Priority Send Functions
            public static void SendChatMessage(string message)
            {
                Session.Socket.SendPacketAsync(new SayPacket
                {
                    Text = message
                });
            }

            public static void ScoutFoundLocationCheck(long locationID)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, $"Scouting location check: {locationID}");

                Session.Locations.ScoutLocationsAsync(locationInfoPacket =>
                {
                    foreach (long key in locationInfoPacket.Keys)
                    {
                        ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, $"Item ID: {locationInfoPacket[key].ItemId} Item: {locationInfoPacket[key].Player}'s + {locationInfoPacket[key].ItemName} from {locationInfoPacket[key].ItemGame}");
                    }
                },
                locationID);
                return;
            }
            #endregion

        }

        // ============================ DATA RECEIVER ==========================================

        public class DataReceiver
        {
            #region High Priority Receive Calls

            public static void OnDeathlink(DeathLink deathLink)
            {
                if(HandlingDeathlinkEvent)
                {
                    return;
                }
                HandlingDeathlinkEvent = true;
                ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, $"Received DeathLink");

                // This runs on the network thread; the kill happens on the game thread in TickCheckItemQueue
                pendingDeathlinkCause = $"Deathlink by {deathLink.Source}";
                return;
            }


            public static void OnItemReceived(ReceivedItemsHelper helper)
            {
                ItemInfo itemInfo = helper.PeekItem();
                ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, $"OnItemReceived - {itemInfo.ItemId} -- {itemInfo.ItemName}");

                // Adds and queues only items not already received. On (re)connect the server resends
                // everything, and those are skipped instead of being given a second time.
                SyncReceivedItemsFromServer(queueNewItems: true);

                helper.DequeueItem();

                return;
            }

            #endregion


            #region Low Priority Receive Calls
            public static void OnPacketReceived(ArchipelagoPacketBase packet)
            {

                //ArchipelagoGUI.ConsoleLog(packet);

                /*
                if (packet is ItemPrintJsonPacket)
                {
                    ItemPrintJsonPacket itemPrintJsonPacket = (ItemPrintJsonPacket)packet;
                    if (itemPrintJsonPacket.ReceivingPlayer == session.ConnectionInfo.Slot || session.Locations.AllLocations.Contains(itemPrintJsonPacket.Item.Location))
                    {
                        string game = session.Players.Players[session.ConnectionInfo.Team][itemPrintJsonPacket.Item.Player].Game;
                        string game2 = session.Players.Players[session.ConnectionInfo.Team][itemPrintJsonPacket.ReceivingPlayer].Game;
                        ArchipelagoPickupNotification(session.Players.GetPlayerName(itemPrintJsonPacket.ReceivingPlayer) + " got " + session.Items.GetItemName(itemPrintJsonPacket.Item.Item, game2), "from " + session.Locations.GetLocationNameFromId(itemPrintJsonPacket.Item.Location, game));
                    }
                }
                */
            }

            public static void OnMessageReceived(LogMessage message)
            {
                ArchipelagoGUI.ConsoleLog(message.ToString());
            }
            #endregion
        }

        #endregion

        #region Session Coroutine Logic
        private static TimedServerCalls CoroutineHelperObject { get; set; }
        private static void CheckCoroutineHelperValid()
        {
            if (CoroutineHelperObject == null)
            {
                var tempgameObject = new GameObject();
                CoroutineHelperObject = tempgameObject.AddComponent<TimedServerCalls>();
            }

            return;
        }

        

        #endregion
    }

    #region Coroutine Handlers

    public class TimedServerCalls:MonoBehaviour
    {
        public static bool IsRetrieveDataCoroutineRunning { get; private set; } = false;

        public IEnumerator RetrieveDataAfterSlotDataPullDelay(float waitTime = 1.0f)
        {
            IsRetrieveDataCoroutineRunning = true;
            ArchipelagoGUI.ConsoleLog($"Waiting for Archipelago data, please standby ============== ");

            yield return new WaitForSeconds(waitTime);

            SessionHandler.InitializeSaveData();

            SessionHandler.CheckForSlotDataInstantiation();
            SaveDataManagement.CheckFullCountStatsForGoals();

            IsRetrieveDataCoroutineRunning = false;

        }

        public static bool IsDelayedItemInitCoroutineRunning { get; private set; } = false;

        public IEnumerator HandleDelayedItemInit(float waitTime = 2f)
        {
            IsDelayedItemInitCoroutineRunning = true;
            ArchipelagoGUI.ConsoleLog("Initializing items after initial setup, please standby =========");
            yield return new WaitForSeconds(waitTime);

            SessionHandler.HandleDelayedItemInitialize();
            // The goals box used to open here and was dismissed by switching off the Archipelagun; the menu's
            // Tracker tab shows the same information now.
            IsDelayedItemInitCoroutineRunning = false;
        }

        
    }

    #endregion
}
