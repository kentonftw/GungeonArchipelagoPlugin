using System;
using System.Collections.Generic;
using System.Reflection;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.Data;
using ArchiGungeon.DebugTools;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace ArchiGungeon.ItemArchipelago
{
    /// <summary>
    /// Master Round, shrine and NPC rescue checks, plus the Boss Hunt and Token Hunt goals.
    /// Location IDs match the APWorld (Locations.py).
    /// </summary>
    public static class ExtraChecks
    {
        public const long MasterRoundStartID = 8756100;
        public const long ShrineStartID = 8756200;
        public const long NPCRescueStartID = 8756300;
        public const long TokenID = 8754700;

        public static bool MasterRoundChecksEnabled { get; private set; }
        public static bool ShrineChecksEnabled { get; private set; }
        public static bool NPCRescueChecksEnabled { get; private set; }
        public static int ShopAPItemRate { get; private set; } = 3;
        public static int BossHuntCount { get; private set; }
        public static int TokensRequired { get; private set; }

        private static readonly List<Hook> hooks = new List<Hook>();
        private static readonly HashSet<long> sentThisSession = new HashSet<long>();
        private static float nextPoll = 0f;

        public static void LoadSlotSettings(Dictionary<string, object> slotData)
        {
            MasterRoundChecksEnabled = ReadInt(slotData, "MasterRoundChecks", 0) != 0;
            ShrineChecksEnabled = ReadInt(slotData, "ShrineChecks", 0) != 0;
            NPCRescueChecksEnabled = ReadInt(slotData, "NPCRescueChecks", 0) != 0;
            ShopAPItemRate = ReadInt(slotData, "ShopAPItemRate", 3);
            BossHuntCount = ReadInt(slotData, "BossHunt", 0);
            TokensRequired = ReadInt(slotData, "TokensRequired", 0);
            sentThisSession.Clear();
            nextPoll = 0f;
        }

        private static int ReadInt(Dictionary<string, object> slotData, string key, int fallback)
        {
            return slotData.ContainsKey(key) ? Convert.ToInt32(slotData[key]) : fallback;
        }

        private static void Send(long locationID)
        {
            if (sentThisSession.Contains(locationID))
            {
                return;
            }
            sentThisSession.Add(locationID);
            ArchDebugPrint.DebugLog(DebugCategory.ServerSend, $"Extra check {locationID}");
            SessionHandler.DataSender.SendFoundLocationCheck(locationID);
        }

        #region Hooks

        public static void InitHooks()
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            AddHook(typeof(AdvancedShrineController).GetMethod("DoShrineEffect", instance), nameof(ShrineEffectHook));
            AddHook(typeof(ChallengeShrineController).GetMethod("HandleFinalEnemiesCleared", instance), nameof(ChallengeClearedHook));
        }

        private static void AddHook(MethodInfo target, string hookName)
        {
            if (target == null)
            {
                ArchDebugPrint.DebugLog(DebugCategory.PluginStartup, $"Could not find game method for {hookName}, skipping hook");
                return;
            }
            hooks.Add(new Hook(target, typeof(ExtraChecks).GetMethod(hookName, BindingFlags.Static | BindingFlags.Public)));
        }

        #endregion

        #region Shrines

        // Order matches the APWorld's shrine_locations
        private enum Shrine { Ammo, Blank, Blood, Cleanse, Companion, Dice, Glass, Hero, Junk, HealthSwap, Challenge }

        public static void ShrineEffectHook(Action<AdvancedShrineController, PlayerController> orig, AdvancedShrineController self, PlayerController player)
        {
            orig(self, player);

            try
            {
                Shrine? kind = Classify(self);
                ArchDebugPrint.DebugLog(DebugCategory.PlayerEventListener, $"Shrine used: {self.displayTextKey} -> {(kind.HasValue ? kind.Value.ToString() : "not a check")}");
                if (kind.HasValue && ShrineChecksEnabled)
                {
                    Send(ShrineStartID + (int)kind.Value);
                }
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.PlayerEventListener, $"Shrine check failed: {e.Message}");
            }
        }

        public static void ChallengeClearedHook(Action<ChallengeShrineController> orig, ChallengeShrineController self)
        {
            orig(self);
            if (ShrineChecksEnabled)
            {
                ArchDebugPrint.DebugLog(DebugCategory.PlayerEventListener, "Challenge Shrine completed");
                Send(ShrineStartID + (int)Shrine.Challenge);
            }
        }

        private static Shrine? Classify(AdvancedShrineController shrine)
        {
            if (shrine.IsBlankShrine) return Shrine.Blank;
            if (shrine.IsRNGShrine) return Shrine.Dice;
            if (shrine.IsHealthArmorSwapShrine) return Shrine.HealthSwap;
            if (shrine.IsJunkShrine) return Shrine.Junk;
            if (shrine.IsBloodShrine) return Shrine.Blood;
            if (shrine.IsGlassShrine) return Shrine.Glass;
            if (shrine.IsCleanseShrine) return Shrine.Cleanse;
            if (shrine.IsLegendaryHeroShrine) return Shrine.Hero;

            if (shrine.Benefits != null)
            {
                foreach (ShrineBenefit benefit in shrine.Benefits)
                {
                    if (benefit == null) continue;
                    if (benefit.benefitType == ShrineBenefit.BenefitType.COMPANION) return Shrine.Companion;
                    if (benefit.benefitType == ShrineBenefit.BenefitType.AMMO_PERCENTAGE) return Shrine.Ammo;
                    if (benefit.benefitType == ShrineBenefit.BenefitType.CLEANSE_CURSE) return Shrine.Cleanse;
                }
            }

            string key = shrine.displayTextKey ?? "";
            if (key.IndexOf("AMMO", StringComparison.OrdinalIgnoreCase) >= 0) return Shrine.Ammo;
            if (key.IndexOf("COMPANION", StringComparison.OrdinalIgnoreCase) >= 0) return Shrine.Companion;
            return null;
        }

        #endregion

        #region Polling: Master Rounds and NPC rescues

        // Master Round item IDs, found by name the first time they're needed ("Master Round I" ... "V")
        private static Dictionary<int, int> masterRoundIndexByItemId = null;

        // Order matches the APWorld's npc_rescue_locations. Flags are the game's own "rescued" flags.
        private static readonly string[] NPCRescueFlags =
        {
            "SHOP_KEY_ACTIVE",                     // Flynt
            "SHOP_BLANK_ACTIVE",                   // Old Red
            "SHOP_CURSE_ACTIVE",                   // Cursula
            "SHOP_GOOP_ACTIVE",                    // Professor Goopton
            "SHOP_TRUCK_ACTIVE",                   // Trorc
            "GUNSLING_KING_RESCUED_FROM_CELL",     // the Gunsling King
            "LOST_ADVENTURER_RESCUED_FROM_CELL",   // the Lost Adventurer
            "TONIC_ACTIVE_IN_FOYER",               // Tonic
            "FRIFLE_ACTIVE_IN_FOYER",              // Frifle and Mauser
            "DAISUKE_ACTIVE_IN_FOYER",             // Daisuke
            "BOWLER_ACTIVE_IN_FOYER",              // Bowler
            "SORCERESS_ACTIVE_IN_FOYER",           // the Sorceress
            "SYNERGRACE_UNLOCKED",                 // the Synergrace
        };

        /// <summary>Game thread, every frame; does its work about once a second.</summary>
        public static void Tick()
        {
            if (!SessionHandler.IsConnected || Time.realtimeSinceStartup < nextPoll || !GameManager.HasInstance)
            {
                return;
            }
            nextPoll = Time.realtimeSinceStartup + 1f;

            try
            {
                if (MasterRoundChecksEnabled)
                {
                    PollMasterRounds();
                }
                if (NPCRescueChecksEnabled)
                {
                    PollNPCRescues();
                }
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.PlayerEventListener, $"Extra check poll failed: {e.Message}");
            }
        }

        private static void PollMasterRounds()
        {
            if (masterRoundIndexByItemId == null)
            {
                masterRoundIndexByItemId = FindMasterRounds();
            }

            foreach (PlayerController player in GameManager.Instance.AllPlayers)
            {
                if (player == null || player.passiveItems == null)
                {
                    continue;
                }
                foreach (PassiveItem item in player.passiveItems)
                {
                    int index;
                    if (item != null && masterRoundIndexByItemId.TryGetValue(item.PickupObjectId, out index))
                    {
                        Send(MasterRoundStartID + index);
                    }
                }
            }
        }

        private static Dictionary<int, int> FindMasterRounds()
        {
            Dictionary<int, int> result = new Dictionary<int, int>();
            string[] numerals = { "I", "II", "III", "IV", "V" };

            foreach (PickupObject pickup in PickupObjectDatabase.Instance.Objects)
            {
                if (!(pickup is PassiveItem))
                {
                    continue;
                }
                string name;
                try { name = pickup.EncounterNameOrDisplayName; } catch (Exception) { continue; }
                if (name == null || !name.StartsWith("Master Round"))
                {
                    continue;
                }
                string numeral = name.Substring("Master Round".Length).Trim();
                int index = Array.IndexOf(numerals, numeral);
                if (index >= 0)
                {
                    result[pickup.PickupObjectId] = index;
                }
            }

            ArchDebugPrint.DebugLog(DebugCategory.PluginStartup, $"Found {result.Count} Master Rounds");
            return result;
        }

        private static void PollNPCRescues()
        {
            if (GameStatsManager.Instance == null)
            {
                return;
            }
            for (int i = 0; i < NPCRescueFlags.Length; i++)
            {
                GungeonFlags flag;
                try { flag = (GungeonFlags)Enum.Parse(typeof(GungeonFlags), NPCRescueFlags[i]); }
                catch (Exception) { continue; }

                if (GameStatsManager.Instance.GetFlag(flag))
                {
                    Send(NPCRescueStartID + i);
                }
            }
        }

        #endregion

        #region Goals

        private static readonly HashSet<long> bossesSentLocally = new HashSet<long>();

        /// <summary>Called when a boss check is sent; re-checks the goal for Boss Hunt.</summary>
        public static void OnBossCheckSent(long locationID)
        {
            bossesSentLocally.Add(locationID);
            if (BossHuntCount > 0)
            {
                SessionHandler.DataSender.CheckForGameCompletion();
            }
        }

        public static int TokenCount
        {
            get { return SessionHandler.CountReceivedItem(TokenID); }
        }

        public static int BossesKilled
        {
            get
            {
                if (!SessionHandler.IsConnected)
                {
                    return 0;
                }
                // The server's checked list lags a sent check slightly, so include ones sent this session
                HashSet<long> bosses = new HashSet<long>(bossesSentLocally);
                foreach (long id in SessionHandler.Session.Locations.AllLocationsChecked)
                {
                    if (id >= BossChecks.StartID && id < BossChecks.StartID + 100)
                    {
                        bosses.Add(id);
                    }
                }
                return bosses.Count;
            }
        }

        /// <summary>Unmet parts of the Token Hunt and Boss Hunt goals, for the goal list.</summary>
        public static List<string> GetUnmetHuntGoals()
        {
            List<string> unmet = new List<string>();
            if (TokensRequired > 0 && TokenCount < TokensRequired)
            {
                unmet.Add($"Archipelago Tokens {TokenCount}/{TokensRequired}");
            }
            if (BossHuntCount > 0 && BossesKilled < BossHuntCount)
            {
                unmet.Add($"Different bosses {BossesKilled}/{BossHuntCount}");
            }
            return unmet;
        }

        /// <summary>Returns true if the item was a token (nothing to give in-game).</summary>
        public static bool HandleReceivedItem(long itemID)
        {
            if (itemID != TokenID)
            {
                return false;
            }
            ArchipelagoGUI_Log($"Archipelago Token {TokenCount}/{Math.Max(TokensRequired, 1)}");
            SessionHandler.DataSender.CheckForGameCompletion();
            return true;
        }

        private static void ArchipelagoGUI_Log(string text)
        {
            ArchiGungeon.UserInterface.ArchipelagoGUI.ConsoleLog(text);
        }

        #endregion
    }
}
