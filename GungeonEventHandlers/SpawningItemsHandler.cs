using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using ArchiGungeon.DebugTools;
using Alexandria.Misc;
using ArchiGungeon.UserInterface;
using Alexandria.ItemAPI;
using ArchiGungeon.Data;
using ArchiGungeon.ArchipelagoServer;

namespace ArchiGungeon.GungeonEventHandlers
{
    #region General Item Spawning
    public class ConsumableSpawnHandler
    {
        public static bool IsSpawnValid { get; protected set; } = true;

        public static void SetCanSpawn(bool newState)
        {
            IsSpawnValid = newState;
            return;
        }


        public static void SpawnConsumableByCase(int itemCase)
        {
            if (IsSpawnValid == false)
            {
                return;
            }

            PlayerController playerToSpawnOn = GungeonPlayerEventListener.GetFirstAlivePlayer();
            EffectsController.PlaySynergyVFX();

            switch (itemCase)
            {
                case 0:
                    //ChestUtility.SpawnChestEasy(IntVector2.Up, ChestUtility.ChestTier.SYNERGY, false);

                    playerToSpawnOn.GiveItem("glass_guon_stone");
                    break;
                case 1:
                    playerToSpawnOn.GiveItem("50_casing");
                    break;
                case 2:
                    playerToSpawnOn.GiveItem("key");
                    break;
                case 3:
                    playerToSpawnOn.GiveItem("blank");
                    break;
                case 4:
                    playerToSpawnOn.GiveItem("armor");
                    break;
                case 5:
                    playerToSpawnOn.GiveItem("heart");
                    break;
                case 6:
                    playerToSpawnOn.GiveItem("ammo");
                    break;

                default:
                    ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Consumable received invalid ID: {itemCase}");
                    break;

            }


            return;
        }

    }

    public class RandomizedByQualityItems
    {
        // Random-tier items are fixed per seed: the nth "Random A Tier Gun" you've received always rolls the same
        // gun in this seed, every run. Each roll gets its own generator, seeded from the room seed, the slot,
        // the item type and which copy of it this is. Items are handed out in the order the server sent them,
        // so the copy number lines up from run to run.
        private static System.Random random = new();
        private static readonly Dictionary<int, int> rollsThisRun = new Dictionary<int, int>();

        // What each roll gave this run, for the menu's item tooltips. Key: "case|copy".
        public struct RollResult { public string Name; public string Description; }
        private static readonly Dictionary<string, RollResult> resultsThisRun = new Dictionary<string, RollResult>();
        private static string currentRollKey = null;

        /// <summary>Call at the start of each run, before received items are handed out again.</summary>
        public static void ResetRollCounters()
        {
            rollsThisRun.Clear();
            resultsThisRun.Clear();
        }

        public static bool TryGetRoll(int itemCase, int copy, out RollResult result)
        {
            return resultsThisRun.TryGetValue($"{itemCase}|{copy}", out result);
        }

        private static void RecordRoll(PickupObject item)
        {
            if (currentRollKey == null || item == null)
            {
                return;
            }
            resultsThisRun[currentRollKey] = new RollResult
            {
                Name = ItemTooltips.NameOf(item),
                Description = ItemTooltips.DescribeGameItem(item),
            };
            currentRollKey = null;
        }

        private static void StartRoll(int itemCase)
        {
            int copy;
            rollsThisRun.TryGetValue(itemCase, out copy);
            rollsThisRun[itemCase] = copy + 1;
            currentRollKey = $"{itemCase}|{copy}";

            random = new System.Random(StableHash($"{SessionHandler.SeedKey}|{itemCase}|{copy}"));
        }

        // string.GetHashCode isn't guaranteed stable between runtimes, so use FNV-1a
        private static int StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        // Picks from the whole item database (sorted by ID) rather than the game's own picker, whose choices
        // depend on what's unlocked on this save and so would change as you unlock things. This also fixes the
        // game's picker returning nothing when every item of a quality is still locked.
        private static Gun GetRandomGunByQualities(PickupObject.ItemQuality[] itemQuals)
        {
            return PickFromDatabase<Gun>(itemQuals);
        }

        private static PassiveItem GetRandomPassiveByQualities(PickupObject.ItemQuality[] itemQuals)
        {
            return PickFromDatabase<PassiveItem>(itemQuals);
        }

        private static T PickFromDatabase<T>(PickupObject.ItemQuality[] itemQuals) where T : PickupObject
        {
            List<T> pool = new List<T>();
            foreach (PickupObject pickup in PickupObjectDatabase.Instance.Objects)
            {
                T item = pickup as T;
                if (item != null && itemQuals.Contains(item.quality) && item.encounterTrackable != null)
                {
                    pool.Add(item);
                }
            }

            if (pool.Count == 0)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"No {typeof(T).Name} found for {string.Join(",", itemQuals.Select(q => q.ToString()).ToArray())}");
                return null;
            }

            pool.Sort((a, b) => a.PickupObjectId.CompareTo(b.PickupObjectId));
            return pool[random.Next(pool.Count)];
        }


        public static void GivePlayerRandomGun(PickupObject.ItemQuality[] itemquals)
        {
            PlayerController playerToSpawnOn = GungeonPlayerEventListener.GetFirstAlivePlayer();
            Gun gunToGive = GetRandomGunByQualities(itemquals);
            if (playerToSpawnOn == null || gunToGive == null)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, "Couldn't give random gun (no player or no gun found)");
                return;
            }

            ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Giving random gun: {gunToGive.name}");
            RecordRoll(gunToGive);
            playerToSpawnOn.inventory.AddGunToInventory(gunToGive);

            return;
        }

        
        public static void GivePlayerRandomPassive(PickupObject.ItemQuality[] itemquals)
        {
            PlayerController playerToSpawnOn = GungeonPlayerEventListener.GetFirstAlivePlayer();
            PassiveItem passiveToSpawn = GetRandomPassiveByQualities(itemquals);
            if (playerToSpawnOn == null || passiveToSpawn == null)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, "Couldn't give random passive (no player or no item found)");
                return;
            }

            //ArchipelagoGUI.ConsoleLog(passiveToSpawn.Cool);
            //ItemBuilder.SetCooldownType
            ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Giving random passive: {passiveToSpawn.name}");
            RecordRoll(passiveToSpawn);
            playerToSpawnOn.AcquirePassiveItem(passiveToSpawn);

            //SpawnObjectPlayerItem.ItemQuality.

            return;
        }

        #region Active ItemIds
        private static List<int> LowRarityActives { get; } = new List<int>()
        {
            485,
            252,
            443,
            105,
            201,
            433,
            448,
            643,
            458,
            206,
            525,
            499,
            449, // C after
            63,
            104,
            460,
            308,
            242,
            234,
            155,
            438,
            403,
            462,
            432,
            447,
            814,
            272,
            106, // D after
            291,
            558,
            108,
            109,
            66,
            136,
            366,
            77,
            625,
            69,
            71,
            820,
            356,
            64,
            205,
            174,
            65,
            250,
            644,
            203,
            209,
            306

        };

        private static List<int> HighRarityActives { get; } = new List<int>()
        {
            536,
            439,
            237,
            216,
            573,
            276,
            267,
            320,
            239,
            663, // B after this point
            485,
            252,
            443,
            105,
            201,
            433,
            448,
            643,
            458,
            206,
            525,
            499,
            449

        };

        #endregion

        public static void GivePlayerRandomActive(bool rollHigherRarityItems = false)
        {
            int itemID = 0;

            if (rollHigherRarityItems)
            {
                int randomIndex =  random.Next(0, HighRarityActives.Count);
                itemID = HighRarityActives[randomIndex];
            }
            else
            {
                int randomIndex = random.Next(0, LowRarityActives.Count);
                itemID = LowRarityActives[randomIndex];
            }

            PlayerController playerToSpawnOn = GungeonPlayerEventListener.GetFirstAlivePlayer();

            RecordRoll(PickupObjectDatabase.GetById(itemID));
            GameObject itemToGive = PickupObjectDatabase.GetById(itemID).gameObject;
            LootEngine.SpawnItem(itemToGive, playerToSpawnOn.CenterPosition, Vector2.zero, 0);
        }

        public static void GiveRandomizedItemByCase(int itemCase)
        {
            EffectsController.PlaySynergyVFX();
            StartRoll(itemCase);

            switch (itemCase)
            {
                case 0:
                    GivePlayerRandomGun(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.D, PickupObject.ItemQuality.C });
                    break;
                case 1:
                    //GivePlayerRandomGun(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.C });
                    //TODO: implement D, C, B active
                    GivePlayerRandomActive(false);
                    break;
                case 2:
                    GivePlayerRandomGun(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.B });
                    break;
                case 3:
                    GivePlayerRandomGun(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.A });
                    break;
                case 4:
                    GivePlayerRandomGun(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.S });
                    break;
                case 5:
                    GivePlayerRandomPassive(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.D, PickupObject.ItemQuality.C });
                    break;
                case 6:

                    GivePlayerRandomActive(true);
                    //GivePlayerRandomPassive(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.C });
                    //TODO: implement B, A, S
                    break;
                case 7:
                    GivePlayerRandomPassive(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.B });
                    break;
                case 8:
                    GivePlayerRandomPassive(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.A });
                    break;
                case 9:
                    GivePlayerRandomPassive(new PickupObject.ItemQuality[] { PickupObject.ItemQuality.S });
                    break;

                default:
                    ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Random Gun/Passive received invalid ID: {itemCase}");
                    break;
            }

            return;
        }

    }

    #endregion

    #region Progression Key Objects
    public class ProgressionItemSpawnHandler
    {
        public static void SpawnProgressionItem(int itemCase)
        {
            EffectsController.PlaySynergyVFX();

            switch (itemCase)
            {
                case 1:

                    GungeonPlayerEventListener.GetFirstAlivePlayer().GiveItem("gnawed_key");

                    ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Giving gnawed key");

                    break;
                case 2:
                    GungeonPlayerEventListener.GetFirstAlivePlayer().GiveItem("old_crest");

                    ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Giving Old Crest");

                    break;

                case 3:
                    GungeonPlayerEventListener.GetFirstAlivePlayer().GiveItem("weird_egg");

                    ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Spawning weird egg");
                    //ETGModConsole.SpawnItem(new string[] { "weird_egg", "1" });

                    break;

                default:
                    ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"PROGRESSION received invalid ID: {itemCase}");
                    break;
            }

            return;
        }

        public static void GivePastBullet()
        {

            GungeonPlayerEventListener.GetFirstAlivePlayer().GiveItem("bullet_that_can_kill_the_past");

            ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Giving bullet");

            return;
        }

        public static void GiveRatNotes()
        {
            ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Giving Rat Notes");

            List<string> ratNoteItems = new List<string>()
            {
                "infuriating_note_1",
                "infuriating_note_2",
                "infuriating_note_3",
                "infuriating_note_4",
                "infuriating_note_5",
                "infuriating_note_6",
            };

            foreach(string note in ratNoteItems)
            {
                GungeonPlayerEventListener.GetFirstAlivePlayer().GiveItem(note);
            }

            return;
        }

    }
    #endregion

}
