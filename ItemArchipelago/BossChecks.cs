using System.Collections.Generic;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.Data;
using ArchiGungeon.DebugTools;

namespace ArchiGungeon.ItemArchipelago
{
    /// <summary>
    /// First-kill check for each boss (APWorld option boss_checks). Location IDs must match
    /// worlds/enter_the_gungeon/Locations.py boss_locations, in the same order from 8756000.
    /// </summary>
    public static class BossChecks
    {
        public const long StartID = 8756000;

        // Index in the APWorld list -> enemy GUID. Trigger Twins (2) and Kill Pillars (12) are several
        // enemies, so they're sent from the listener once all of them are dead.
        private static readonly Dictionary<string, int> BossGuidToIndex = new Dictionary<string, int>()
        {
            { "ffca09398635467da3b1f4a54bcfda80", 0 },  // Bullet King
            { "ec6b674e0acd4553b47ee94493d66422", 1 },  // Gatling Gull
            { "da797878d215453abba824ff902e21b4", 3 },  // Ammoconda
            { "4b992de5b4274168a8878ef9bf7ea36b", 4 },  // Beholster
            { "c367f00240a64d5d9f3c26484dc35833", 5 },  // Gorgun
            { "5e0af7f7d9de4755a68d2fd3bbc15df4", 6 },  // Cannonbalrog
            { "fa76c8cfdf1c4a88b55173666b4bc7fb", 7 },  // Treadnaught
            { "8b0dd96e2fe74ec7bebc1bc689c0008a", 8 },  // Mine Flayer
            { "9189f46c47564ed588b9108965f975c9", 9 },  // Door Lord
            { "f3b04a067a65492f8b279130323b41f0", 10 }, // Wallmonger (floor 4; this pair may be swapped)
            { "6c43fddfd401456c916089fdd1c99b1c", 11 }, // High Priest
            { "465da2bb086a4a88a803f79fe3a27677", 13 }, // High Dragun
            { "05b8afe0b6cc4fffa9dc6036fa24c8ec", 14 }, // Advanced Dragun
            { "1b5810fafbec445d89921a4efb4e42b7", 15 }, // Blobulord
            { "5729c8b5ffa7415bb3d01205663a33ef", 16 }, // Old King
            { "4d164ba3f62648809a4a82c90fc22cae", 17 }, // Resourceful Rat (final form)
            { "41ee1c8538e8474a82a74c4aff99c712", 18 }, // Agunim
            { "7c5d5f09911e49b78ae644d2b50ff3bf", 19 }, // Lich (final form)
        };

        private const int TriggerTwinsIndex = 2;
        private const int KillPillarsIndex = 12;

        public static void OnBossKilled(string enemyGuid)
        {
            int index;
            if (enemyGuid != null && BossGuidToIndex.TryGetValue(enemyGuid, out index))
            {
                Send(index);
            }
        }

        public static void OnTriggerTwinsDefeated()
        {
            Send(TriggerTwinsIndex);
        }

        public static void OnKillPillarsDefeated()
        {
            Send(KillPillarsIndex);
        }

        private static void Send(int index)
        {
            if (!ProgressionUnlocks.BossChecksEnabled)
            {
                return;
            }

            // Sending an already-completed check again is harmless, so no local bookkeeping is needed
            ArchDebugPrint.DebugLog(DebugCategory.ServerSend, $"Boss check {index} ({StartID + index})");
            SessionHandler.DataSender.SendFoundLocationCheck(StartID + index);
            ExtraChecks.OnBossCheckSent(StartID + index);
        }
    }
}
