using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Alexandria.ItemAPI;
using Alexandria.NPCAPI;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.Data;
using ArchiGungeon.DebugTools;
using ArchiGungeon.ItemArchipelago;
using Dungeonator;
using UnityEngine;
using Color = UnityEngine.Color;

namespace ArchiGungeon.Character
{
    /// <summary>
    /// The Elevator Operator: an NPC who stands by each floor's exit elevator. Talk to him for your unlock
    /// status and progress; he also speaks up when you step into an elevator you can't use yet.
    /// </summary>
    public class ElevatorOperator : BraveBehaviour, IPlayerInteractable
    {
        public static ElevatorOperator Current { get; private set; }

        private static GameObject prefab;
        private const string SpriteFolder = "ArchiGungeon/Resources/";
        private static readonly string[] IdleFrames =
            { "operator_idle_001", "operator_idle_002", "operator_idle_003", "operator_idle_004" };
        private static readonly string[] TalkFrames =
            { "operator_talk_001", "operator_talk_002", "operator_talk_003", "operator_talk_004" };

        private const float SecondsPerLine = 3.5f;

        private RoomHandler room;
        private Coroutine conversation;
        private List<string> lines = new List<string>();
        private int lineIndex = 0;
        private bool skipToNext = false;
        private float lastLockLineTime = -100f;

        #region Setup

        public static void Init()
        {
            try
            {
                BuildPrefab();
                GameManager.Instance.OnNewLevelFullyLoaded += OnLevelLoaded;
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.PluginStartup, $"Elevator Operator setup failed: {e}");
            }
        }

        private static void BuildPrefab()
        {
            Assembly assembly = typeof(ElevatorOperator).Assembly;

            GameObject obj = SpriteBuilder.SpriteFromResource(SpriteFolder + IdleFrames[0] + ".png", null, assembly);
            obj.name = "ArchipelaGun Elevator Operator";
            FakePrefab.MarkAsFakePrefab(obj);
            UnityEngine.Object.DontDestroyOnLoad(obj);
            obj.SetActive(false);

            tk2dSprite sprite = obj.GetComponent<tk2dSprite>();
            tk2dSpriteCollectionData collection = sprite.Collection;

            List<int> idleIds = IdleFrames.Select(f => SpriteBuilder.AddSpriteToCollection(SpriteFolder + f + ".png", collection, assembly)).ToList();
            List<int> talkIds = TalkFrames.Select(f => SpriteBuilder.AddSpriteToCollection(SpriteFolder + f + ".png", collection, assembly)).ToList();

            tk2dSpriteAnimator animator = obj.AddComponent<tk2dSpriteAnimator>();
            SpriteBuilder.AddAnimation(animator, collection, idleIds, "idle", tk2dSpriteAnimationClip.WrapMode.Loop, 4f);
            SpriteBuilder.AddAnimation(animator, collection, talkIds, "talk", tk2dSpriteAnimationClip.WrapMode.Loop, 8f);
            animator.playAutomatically = true;
            animator.DefaultClipId = animator.GetClipIdByName("idle");

            // A small solid footprint (in pixels) under his feet so you can't walk through him. Frames are 26x33.
            ShopAPI.GenerateOrAddToRigidBody(obj, CollisionLayer.HighObstacle, PixelCollider.PixelColliderGeneration.Manual,
                collideWithTileMap: false, CollideWithOthers: true, CanBeCarried: false, CanBePushed: false, RecheckTriggers: false,
                IsTrigger: false, replaceExistingColliders: true, UsesPixelsAsUnitSize: true,
                dimensions: new IntVector2(12, 6), offset: new IntVector2(7, 0));

            obj.AddComponent<ElevatorOperator>();
            prefab = obj;
        }

        #endregion

        #region Spawning

        private static void OnLevelLoaded()
        {
            Current = null;

            try
            {
                if (prefab == null || GameManager.Instance.IsFoyer || GameManager.Instance.Dungeon == null)
                {
                    return;
                }

                ElevatorDepartureController elevator = UnityEngine.Object.FindObjectOfType<ElevatorDepartureController>();
                if (elevator == null)
                {
                    return;
                }

                Vector2 elevatorCenter = elevator.sprite != null ? elevator.sprite.WorldCenter : (Vector2)elevator.transform.position;
                RoomHandler elevatorRoom = GameManager.Instance.Dungeon.data.GetAbsoluteRoomFromPosition(elevatorCenter.ToIntVector2(VectorConversions.Floor));
                if (elevatorRoom == null)
                {
                    return;
                }

                // Right beside the elevator's platform; the room's reward spot is only a fallback
                IntVector2 cell;
                if (!TryFindSpotBesideElevator(elevator, elevatorRoom, out cell))
                {
                    cell = elevatorRoom.GetBestRewardLocation(new IntVector2(2, 2), elevatorCenter + new Vector2(-3.5f, -1.5f), false);
                }

                // Centre the 26px-wide sprite on the 2-tile footprint
                Vector3 position = cell.ToVector3() + new Vector3((2f - 26f / 16f) / 2f, 0f, 0f);
                GameObject instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
                instance.SetActive(true);

                ElevatorOperator npc = instance.GetComponent<ElevatorOperator>();
                npc.Place(elevatorRoom);
                Current = npc;

                ArchDebugPrint.DebugLog(DebugCategory.PlayerEventListener, $"Elevator Operator placed at {cell}");
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.PlayerEventListener, $"Elevator Operator spawn failed: {e}");
            }
        }

        /// <summary>
        /// Looks for a free 2x2 patch of floor hugging the elevator, preferring its lower-left side (where you
        /// walk up to it), then the lower-right, then anywhere within a few tiles.
        /// </summary>
        private static bool TryFindSpotBesideElevator(ElevatorDepartureController elevator, RoomHandler room, out IntVector2 result)
        {
            result = IntVector2.Zero;
            DungeonData data = GameManager.Instance.Dungeon.data;

            Vector2 min, max;
            if (elevator.sprite != null)
            {
                min = elevator.sprite.WorldBottomLeft;
                max = elevator.sprite.WorldTopRight;
            }
            else
            {
                min = elevator.transform.position;
                max = min + new Vector2(4f, 4f);
            }

            IntVector2 eMin = min.ToIntVector2(VectorConversions.Floor);
            IntVector2 eMax = max.ToIntVector2(VectorConversions.Ceil);

            Vector2 targetLeft = new Vector2(min.x - 1.5f, min.y + 0.5f);
            Vector2 targetRight = new Vector2(max.x + 0.5f, min.y + 0.5f);

            List<IntVector2> candidates = new List<IntVector2>();
            for (int x = eMin.x - 5; x <= eMax.x + 4; x++)
            {
                for (int y = eMin.y - 4; y <= eMin.y + 4; y++)
                {
                    candidates.Add(new IntVector2(x, y));
                }
            }

            candidates.Sort((a, b) => Score(a, targetLeft, targetRight).CompareTo(Score(b, targetLeft, targetRight)));

            foreach (IntVector2 cell in candidates)
            {
                if (IsFreeFootprint(data, room, cell, eMin, eMax))
                {
                    result = cell;
                    for (int dx = 0; dx < 2; dx++)
                    {
                        data[cell + new IntVector2(dx, 0)].isOccupied = true;
                    }
                    return true;
                }
            }
            return false;
        }

        private static float Score(IntVector2 cell, Vector2 left, Vector2 right)
        {
            Vector2 c = cell.ToVector2() + Vector2.one;
            // Small bias so the left side wins a tie
            return Mathf.Min(Vector2.Distance(c, left), Vector2.Distance(c, right) + 0.75f);
        }

        private static bool IsFreeFootprint(DungeonData data, RoomHandler room, IntVector2 origin, IntVector2 eMin, IntVector2 eMax)
        {
            for (int dx = 0; dx < 2; dx++)
            {
                for (int dy = 0; dy < 2; dy++)
                {
                    IntVector2 c = origin + new IntVector2(dx, dy);
                    if (c.x >= eMin.x && c.x < eMax.x && c.y >= eMin.y && c.y < eMax.y)
                    {
                        return false; // on the elevator itself
                    }
                    if (!data.CheckInBoundsAndValid(c))
                    {
                        return false;
                    }
                    CellData cell = data[c];
                    if (cell == null || cell.type != CellType.FLOOR || cell.isOccupied || cell.isExitCell || cell.isDoorFrameCell
                        || cell.IsPlayerInaccessible || cell.parentRoom != room)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private void Place(RoomHandler parentRoom)
        {
            room = parentRoom;

            sprite.IsPerpendicular = true;
            sprite.HeightOffGround = 0f;
            sprite.UpdateZDepth();
            SpriteOutlineManager.AddOutlineToSprite(sprite, Color.black);

            if (specRigidbody != null)
            {
                specRigidbody.Reinitialize();
            }

            spriteAnimator.Play("idle");
            room.RegisterInteractable(this);
        }

        #endregion

        #region IPlayerInteractable

        public float GetDistanceToPoint(Vector2 point)
        {
            if (sprite == null)
            {
                return 1000f;
            }

            Vector2 bottomLeft = sprite.WorldBottomLeft;
            Vector2 size = sprite.WorldTopRight - bottomLeft;
            return BraveMathCollege.DistToRectangle(point, bottomLeft, size);
        }

        public float GetOverrideMaxDistance()
        {
            return -1f;
        }

        public void OnEnteredRange(PlayerController interactor)
        {
            if (sprite == null)
            {
                return;
            }
            SpriteOutlineManager.RemoveOutlineFromSprite(sprite, true);
            SpriteOutlineManager.AddOutlineToSprite(sprite, Color.white);
        }

        public void OnExitRange(PlayerController interactor)
        {
            if (sprite == null)
            {
                return;
            }
            SpriteOutlineManager.RemoveOutlineFromSprite(sprite, true);
            SpriteOutlineManager.AddOutlineToSprite(sprite, Color.black);
        }

        public void Interact(PlayerController interactor)
        {
            // Talking again while he's mid-conversation moves on to the next line
            if (conversation != null)
            {
                skipToNext = true;
                return;
            }

            Say(BuildConversation());
        }

        public string GetAnimationState(PlayerController interactor, out bool shouldBeFlipped)
        {
            shouldBeFlipped = false;
            return string.Empty;
        }

        #endregion

        #region Talking

        /// <summary>Called when the player steps into a locked elevator. Returns false if he isn't around to say it.</summary>
        public static bool TryAnnounceLock(int needed, int have)
        {
            ElevatorOperator npc = Current;
            if (npc == null || npc.sprite == null)
            {
                return false;
            }

            // The lock hook fires every frame; only speak up again once he has finished
            if (npc.conversation != null || Time.realtimeSinceStartup - npc.lastLockLineTime < SecondsPerLine * 2f)
            {
                return true;
            }

            npc.lastLockLineTime = Time.realtimeSinceStartup;
            npc.Say(new List<string>
            {
                $"Hold it! You need Chamber Access {needed} to ride down. You've got {have}.",
                "Somebody out in the multiworld has it. Keep finding checks!",
            });
            return true;
        }

        private void Say(List<string> conversationLines)
        {
            if (conversationLines == null || conversationLines.Count == 0)
            {
                return;
            }

            if (conversation != null)
            {
                StopCoroutine(conversation);
            }

            lines = conversationLines;
            lineIndex = 0;
            conversation = StartCoroutine(Talk());
        }

        private IEnumerator Talk()
        {
            spriteAnimator.Play("talk");

            while (lineIndex < lines.Count)
            {
                skipToNext = false;
                TextBoxManager.ShowTextBox(SpeechPoint, transform, SecondsPerLine, lines[lineIndex], string.Empty, false,
                    TextBoxManager.BoxSlideOrientation.NO_ADJUSTMENT, false, false);

                float shown = 0f;
                // Mouth moves for the first part of each line, then he waits
                while (shown < SecondsPerLine && !skipToNext)
                {
                    if (shown > 1.4f && spriteAnimator.IsPlaying("talk"))
                    {
                        spriteAnimator.Play("idle");
                    }
                    shown += BraveTime.DeltaTime;
                    yield return null;
                }

                lineIndex++;
                if (lineIndex < lines.Count)
                {
                    TextBoxManager.ClearTextBox(transform);
                    spriteAnimator.Play("talk");
                }
            }

            spriteAnimator.Play("idle");
            conversation = null;
        }

        private Vector3 SpeechPoint
        {
            get { return sprite.WorldTopCenter + new Vector2(0f, 0.25f); }
        }

        private List<string> BuildConversation()
        {
            List<string> result = new List<string>();

            if (!SessionHandler.IsConnected)
            {
                result.Add("Huh, the line to the multiworld is dead.");
                result.Add("Press F8 and connect, then come back and see me.");
                return result;
            }

            int needed = ProgressionUnlocks.AccessNeededToLeaveCurrentFloor();
            int have = ProgressionUnlocks.ChamberAccessCount;

            if (ProgressionUnlocks.ChamberLocksEnabled && have < needed)
            {
                result.Add($"Sorry, this elevator's closed to you. You need Chamber Access {needed}, and you've got {have}.");
            }
            else if (ProgressionUnlocks.ChamberLocksEnabled && needed > 0)
            {
                result.Add($"You're cleared to go down. Chamber Access {have} of {ProgressionUnlocks.MaxChamberAccess}.");
            }
            else
            {
                result.Add("Elevator's all yours. Going down!");
            }

            string checks = DescribeChecks();
            if (checks.Length > 0)
            {
                result.Add(checks);
            }

            if (ProgressionUnlocks.BulletPartsEnabled && ProgressionUnlocks.BulletPartCount < ProgressionUnlocks.BulletPartIDs.Length)
            {
                result.Add($"Bullet parts found: {ProgressionUnlocks.BulletPartCount} of {ProgressionUnlocks.BulletPartIDs.Length}.");
            }

            List<string> unmet = ArchipelagoCompletion.GetAllUnmetCountsForGoals();
            if (unmet.Count == 0)
            {
                result.Add("You've done everything your goal needs. Go finish it!");
            }
            else
            {
                AddWrappedList(result, "Still on your list: ", unmet);
            }

            return result;
        }

        // The speech bubble only fits about two short lines, so long lists are spread over several bubbles
        private const int MaxBubbleChars = 70;

        private static void AddWrappedList(List<string> result, string prefix, List<string> items)
        {
            string current = prefix;
            bool first = true;
            for (int i = 0; i < items.Count; i++)
            {
                string piece = (first ? "" : ", ") + items[i];
                if (!first && current.Length + piece.Length + 1 > MaxBubbleChars)
                {
                    result.Add(current + ",");
                    current = "Also: " + items[i];
                }
                else
                {
                    current += piece;
                }
                first = false;
            }
            result.Add(current + ".");
        }

        private static string DescribeChecks()
        {
            try
            {
                var locations = SessionHandler.Session.Locations;
                List<string> parts = new List<string>();
                AddCount(parts, locations, "AP items", 8755000, 8755199);
                AddCount(parts, locations, "bosses", 8756000, 8756099);
                AddCount(parts, locations, "kill goals", 8755900, 8755999);
                return parts.Count == 0 ? "" : "Checks done: " + string.Join(", ", parts.ToArray()) + ".";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static void AddCount(List<string> parts, Archipelago.MultiClient.Net.Helpers.ILocationCheckHelper locations, string label, long first, long last)
        {
            int total = locations.AllLocations.Count(id => id >= first && id <= last);
            if (total == 0)
            {
                return;
            }
            int done = locations.AllLocationsChecked.Count(id => id >= first && id <= last);
            parts.Add($"{label} {done}/{total}");
        }

        #endregion

        // No OnDestroy override: its access level differs between the build and runtime game libraries.
        // Current compares equal to null once the floor unloads, which the checks above rely on.
    }
}
