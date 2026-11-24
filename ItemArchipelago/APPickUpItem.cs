using Alexandria.ItemAPI;
using ArchiGungeon.ArchipelagoServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using ArchiGungeon.DebugTools;
using Archipelago.MultiClient.Net.Models;
using ArchiGungeon.Data;

namespace ArchiGungeon.ItemArchipelago
{

    public class APPickUpItem: PassiveItem
    {
        #region Item Init
        private static long APItemStartID { get; } = 8755000;

        private static List<long> remainingLocationIDs = new List<long>();

        public static int SpawnItemID = -1;

        private static string displayName = "AP Item";
        private static string shortDescription = "A cool item from another world";
        private const string NameKey = "#ARCHIGUN_APITEM_ENCNAME";
        private const string DescriptionKey = "#ARCHIGUN_APITEM_SHORTDESC";
        private const string EntryKey = "#ARCHIGUN_APITEM_LONGDESC";
        private const string DefaultEntry = "An item from another world. Picking it up sends it to whoever it belongs to in the multiworld.";

        // What each AP Item picked up this run held, for the death screen recap
        private static readonly List<string> foundThisRun = new List<string>();
        private static readonly List<string> foundDetailsThisRun = new List<string>();

        /// <summary>This run's AP Items: what each held, and its importance/game line.</summary>
        public static List<KeyValuePair<string, string>> GetFoundThisRun()
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < foundThisRun.Count; i++)
            {
                result.Add(new KeyValuePair<string, string>(foundThisRun[i], i < foundDetailsThisRun.Count ? foundDetailsThisRun[i] : ""));
            }
            return result;
        }
        private static bool showingRunSummary = false;

        // Scouted contents of each AP Item location: written on the network thread, read on the game thread
        private static readonly object previewLock = new object();
        private static Dictionary<long, string> previewNames = new Dictionary<long, string>();
        private static Dictionary<long, string> previewDescriptions = new Dictionary<long, string>();
        private static bool previewDirty = false;
        private static string spriteDirectory = "ArchiGungeon/Resources/archipelago.png";

        public static void RegisterItemBase()
        {
            ArchDebugPrint.DebugLog(DebugCategory.PluginStartup, "Registering APPickUpItem");

            GameObject obj = new GameObject(displayName);
            var item = obj.AddComponent<APPickUpItem>();

            ItemBuilder.AddSpriteToObject(displayName, spriteDirectory, obj);

            ItemBuilder.SetupItem(item, "A crossover!", shortDescription, ArchipelaGunPlugin.MOD_ITEM_PREFIX);

            // Own string keys, so the previews can change the text the game looks up
            item.encounterTrackable.journalData.PrimaryDisplayName = NameKey;
            item.encounterTrackable.journalData.NotificationPanelDescription = DescriptionKey;
            item.encounterTrackable.journalData.AmmonomiconFullEntry = EntryKey;
            ETGMod.Databases.Strings.Items.Set(NameKey, displayName);
            ETGMod.Databases.Strings.Items.Set(DescriptionKey, shortDescription);
            ETGMod.Databases.Strings.Items.Set(EntryKey, DefaultEntry);
            item.encounterTrackable.journalData.ClearCache();

            item.ShouldBeExcludedFromShops = true;
            item.CanBeDropped = false;
            item.CanBeSold = false;
            item.IgnoredByRat = true;
            item.quality = ItemQuality.EXCLUDED;

            SpawnItemID = PickupObjectDatabase.GetId(item);

            //ArchipelagoGUI.ConsoleLog("APItem spawn ID: " + SpawnItemID);

            return;
        }

        #endregion

        #region Item Behavior
        public static void RegisterAPItemLocations(int APItemCount)
        {
            // Reconnecting registers again; without clearing, every ID was listed twice and the
            // second copies re-sent locations that were already checked
            remainingLocationIDs.Clear();

            for (int i = 0; i < APItemCount; i++)
            {
                remainingLocationIDs.Add(APItemStartID + i);
            }

  
            var locationID = SessionHandler.Session.Locations.AllLocationsChecked;

            foreach(long item in locationID)
            {
                if(remainingLocationIDs.Contains(item))
                {
                    ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, "APItem Location Check already cleared: " + item);
                    remainingLocationIDs.Remove(item);
                }
            }

            ScoutRemainingLocations();

            return;
        }

        #endregion

        #region Item Previews
        // Every AP Item hands out the next unchecked location, so the item's name and description are set
        // to what that location holds: the shop, the pickup popup and the hover text then show it.

        private static void ScoutRemainingLocations()
        {
            lock (previewLock)
            {
                previewNames.Clear();
                previewDescriptions.Clear();
                previewDirty = true;
            }

            if (remainingLocationIDs.Count == 0)
            {
                return;
            }

            int ownSlot = SessionHandler.Session.ConnectionInfo.Slot;

            try
            {
                SessionHandler.Session.Locations.ScoutLocationsAsync(scouted =>
                {
                    lock (previewLock)
                    {
                        foreach (var entry in scouted)
                        {
                            var info = entry.Value;
                            string itemName = info.ItemName ?? $"Item {info.ItemId}";
                            bool isOwn = info.Player != null && info.Player.Slot == ownSlot;
                            string owner = info.Player != null ? info.Player.Name : "someone";

                            previewNames[entry.Key] = isOwn ? itemName : $"{itemName} for {owner}";
                            previewDescriptions[entry.Key] = $"{DescribeImportance(info.Flags)}" + (isOwn ? "" : $" ({info.ItemGame})");
                        }

                        previewDirty = true;
                    }
                }, remainingLocationIDs.ToArray());
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"AP Item scout failed: {e.Message}");
            }
        }

        private static string DescribeImportance(Archipelago.MultiClient.Net.Enums.ItemFlags flags)
        {
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.Advancement) != 0)
            {
                return "Progression";
            }
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.Trap) != 0)
            {
                return "Trap?";
            }
            if ((flags & Archipelago.MultiClient.Net.Enums.ItemFlags.NeverExclude) != 0)
            {
                return "Useful";
            }
            return "Filler";
        }

        /// <summary>Game thread: applies the next location's preview to the AP Item once it is known.</summary>
        public static void TickPreview()
        {
            if (!previewDirty || SpawnItemID < 0 || showingRunSummary)
            {
                return;
            }

            string name = displayName;
            string description = shortDescription;

            lock (previewLock)
            {
                previewDirty = false;

                if (remainingLocationIDs.Count > 0 && previewNames.ContainsKey(remainingLocationIDs[0]))
                {
                    name = previewNames[remainingLocationIDs[0]];
                    description = previewDescriptions[remainingLocationIDs[0]];
                }
            }

            try
            {
                PickupObject prefab = PickupObjectDatabase.GetById(SpawnItemID);
                if (prefab == null || prefab.encounterTrackable == null)
                {
                    return;
                }

                ETGMod.Databases.Strings.Items.Set(NameKey, name);
                ETGMod.Databases.Strings.Items.Set(DescriptionKey, description);
                ETGMod.Databases.Strings.Items.Set(EntryKey, DefaultEntry);
                ClearNameCaches(prefab);
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"AP Item preview failed: {e.Message}");
            }
        }


        public static int GetBaseAPItemAmount(int userOptionCase)
        {
            switch (userOptionCase)
            {
                case 0:
                    return 16;
                case 1:
                    return 25;
                case 2:
                    return 40;
                default:
                    return 25;
            }
        }

        public override void Pickup(PlayerController player)
        {
            base.Pickup(player);

            if(remainingLocationIDs.Count > 0)
            {
                string held;
                string detail;
                lock (previewLock)
                {
                    held = previewNames.ContainsKey(remainingLocationIDs[0]) ? previewNames[remainingLocationIDs[0]] : "an item for the multiworld";
                    detail = previewDescriptions.ContainsKey(remainingLocationIDs[0]) ? previewDescriptions[remainingLocationIDs[0]] : "";
                }
                foundThisRun.Add(held);
                foundDetailsThisRun.Add(detail);

                ArchDebugPrint.DebugLog(DebugCategory.ServerSend, $"Sending location ID {remainingLocationIDs[0]}");
                SessionHandler.DataSender.SendFoundLocationCheck(remainingLocationIDs[0]);

                remainingLocationIDs.RemoveAt(0);

                // The next AP Item holds the next location
                previewDirty = true;
            }

            //handle edge case of user finishing items
            
            return;
        }

        public static bool HasAPItemChecksRemaining()
        {
            return (remainingLocationIDs.Count > 0);
        }

        // Names are cached per journal entry: the prefab's, the encounter database's (used by the Ammonomicon and
        // the death screen) and each AP Item already spawned or held
        private static void ClearNameCaches(PickupObject prefab)
        {
            prefab.encounterTrackable.journalData.ClearCache();

            EncounterDatabaseEntry entry = EncounterDatabase.GetEntry(prefab.encounterTrackable.EncounterGuid);
            if (entry != null && entry.journalData != null)
            {
                entry.journalData.ClearCache();
            }

            foreach (APPickUpItem placed in UnityEngine.Object.FindObjectsOfType<APPickUpItem>())
            {
                if (placed.encounterTrackable != null)
                {
                    placed.encounterTrackable.journalData.ClearCache();
                }
            }
        }

        /// <summary>
        /// On death: every AP Item you're holding shares one name, which was showing the next location's
        /// preview. Show a recap of what this run's AP Items held instead.
        /// </summary>
        public static void ShowRunSummary()
        {
            if (SpawnItemID < 0)
            {
                return;
            }

            try
            {
                showingRunSummary = true;
                string count = foundThisRun.Count == 1 ? "1 AP Item" : $"{foundThisRun.Count} AP Items";
                string list = foundThisRun.Count == 0 ? "None this run." : string.Join("\n", foundThisRun.ToArray());

                ETGMod.Databases.Strings.Items.Set(NameKey, "AP Items");
                ETGMod.Databases.Strings.Items.Set(DescriptionKey, $"Found {count} this run");
                ETGMod.Databases.Strings.Items.Set(EntryKey, "This run's AP Items held:\n" + list);
                ClearNameCaches(PickupObjectDatabase.GetById(SpawnItemID));
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.ItemHandling, $"AP Item run summary failed: {e.Message}");
            }
        }

        /// <summary>New run: go back to previewing the next location.</summary>
        public static void ResetForNewRun()
        {
            foundThisRun.Clear();
            foundDetailsThisRun.Clear();
            showingRunSummary = false;
            previewDirty = true;
        }

        #endregion
    }
}
