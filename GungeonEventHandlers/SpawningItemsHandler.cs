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
        private static System.Random random = new();

        // The game's pickers return null when nothing of that quality is eligible (e.g. every S-tier passive is
        // still locked on this save), which crashed the hand-out and lost the item. Retry, then pick from the
        // whole database ourselves.
        private static Gun GetRandomGunByQualities(PickupObject.ItemQuality[] itemQuals)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                Gun gun = PickupObjectDatabase.GetRandomGunOfQualities(random, new List<int>(), itemQuals);
                if (gun != null)
                {
                    return gun;
                }
            }
            return PickFromDatabase<Gun>(itemQuals);
        }

        private static PassiveItem GetRandomPassiveByQualities(PickupObject.ItemQuality[] itemQuals)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                PassiveItem passive = PickupObjectDatabase.GetRandomPassiveOfQualities(random, new List<int>(), itemQuals);
                if (passive != null)
                {
                    return passive;
                }
            }
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

            ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"Random {typeof(T).Name} fallback pool: {pool.Count}");
            return pool.Count > 0 ? pool[random.Next(pool.Count)] : null;
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

            GameObject itemToGive = PickupObjectDatabase.GetById(itemID).gameObject;
            LootEngine.SpawnItem(itemToGive, playerToSpawnOn.CenterPosition, Vector2.zero, 0);
        }

        public static void GiveRandomizedItemByCase(int itemCase)
        {
            EffectsController.PlaySynergyVFX();

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
