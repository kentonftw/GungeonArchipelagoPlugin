using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.GungeonEventHandlers;
using ArchiGungeon.UserInterface;
using ArchiGungeon.DebugTools;
using ArchiGungeon.Data;

namespace ArchiGungeon.ItemArchipelago
{
    /// <summary>
    /// Persistent unlocks from the APWorld's progression options: Progressive Chamber Access (elevator locks),
    /// Bullet That Can Kill The Past parts, and Sell Creep's Favor. Also re-times the Old Crest. Unlock state is derived from every item
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
        public static bool AllowGlitchChestSkips { get; private set; }
        public static bool AllowTeleporterSkips { get; private set; }
        public static bool BossChecksEnabled { get; private set; }
        public static bool EnemyKillChecksEnabled { get; private set; }
        public static bool PersistentUpgradesEnabled { get; private set; }

        private static bool bulletGivenThisRun = false;

        // Every received item is re-handled at the start of each run, so only announce unlocks that are new.
        private static int announcedChamberAccess = 0;
        private static int announcedBulletParts = 0;
        private static bool announcedFavor = false;
        private static bool oldCrestGivenThisRun = false;

        // Scene name of the Gungeon Proper (Chamber 2), where the Old Crest's altar is.
        private const string GungeonProperScene = "tt5";

        #region Setup

        private static readonly List<Hook> hooks = new List<Hook>();

        public static void InitHooks()
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // Elevator out of a floor, and the "cryo" elevator (save and quit, resumes on the next floor)
            AddHook(typeof(ElevatorDepartureController).GetMethod("DoDeparture", instance), "DoDepartureHook");
            AddHook(typeof(ElevatorDepartureController).GetMethod("DoCryoDeparture", instance), "DoCryoDepartureHook");

            // Breach shortcut elevator: hide shortcuts to chambers that aren't unlocked yet
            AddHook(typeof(ShortcutElevatorController).GetMethod("DetermineAvailableShortcuts", instance), "DetermineAvailableShortcutsHook");
            AddHook(typeof(ShortcutElevatorController).GetMethod("GetCachedAvailableShortcut", instance), "GetCachedAvailableShortcutHook");

            // Sell Creep's grate to the R&G Dept.
            AddHook(typeof(SellCellController).GetMethod("HandleSellPitOpening", instance), "HandleSellPitOpeningHook");

            // Blacksmith / Ark: report the Bullet as built only once all four AP parts are received
            AddHook(typeof(GameStatsManager).GetMethod("GetFlag", instance, null, new Type[] { typeof(GungeonFlags) }, null), "GetFlagHook");

            // Floor skips that go around the elevator (optional, see AllowGlitchChestSkips / AllowTeleporterSkips)
            AddHook(typeof(Chest).GetMethod("Open", instance, null, new Type[] { typeof(PlayerController) }, null), "ChestOpenHook");
            AddHook(typeof(TeleporterPrototypeItem).GetMethod("DoEffect", instance, null, new Type[] { typeof(PlayerController) }, null), "TeleporterDoEffectHook");
        }

        private static void AddHook(MethodInfo target, string hookName)
        {
            if (target == null)
            {
                ArchDebugPrint.DebugLog(DebugCategory.PluginStartup, $"Could not find game method for {hookName}, skipping hook");
                return;
            }

            hooks.Add(new Hook(target, typeof(ProgressionUnlocks).GetMethod(hookName, BindingFlags.Static | BindingFlags.Public)));
        }

        public static void LoadSlotSettings(Dictionary<string, object> slotData)
        {
            ChamberLocksEnabled = ReadFlag(slotData, "ChamberAccessLocks");
            BulletPartsEnabled = ReadFlag(slotData, "BulletParts");
            SellCreepsFavorEnabled = ReadFlag(slotData, "SellCreepsFavor");
            AllowGlitchChestSkips = ReadFlag(slotData, "AllowGlitchChestSkips");
            AllowTeleporterSkips = ReadFlag(slotData, "AllowTeleporterSkips");
            BossChecksEnabled = ReadFlag(slotData, "BossChecks");
            EnemyKillChecksEnabled = ReadFlag(slotData, "EnemyKillChecks");
            PersistentUpgradesEnabled = ReadFlag(slotData, "PersistentUpgrades");

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
            PersistentUpgrades.ResetForNewRun();
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
                if (ChamberAccessCount > announcedChamberAccess)
                {
                    announcedChamberAccess = ChamberAccessCount;
                    ShowMessage($"Chamber access {ChamberAccessCount}/{MaxChamberAccess}");
                }
                return true;
            }

            if (itemID == SellCreepsFavorID)
            {
                if (!announcedFavor)
                {
                    announcedFavor = true;
                    ShowMessage("The Sell Creep owes you a favor");
                }
                return true;
            }

            if (Array.IndexOf(BulletPartIDs, itemID) >= 0)
            {
                if (BulletPartCount > announcedBulletParts)
                {
                    announcedBulletParts = BulletPartCount;
                    ShowMessage($"Bullet part {BulletPartCount}/{BulletPartIDs.Length}");
                }
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

        public static void DoCryoDepartureHook(Action<ElevatorDepartureController, bool> orig, ElevatorDepartureController self, bool playerless)
        {
            int needed = AccessNeededToLeaveCurrentFloor();

            if (!playerless && ChamberAccessCount < needed)
            {
                ShowMessage($"Elevator locked - Chamber access {ChamberAccessCount}/{needed}");
                return;
            }

            orig(self, playerless);
        }

        /// <summary>Chamber number a Breach shortcut starts on, or 0 if it isn't a main chamber.</summary>
        private static readonly Dictionary<string, int> shortcutChamber = new Dictionary<string, int>()
        {
            { "tt5", 2 },
            { "tt_mines", 3 },
            { "tt_catacombs", 4 },
            { "tt_forge", 5 },
        };

        private static readonly FieldInfo availableShortcutsField =
            typeof(ShortcutElevatorController).GetField("availableShortcuts", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo selectedShortcutField =
            typeof(ShortcutElevatorController).GetField("m_selectedShortcutIndex", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void DetermineAvailableShortcutsHook(Action<ShortcutElevatorController> orig, ShortcutElevatorController self)
        {
            orig(self);

            if (!ChamberLocksEnabled || availableShortcutsField == null)
            {
                return;
            }

            List<ShortcutDefinition> shortcuts = availableShortcutsField.GetValue(self) as List<ShortcutDefinition>;
            if (shortcuts == null)
            {
                return;
            }

            int access = ChamberAccessCount;
            shortcuts.RemoveAll(shortcut =>
            {
                int chamber;
                return shortcut.targetLevelName != null
                    && shortcutChamber.TryGetValue(shortcut.targetLevelName, out chamber)
                    && chamber - 1 > access;
            });

            if (selectedShortcutField != null)
            {
                int selected = (int)selectedShortcutField.GetValue(self);
                selectedShortcutField.SetValue(self, ClampIndex(selected, shortcuts.Count));
            }
        }

        public static int GetCachedAvailableShortcutHook(Func<ShortcutElevatorController, int> orig, ShortcutElevatorController self)
        {
            int cached = orig(self);

            List<ShortcutDefinition> shortcuts = availableShortcutsField == null ? null
                : availableShortcutsField.GetValue(self) as List<ShortcutDefinition>;

            return shortcuts == null ? cached : ClampIndex(cached, shortcuts.Count);
        }

        private static int ClampIndex(int index, int count)
        {
            if (count <= 0 || index < 0)
            {
                return 0;
            }
            return Math.Min(index, count - 1);
        }

        #endregion

        #region Glitch chests and Teleporter Prototype

        private static readonly FieldInfo glitchChestField =
            typeof(Chest).GetField("m_isGlitchChest", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool IsNextFloorLocked()
        {
            return ChamberAccessCount < AccessNeededToLeaveCurrentFloor();
        }

        public static void ChestOpenHook(Action<Chest, PlayerController> orig, Chest self, PlayerController player)
        {
            // A Glitch Chest loads the next floor instead of opening. While that floor is locked, open it as a normal chest.
            if (!AllowGlitchChestSkips && glitchChestField != null && player != null
                && (bool)glitchChestField.GetValue(self) && IsNextFloorLocked())
            {
                glitchChestField.SetValue(self, false);
                ShowMessage("Glitch chest can't skip a locked chamber");
            }

            orig(self, player);
        }

        public static void TeleporterDoEffectHook(Action<TeleporterPrototypeItem, PlayerController> orig, TeleporterPrototypeItem self, PlayerController user)
        {
            if (AllowTeleporterSkips || GameManager.Instance.Dungeon == null)
            {
                orig(self, user);
                return;
            }

            float nextFloorChance = self.ChanceToGoToNextFloor;
            float secretFloorChance = self.ChanceToGoToSecretFloor;

            if (IsNextFloorLocked())
            {
                self.ChanceToGoToNextFloor = 0f;
            }

            // From the Gungeon Proper the secret-floor warp goes to the Abbey, which normally needs the Old Crest.
            // (From the Keep it goes to the Oubliette, which is always open.)
            bool onGungeonProper = (int)GameManager.Instance.Dungeon.tileIndices.tilesetId == 1;
            if (onGungeonProper && SessionHandler.CountReceivedItem(OldCrestID) == 0)
            {
                self.ChanceToGoToSecretFloor = 0f;
            }

            try
            {
                orig(self, user);
            }
            finally
            {
                self.ChanceToGoToNextFloor = nextFloorChance;
                self.ChanceToGoToSecretFloor = secretFloorChance;
            }
        }

        #endregion

        #region Sell Creep and Blacksmith

        public static IEnumerator HandleSellPitOpeningHook(Func<SellCellController, IEnumerator> orig, SellCellController self)
        {
            if (!CanOpenRGDept)
            {
                // Selling more items re-checks, so the grate opens once the favor (and Forge access) arrives.
                ShowMessage(SellCreepsFavorEnabled && SessionHandler.CountReceivedItem(SellCreepsFavorID) == 0
                    ? "The Sell Creep wants a favor first"
                    : $"R&G Dept. needs Chamber access {MaxChamberAccess}");
                return EmptyCoroutine();
            }

            return orig(self);
        }

        private static IEnumerator EmptyCoroutine()
        {
            yield break;
        }

        private static readonly GungeonFlags[] blacksmithElementFlags =
        {
            GungeonFlags.BLACKSMITH_ELEMENT1,
            GungeonFlags.BLACKSMITH_ELEMENT2,
            GungeonFlags.BLACKSMITH_ELEMENT3,
            GungeonFlags.BLACKSMITH_ELEMENT4,
        };

        public static bool GetFlagHook(Func<GameStatsManager, GungeonFlags, bool> orig, GameStatsManager self, GungeonFlags flag)
        {
            if (BulletPartsEnabled)
            {
                if (flag == GungeonFlags.BLACKSMITH_BULLET_COMPLETE)
                {
                    return HasAllBulletParts;
                }

                int element = Array.IndexOf(blacksmithElementFlags, flag);
                if (element >= 0)
                {
                    return SessionHandler.CountReceivedItem(BulletPartIDs[element]) > 0;
                }
            }

            return orig(self, flag);
        }

        #endregion

        private const float MessageDuration = 3f;
        private static string lastMessage = null;
        private static float lastMessageTime = -100f;

        // The departure hooks fire every frame while you stand in a locked elevator. Re-showing the box each
        // time restarted its typing animation, so it stayed blank; show it once per lock and skip the typing.
        private static void ShowMessage(string text)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (text == lastMessage && now - lastMessageTime < MessageDuration)
            {
                return;
            }
            lastMessage = text;
            lastMessageTime = now;

            ArchipelagoGUI.ConsoleLog(text);

            PlayerController player = GungeonPlayerEventListener.GetFirstAlivePlayer();
            if (player != null)
            {
                TextBoxHandler.ShowItemEventTextBox(player, text, MessageDuration, instant: true);
            }
        }
    }
}
