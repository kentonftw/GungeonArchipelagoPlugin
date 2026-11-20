using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using ArchiGungeon.GungeonEventHandlers;
using ArchiGungeon.ItemArchipelago;
using ArchiGungeon.DebugTools;
using ArchiGungeon.Character;
using ArchiGungeon.Data;

namespace ArchiGungeon.ArchipelagoServer
{
    public class ArchipelagoGungeonBridge
    {
        #region APWorld Data
        private static readonly long baseItemID = 8754000;
        private static readonly long consumableCategoryItemID = 8754100;
        private static readonly long trapCategoryItemID = 8754200;
        private static readonly long progressionItemID = 8754300;
        private static readonly long paradoxCharacterItemID = 8754400;
        private static readonly long undoCurseItemID = 8754500;

        #endregion

        #region Player References
        private static PlayerController playerOne;
        private static PlayerController playerTwo;

        public static void SetPlayerOne(PlayerController controller)
        {
            ArchDebugPrint.DebugLog(DebugCategory.PlayerEventListener, "Setting palyer One");
            playerOne = controller;
            return;
        }

        public static void SetPlayerTwo(PlayerController controller) 
        {
            playerTwo = controller;
            return;
        }
        #endregion

        #region Server Receive Events

        // PlayerController.Die and Fall, and HealthHaver.currentHealth, are private in the real game. Naming
        // them directly compiled against the publicized game libraries but made this whole method throw
        // at runtime, so a received DeathLink never killed anyone. Private methods go through reflection.
        private static readonly System.Reflection.MethodInfo playerDieMethod = typeof(PlayerController).GetMethod(
            "Die", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            null, new System.Type[] { typeof(Vector2) }, null);
        private static readonly System.Reflection.MethodInfo playerFallMethod = typeof(PlayerController).GetMethod(
            "Fall", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            null, System.Type.EmptyTypes, null);

        private static void InvokePrivate(System.Reflection.MethodInfo method, PlayerController player, params object[] args)
        {
            if (method == null)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, "Deathlink: game method not found");
                return;
            }
            method.Invoke(player, args);
        }

        public static void DeathlinkKillPlayer(string causeOfDeath = "Deathlink")
        {
            
            if(playerTwo != null)
            {
                if(playerTwo.healthHaver.IsAlive && playerOne.healthHaver.IsDead)
                {
                    ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, "Attempting to kill Player Two");

                    playerTwo.healthHaver.lastIncurredDamageSource = causeOfDeath;
                    playerTwo.healthHaver.Die(Vector2.zero);
                    return;
                }
                else if(playerTwo.healthHaver.IsAlive)
                {
                    ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, "Soft killing Player Two");

                    //playerTwo.healthHaver.ManualDeathHandling = true;
                    playerTwo.healthHaver.ForceSetCurrentHealth(0f);
                    playerTwo.healthHaver.lastIncurredDamageSource = causeOfDeath;
                    InvokePrivate(playerFallMethod, playerTwo);

                    return;
                }

            }

            ArchDebugPrint.DebugLog(DebugCategory.ServerReceive, "Killing Player One");
            playerOne.healthHaver.lastIncurredDamageSource = causeOfDeath;
            InvokePrivate(playerDieMethod, playerOne, Vector2.zero);

            return;
        }

        public static void GiveGungeonItem(long receivedItemID)
        {
            bool foundSpecificItem = CheckIDForSpecificItem(receivedItemID);

            if(foundSpecificItem)
            {
                return;
            }

            // gun & passives
            long categoryAdjustedID = receivedItemID - (long)baseItemID;
            if (categoryAdjustedID < 100)
            {
                RandomizedByQualityItems.GiveRandomizedItemByCase((int)categoryAdjustedID);
                return;
            }

            // consumables
            categoryAdjustedID = receivedItemID - (long)consumableCategoryItemID;
            if(categoryAdjustedID < 100)
            {
                ConsumableSpawnHandler.SpawnConsumableByCase((int)categoryAdjustedID);
                return;
            }

            // traps
            categoryAdjustedID = receivedItemID - (long)trapCategoryItemID;
            if (categoryAdjustedID < 100)
            {
                TrapSpawnHandler.SpawnTrapByCase((int)categoryAdjustedID);
                return;
            }

            // progression
            categoryAdjustedID = receivedItemID - (long)progressionItemID;
            if (categoryAdjustedID < 100)
            {
                ProgressionItemSpawnHandler.SpawnProgressionItem((int)categoryAdjustedID);
                return;
            }

            // paradox mode
            categoryAdjustedID = receivedItemID - (long)paradoxCharacterItemID;
            if (categoryAdjustedID < 100)
            {
                CharSwap.ReceiveParadoxModeItem((int)categoryAdjustedID);
                return;
            }

            return;
        }
        #endregion

        #region Item Spawning
        private static bool CheckIDForSpecificItem(long itemIdToCheck)
        {
            bool matchedItem = false;

            if(itemIdToCheck == undoCurseItemID)
            {
                GiveUndoReverseCurse(1);
                matchedItem = true;
            }

            // Chamber access, bullet parts, Sell Creep's Favor, Old Crest
            if (!matchedItem && ProgressionUnlocks.HandleReceivedItem(itemIdToCheck))
            {
                matchedItem = true;
            }

            // Max health, armor, blanks, casings, keys
            if (!matchedItem && PersistentUpgrades.HandleReceivedItem(itemIdToCheck))
            {
                matchedItem = true;
            }


            return matchedItem;
        }

        public static void SpawnAPItem(int numberToSpawn)
        {
            PlayerController playerToSpawnOn;

            if(playerOne.healthHaver.IsAlive)
            {
                playerToSpawnOn = playerOne;
            }

            else if (playerTwo != null)
            {
                if(playerTwo.healthHaver.IsDead)
                {
                    return;
                }
                playerToSpawnOn = playerTwo;
            }

            else
            {
                return;
            }

            for(int i=0; i < numberToSpawn; i++)
            {
                GameObject archipelItem = PickupObjectDatabase.GetById(APPickUpItem.SpawnItemID).gameObject;
                LootEngine.SpawnItem(archipelItem, playerToSpawnOn.CenterPosition, Vector2.zero, 0);
            }

            return;
        }

        public static void GiveReverseCurse(int numberToSpawn)
        {
            PlayerController playerToSpawnOn;

            if (playerOne.healthHaver.IsAlive)
            {
                playerToSpawnOn = playerOne;
            }

            else if (playerTwo != null)
            {
                if (playerTwo.healthHaver.IsDead)
                {
                    return;
                }
                playerToSpawnOn = playerTwo;
            }
            else
            {
                return;
            }

            for (int i = 0; i < numberToSpawn; i++)
            {
                PickupObject cursePassive = PickupObjectDatabase.GetById(ReverseCurse.SpawnItemID);
                playerToSpawnOn.AcquirePassiveItemPrefabDirectly((PassiveItem)cursePassive);
            }

            return;
        }

        public static void GiveUndoReverseCurse(int numberToSpawn)
        {
            PlayerController playerToSpawnOn;

            if (playerOne.healthHaver.IsAlive)
            {
                playerToSpawnOn = playerOne;
            }

            else if (playerTwo != null)
            {
                if (playerTwo.healthHaver.IsDead)
                {
                    return;
                }
                playerToSpawnOn = playerTwo;
            }
            else
            {
                return;
            }

            for (int i = 0; i < numberToSpawn; i++)
            {
                PickupObject undoCursePassive = PickupObjectDatabase.GetById(ReverseCurseReversal.SpawnItemID);
                playerToSpawnOn.AcquirePassiveItemPrefabDirectly((PassiveItem)undoCursePassive);
            }

            return;
        }
        #endregion
    }

}

