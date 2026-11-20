using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.GungeonEventHandlers;
using ArchiGungeon.UserInterface;
using ArchiGungeon.DebugTools;

namespace ArchiGungeon.ItemArchipelago
{
    /// <summary>
    /// Persistent unlocks from the APWorld's progression options: Progressive Chamber Access (elevator locks),
    /// Bullet That Can Kill The Past parts, and Sell Creep's Favor. Unlock state is derived from every item
    /// received from the server, so it survives death and new runs.
    /// </summary>
    public static class ProgressionUnlocks
    {
        // Item IDs must match worlds/enter_the_gungeon/Items.py
        public const long ChamberAccessID = 8754320;
        public const long SellCreepsFavorID = 8754321;
        public static readonly long[] BulletPartIDs = { 8754310, 8754311, 8754312, 8754313 };
        public const long OldCrestID = 8754302;

        public const int MaxChamberAccess = 4;

        // Slot data settings. All default to off so older APWorlds behave exactly as before.
        public static bool ChamberLocksEnabled { get; private set; }
        public static bool BulletPartsEnabled { get; private set; }
        public static bool SellCreepsFavorEnabled { get; private set; }

        private static bool bulletGivenThisRun = false;
        private static bool oldCrestGivenThisRun = false;

        // Scene name of the Gungeon Proper (Chamber 2), where the Old Crest's altar is.
        private const string GungeonProperScene = "tt5";

        #region Setup

        private static Hook elevatorDepartureHook;

        public static void InitHooks()
        {
            elevatorDepartureHook = new Hook(
                typeof(ElevatorDepartureController).GetMethod("DoDeparture", BindingFlags.Instance | BindingFlags.Public),
                typeof(ProgressionUnlocks).GetMethod("DoDepartureHook", BindingFlags.Static | BindingFlags.Public)
            );
        }

        public static void LoadSlotSettings(Dictionary<string, object> slotData)
        {
            ChamberLocksEnabled = ReadFlag(slotData, "ChamberAccessLocks");
            BulletPartsEnabled = ReadFlag(slotData, "BulletParts");
            SellCreepsFavorEnabled = ReadFlag(slotData, "SellCreepsFavor");

            ArchDebugPrint.DebugLog(DebugCategory.InitializingGameState,
                $"Progression options - chamber locks: {ChamberLocksEnabled}, bullet parts: {BulletPartsEnabled}, sell creep's favor: {SellCreepsFavorEnabled}");
        }

        private static bool ReadFlag(Dictionary<string, object> slotData, string key)
        {
            return slotData.ContainsKey(key) && Convert.ToInt32(slotData[key]) != 0;
        }

        public static void ResetForNewRun()
        {
            bulletGivenThisRun = false;
            oldCrestGivenThisRun = false;
        }

        #endregion

        #region Unlock state

        public static int ChamberAccessCount
        {
            get { return Math.Min(SessionHandler.CountReceivedItem(ChamberAccessID), MaxChamberAccess); }
        }

        public static bool HasAllBulletParts
        {
            get
            {
                foreach (long partID in BulletPartIDs)
                {
                    if (SessionHandler.CountReceivedItem(partID) == 0)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        public static int BulletPartCount
        {
            get
            {
                int count = 0;
                foreach (long partID in BulletPartIDs)
                {
                    if (SessionHandler.CountReceivedItem(partID) > 0)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>True when the Bullet That Can Kill The Past is allowed this run.</summary>
        public static bool CanHaveBullet
        {
            get { return !BulletPartsEnabled || HasAllBulletParts; }
        }

        /// <summary>True when the Sell Creep may open the grate to the R&amp;G Dept.</summary>
        public static bool CanOpenRGDept
        {
            get
            {
                bool hasFavor = !SellCreepsFavorEnabled || SessionHandler.CountReceivedItem(SellCreepsFavorID) > 0;
                // The R&G Dept. drops you straight into the Forge, skipping its elevator, so it also needs Forge access.
                bool hasForgeAccess = !ChamberLocksEnabled || ChamberAccessCount >= MaxChamberAccess;
                return hasFavor && hasForgeAccess;
            }
        }

        #endregion

        #region Receiving items

        /// <summary>Handles item IDs owned by this class. Returns true if the ID was handled.</summary>
        public static bool HandleReceivedItem(long itemID)
        {
            if (itemID == ChamberAccessID)
            {
                ShowMessage($"Chamber access {ChamberAccessCount}/{MaxChamberAccess}");
                return true;
            }

            if (itemID == SellCreepsFavorID)
            {
                ShowMessage("The Sell Creep owes you a favor");
                return true;
            }

            if (Array.IndexOf(BulletPartIDs, itemID) >= 0)
            {
                ShowMessage($"Bullet part {BulletPartCount}/{BulletPartIDs.Length}");
                GiveBulletIfReady();
                return true;
            }

            if (itemID == OldCrestID)
            {
                // Given on arrival at the Gungeon Proper instead of at run start, so it doesn't have to be
                // carried through the Keep without taking damage.
                GiveOldCrestIfOnGungeonProper();
                return true;
            }

            return false;
        }

        /// <summary>Gives the Bullet That Can Kill The Past once per run, if the pasts goal wants it and it's unlocked.</summary>
        public static void GiveBulletIfReady()
        {
            if (bulletGivenThisRun || !SessionHandler.IsPastGoalEnabled() || !CanHaveBullet)
            {
                return;
            }

            ProgressionItemSpawnHandler.GivePastBullet();
            bulletGivenThisRun = true;
        }

        public static void OnFloorLoaded(string sceneName)
        {
            if (sceneName == GungeonProperScene)
            {
                GiveOldCrestIfOnGungeonProper();
            }
        }

        private static void GiveOldCrestIfOnGungeonProper()
        {
            if (oldCrestGivenThisRun || SessionHandler.CountReceivedItem(OldCrestID) == 0)
            {
                return;
            }

            GameLevelDefinition levelDef = GameManager.Instance.GetLastLoadedLevelDefinition();
            if (levelDef == null || levelDef.dungeonSceneName != GungeonProperScene)
            {
                return;
            }

            GungeonPlayerEventListener.GetFirstAlivePlayer().GiveItem("old_crest");
            oldCrestGivenThisRun = true;
        }

        #endregion

        #region Elevator lock

        /// <summary>
        /// Chamber Access needed to take the elevator out of the current floor, keyed by tileset ID
        /// (GlobalDungeonData.ValidTilesets). Leaving for Chamber N needs N - 1 Chamber Access.
        /// </summary>
        private static readonly Dictionary<int, int> accessNeededToLeave = new Dictionary<int, int>()
        {
            { 2, 1 },      // Keep of the Lead Lord -> Gungeon Proper
            { 4, 1 },      // Oubliette -> Gungeon Proper
            { 1, 2 },      // Gungeon Proper -> Black Powder Mine
            { 8, 2 },      // Abbey of the True Gun -> Black Powder Mine
            { 16, 3 },     // Black Powder Mine -> Hollow
            { 32768, 3 },  // Resourceful Rat's Lair -> Hollow
            { 32, 4 },     // Hollow -> Forge
            { 2048, 4 },   // R&G Dept. -> Forge
        };

        public static int AccessNeededToLeaveCurrentFloor()
        {
            if (!ChamberLocksEnabled || GameManager.Instance.Dungeon == null)
            {
                return 0;
            }

            int tileset = (int)GameManager.Instance.Dungeon.tileIndices.tilesetId;
            int needed;
            return accessNeededToLeave.TryGetValue(tileset, out needed) ? needed : 0;
        }

        public static void DoDepartureHook(Action<ElevatorDepartureController> orig, ElevatorDepartureController self)
        {
            int needed = AccessNeededToLeaveCurrentFloor();

            if (ChamberAccessCount < needed)
            {
                ArchDebugPrint.DebugLog(DebugCategory.PlayerEventListener, $"Elevator locked: need {needed} chamber access, have {ChamberAccessCount}");
                ShowMessage($"Elevator locked - Chamber access {ChamberAccessCount}/{needed}");
                return;
            }

            orig(self);
        }

        #endregion

        private static void ShowMessage(string text)
        {
            ArchipelagoGUI.ConsoleLog(text);

            PlayerController player = GungeonPlayerEventListener.GetFirstAlivePlayer();
            if (player != null)
            {
                TextBoxHandler.ShowItemEventTextBox(player, text, 3f);
            }
        }
    }
}
