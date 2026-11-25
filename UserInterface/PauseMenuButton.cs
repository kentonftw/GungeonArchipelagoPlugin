using System;
using ArchiGungeon.Data;
using ArchiGungeon.DebugTools;
using UnityEngine;

namespace ArchiGungeon.UserInterface
{
    /// <summary>
    /// Adds an "Archipelago" button to the game's pause menu, under its other buttons, that opens the ArchipelaGun
    /// menu. It's a copy of the menu's own Options button, so it looks and highlights the same and works with a
    /// controller (it's linked into the menu's up/down order).
    /// </summary>
    public static class PauseMenuButton
    {
        private const float RowSpacing = 45f;

        private static dfButton button;
        private static PauseMenuController controller;
        private static float nextCheck = 0f;
        private static bool linked = false;

        public static void Tick()
        {
            try
            {
                if (button == null)
                {
                    // The pause menu is rebuilt with each scene, taking our button with it
                    if (Time.unscaledTime < nextCheck) return;
                    nextCheck = Time.unscaledTime + 1f;
                    TryAdd();
                    return;
                }

                if (button.Parent != null && button.Parent.IsVisible)
                {
                    PlaceUnderLowestButton();
                    if (!linked)
                    {
                        // Done once the menu is showing, so we know which buttons are visible
                        linked = true;
                        LinkIntoNavigation();
                    }
                }
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Pause menu button: {e.Message}");
                button = null;
                nextCheck = Time.unscaledTime + 10f;
            }
        }

        private static void TryAdd()
        {
            if (!GameUIRoot.HasInstance || GameUIRoot.Instance.PauseMenuPanel == null)
            {
                return;
            }
            controller = GameUIRoot.Instance.PauseMenuPanel.GetComponent<PauseMenuController>();
            if (controller == null || controller.OptionsButton == null)
            {
                return;
            }

            dfButton template = controller.OptionsButton;
            dfControl panel = template.Parent;
            GameObject copy = UnityEngine.Object.Instantiate(template.gameObject);
            copy.name = "ArchipelagoButton";
            // Drop anything wired to the Options button's own action
            foreach (dfEventBinding binding in copy.GetComponentsInChildren<dfEventBinding>(true))
            {
                UnityEngine.Object.DestroyImmediate(binding);
            }
            button = copy.GetComponent<dfButton>();
            panel.AddControl(button);

            button.IsLocalized = false;
            button.Text = "Archipelago";
            button.Click += OnClick;

            linked = false;
            ArchDebugPrint.DebugLog(DebugCategory.UserInterface, "Added the Archipelago button to the pause menu");
        }

        private static dfButton[] GameButtons()
        {
            return new[] { controller.ReturnToGameButton, controller.BestiaryButton, controller.OptionsButton, controller.QuickRestartButton, controller.ExitToMainMenuButton, controller.QuitGameButton };
        }

        private static dfButton LowestVisible()
        {
            dfButton lowest = null;
            foreach (dfButton b in GameButtons())
            {
                if (b != null && b.IsVisible && (lowest == null || b.RelativePosition.y > lowest.RelativePosition.y))
                {
                    lowest = b;
                }
            }
            return lowest;
        }

        private static void PlaceUnderLowestButton()
        {
            dfButton lowest = LowestVisible();
            if (lowest == null) return;
            dfControl panel = button.Parent;
            float x = Mathf.Round((panel.Width - button.Width) / 2f);
            float y = lowest.RelativePosition.y + RowSpacing;
            Vector3 target = new Vector3(x, y, 0f);
            if (button.RelativePosition != target)
            {
                button.RelativePosition = target;
            }
        }

        // Down from the lowest game button goes to ours; down from ours wraps to where that used to go
        private static void LinkIntoNavigation()
        {
            dfButton lowest = LowestVisible();
            UIKeyControls ours = button.GetComponent<UIKeyControls>();
            UIKeyControls theirs = lowest != null ? lowest.GetComponent<UIKeyControls>() : null;
            if (ours == null || theirs == null)
            {
                return;
            }

            ours.up = lowest;
            ours.down = theirs.down;
            UIKeyControls wrapTarget = theirs.down != null ? theirs.down.GetComponent<UIKeyControls>() : null;
            if (wrapTarget != null && wrapTarget.up == lowest)
            {
                wrapTarget.up = button;
            }
            theirs.down = button;
        }

        private static void OnClick(dfControl control, dfMouseEventArgs args)
        {
            try
            {
                // Closes the pause menu the way its own Resume button does (it's private in the game)
                System.Reflection.MethodInfo resume = typeof(PauseMenuController).GetMethod("DoReturnToGame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (resume != null)
                {
                    resume.Invoke(controller, new object[] { control, args });
                }
                else
                {
                    GameManager.Instance.Unpause();
                }
            }
            catch (Exception e)
            {
                ArchDebugPrint.DebugLog(DebugCategory.UserInterface, $"Couldn't close the pause menu: {e.Message}");
                GameManager.Instance.Unpause();
            }
            ArchipelagoGUI.Instance?.OnOpen();
        }
    }
}
