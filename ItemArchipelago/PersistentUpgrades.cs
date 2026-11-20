using System;
using System.Collections.Generic;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.Data;
using ArchiGungeon.DebugTools;
using ArchiGungeon.GungeonEventHandlers;
using ArchiGungeon.UserInterface;

namespace ArchiGungeon.ItemArchipelago
{
    /// <summary>
    /// Permanent upgrades (APWorld option persistent_upgrades) applied at the start of every run, one
    /// step per copy received. IDs must match worlds/enter_the_gungeon/Items.py persistent_upgrade_table.
    /// </summary>
    public static class PersistentUpgrades
    {
        public const long MaxHealthID = 8754600;
        public const long ArmorID = 8754601;
        public const long BlankID = 8754602;
        public const long CasingsID = 8754603;
        public const long KeyID = 8754604;

        private const int CasingsPerUpgrade = 25;

        private static readonly long[] AllIDs = { MaxHealthID, ArmorID, BlankID, CasingsID, KeyID };

        // How many of each upgrade this run's player already has. Health, armor and blanks belong to the
        // player object, so they're re-applied if the game replaces it (Paradox Mode character swaps).
        private static readonly Dictionary<long, int> appliedThisRun = new Dictionary<long, int>();
        private static PlayerController appliedToPlayer = null;

        public static void ResetForNewRun()
        {
            appliedThisRun.Clear();
            appliedToPlayer = null;
        }

        /// <summary>Handles upgrade item IDs. Returns true if the ID was handled.</summary>
        public static bool HandleReceivedItem(long itemID)
        {
            if (Array.IndexOf(AllIDs, itemID) < 0)
            {
                return false;
            }

            ApplyPending();
            return true;
        }

        /// <summary>Gives the current player any upgrades received but not yet applied this run.</summary>
        public static void ApplyPending()
        {
            if (!SessionHandler.IsValidToSpawnItems || GameManager.Instance == null || GameManager.Instance.IsFoyer)
            {
                return;
            }

            PlayerController player = GungeonPlayerEventListener.GetFirstAlivePlayer();
            if (player == null)
            {
                return;
            }

            if (appliedToPlayer != null && appliedToPlayer != player)
            {
                // New player object: its health, armor and blanks start from scratch
                appliedThisRun.Remove(MaxHealthID);
                appliedThisRun.Remove(ArmorID);
                appliedThisRun.Remove(BlankID);
            }
            appliedToPlayer = player;

            List<string> gained = new List<string>();

            int maxHealth = TakeNew(MaxHealthID);
            if (maxHealth > 0)
            {
                AddMaxHealth(player, maxHealth);
                gained.Add($"+{maxHealth} max health");
            }

            int armor = TakeNew(ArmorID);
            if (armor > 0)
            {
                player.healthHaver.Armor += armor;
                gained.Add($"+{armor} armor");
            }

            int blanks = TakeNew(BlankID);
            if (blanks > 0)
            {
                player.Blanks += blanks;
                gained.Add($"+{blanks} blank{(blanks > 1 ? "s" : "")}");
            }

            int casings = TakeNew(CasingsID);
            if (casings > 0)
            {
                player.carriedConsumables.Currency += casings * CasingsPerUpgrade;
                gained.Add($"+{casings * CasingsPerUpgrade} casings");
            }

            int keys = TakeNew(KeyID);
            if (keys > 0)
            {
                player.carriedConsumables.KeyBullets += keys;
                gained.Add($"+{keys} key{(keys > 1 ? "s" : "")}");
            }

            if (gained.Count > 0)
            {
                string text = "Upgrades: " + string.Join(", ", gained.ToArray());
                ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, text);
                TextBoxHandler.ShowItemEventTextBox(player, text, 3f);
            }
        }

        // Received copies not yet applied this run, marking them applied
        private static int TakeNew(long id)
        {
            int received = SessionHandler.CountReceivedItem(id);
            int applied;
            appliedThisRun.TryGetValue(id, out applied);

            appliedThisRun[id] = received;
            return Math.Max(0, received - applied);
        }

        private static void AddMaxHealth(PlayerController player, int amount)
        {
            // The Robot has no hearts; the game turns heart containers into armor for it too
            if (player.characterIdentity == PlayableCharacters.Robot)
            {
                player.healthHaver.Armor += amount;
                return;
            }

            StatModifier healthUp = new StatModifier();
            healthUp.statToBoost = PlayerStats.StatType.Health;
            healthUp.amount = amount;
            healthUp.modifyType = StatModifier.ModifyMethod.ADDITIVE;

            player.ownerlessStatModifiers.Add(healthUp);
            player.stats.RecalculateStats(player);
            player.healthHaver.ApplyHealing(amount);
        }
    }
}
