using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Models;
using ArchiGungeon.GungeonEventHandlers;
using ArchiGungeon.ItemArchipelago;
using ArchiGungeon.ArchipelagoServer;

namespace ArchiGungeon.UserInterface
{
    /// <summary>Hover text for received items: what each one is, and what it turned into or did this run.</summary>
    public static class ItemTooltips
    {
        private const int MaxEntryChars = 260;

        // Random-tier items: id - 8754000 is the "case" RandomizedByQualityItems rolls on
        private static readonly Dictionary<long, string> RandomTierKinds = new Dictionary<long, string>
        {
            { 8754000, "a random D or C tier gun" },
            { 8754001, "a random lower-tier active item" },
            { 8754002, "a random B tier gun" },
            { 8754003, "a random A tier gun" },
            { 8754004, "a random S tier gun" },
            { 8754005, "a random D or C tier passive item" },
            { 8754006, "a random higher-tier active item" },
            { 8754007, "a random B tier passive item" },
            { 8754008, "a random A tier passive item" },
            { 8754009, "a random S tier passive item" },
        };

        private static readonly Dictionary<long, string> FixedDescriptions = new Dictionary<long, string>
        {
            // consumables (given once, when received)
            { 8754100, "Gives a Glass Guon Stone, which orbits you and blocks bullets until it breaks." },
            { 8754101, "Gives 50 casings." },
            { 8754102, "Gives a key." },
            { 8754103, "Gives a blank." },
            { 8754104, "Gives a piece of armor." },
            { 8754105, "Gives a heart." },
            { 8754106, "Gives an ammo pickup." },

            // traps (happen once, when received)
            { 8754200, "Trap: spawns 20 rats around you." },
            { 8754201, "Trap: puts a Mimic Gun in your inventory." },
            { 8754202, "Trap: gives you a stack of the Reverse Curse." },
            { 8754203, "Trap: makes you drop the gun you're holding." },
            { 8754204, "Trap: sets you on fire." },
            { 8754205, "Trap: fills half your poison meter." },
            { 8754206, "Trap: fills half your curse meter." },
            { 8754207, "Trap: spawns three Shelletons." },

            // key items
            { 8754301, "The Gnawed Key, for the Resourceful Rat's Lair. Handed to you at the start of each run." },
            { 8754302, "The Old Crest, for the Abbey of the True Gun. Handed to you at the start of each run." },
            { 8754303, "The Weird Egg. Handed to you at the start of each run." },

            // progression
            { ProgressionUnlocks.ChamberAccessID, "Unlocks the next chamber's elevator. Each copy lets you go one chamber deeper." },
            { ProgressionUnlocks.SellCreepsFavorID, "The Sell Creep owes you one: he'll open the way to the R&G Dept. (once you can reach the Forge)." },
            { 8754310, "A bullet part. Collect all four to be given the Bullet That Can Kill the Past." },
            { 8754311, "A bullet part. Collect all four to be given the Bullet That Can Kill the Past." },
            { 8754312, "A bullet part. Collect all four to be given the Bullet That Can Kill the Past." },
            { 8754313, "A bullet part. Collect all four to be given the Bullet That Can Kill the Past." },

            // paradox unlocks
            { 8754400, "Unlocks the Marine for Paradox Mode." },
            { 8754401, "Unlocks the Convict for Paradox Mode." },
            { 8754402, "Unlocks the Pilot for Paradox Mode." },
            { 8754403, "Unlocks the Hunter for Paradox Mode." },
            { 8754404, "Unlocks the Robot for Paradox Mode." },
            { 8754405, "Unlocks the Bullet for Paradox Mode." },

            { 8754500, "Removes one stack of the Reverse Curse." },
            { ExtraChecks.TokenID, "An Archipelago Token. Find enough of them to finish the Token Hunt goal." },

            // persistent upgrades (applied at the start of every run)
            { PersistentUpgrades.MaxHealthID, "+1 max heart every run (the Robot gets armor instead)." },
            { PersistentUpgrades.ArmorID, "+1 armor at the start of every run." },
            { PersistentUpgrades.BlankID, "+1 blank at the start of every run." },
            { PersistentUpgrades.CasingsID, "+25 casings at the start of every run." },
            { PersistentUpgrades.KeyID, "+1 key at the start of every run." },
        };

        /// <summary>
        /// Hover text for one received item. copy is which copy of this item type it is (0 = first received),
        /// used to find what a random-tier item rolled this run.
        /// </summary>
        public static string Build(ItemInfo item, int copy)
        {
            string from = item.Player != null ? item.Player.Name : "someone";
            string location = string.IsNullOrEmpty(item.LocationName) ? "" : $" ({item.LocationName})";
            string header = $"{item.ItemName}\nFrom {from}{location}";

            string kind;
            if (RandomTierKinds.TryGetValue(item.ItemId, out kind))
            {
                RandomizedByQualityItems.RollResult roll;
                if (RandomizedByQualityItems.TryGetRoll((int)(item.ItemId - 8754000), copy, out roll))
                {
                    return $"{header}\n\nThis run it gave: {roll.Name}\n{roll.Description}";
                }
                return $"{header}\n\nGives {kind}. It hasn't been handed out yet this run.";
            }

            string fixedText;
            if (FixedDescriptions.TryGetValue(item.ItemId, out fixedText))
            {
                return $"{header}\n\n{fixedText}";
            }

            if ((item.Flags & ItemFlags.Trap) != 0)
            {
                return $"{header}\n\nA trap.";
            }

            return header;
        }

        public static string NameOf(PickupObject pickup)
        {
            try
            {
                return pickup.EncounterNameOrDisplayName;
            }
            catch (Exception)
            {
                return pickup.name;
            }
        }

        /// <summary>The item's in-game quote and Ammonomicon entry, trimmed to fit a tooltip.</summary>
        public static string DescribeGameItem(PickupObject pickup)
        {
            try
            {
                JournalEntry journal = pickup.encounterTrackable != null ? pickup.encounterTrackable.journalData : null;
                if (journal == null)
                {
                    return "";
                }

                string quote = Localize(journal.NotificationPanelDescription);
                string entry = Localize(journal.AmmonomiconFullEntry);
                if (entry.Length > MaxEntryChars)
                {
                    entry = entry.Substring(0, MaxEntryChars).TrimEnd() + "...";
                }

                if (quote.Length == 0)
                {
                    return entry;
                }
                return entry.Length == 0 ? $"\"{quote}\"" : $"\"{quote}\"\n{entry}";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string Localize(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }
            string text = key.StartsWith("#") ? StringTableManager.GetItemsString(key, -1) : key;
            // Ammonomicon text uses {wb}/{w} style markup and line breaks; keep tooltips plain
            return (text ?? "").Replace("{wb}", "").Replace("{w}", "").Replace("{wq}", "").Replace("\r", "").Trim();
        }
    }
}
