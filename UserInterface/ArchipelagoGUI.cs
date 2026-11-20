using BepInEx.Logging;
using SGUI;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ArchiGungeon.ArchipelagoServer;
using ArchiGungeon.DebugTools;
using ArchiGungeon.Data;


namespace ArchiGungeon.UserInterface
{
    #region Mod Console Defition
    public delegate void MenuEvent();

    // Front end Archipelago menu
    public class ArchipelagoGUI : ETGModMenu
    {
        // bool & event for pausing gameplay & user control
        public static bool IsOpen;
        private static bool IsHoldingInput = true;
        public static MenuEvent OnMenuClose;
        public static MenuEvent OnMenuOpen;

        // Hook into ETG console logger
        private static ManualLogSource Logger = ETGModConsole.Logger;


        // Text input vars
        private List<string> lastCommands = new List<string>();
        private int currentCommandIndex = -1;
        protected static char[] _SplitArgsCharacters = new char[1] { ' ' };
        public static string passwordInput = null;
        public static string manualNameInput = "UNSET";

        // Backend class for bridging Gungeon to Archipelago
        private static ArchipelConsoleCommandParser archipelagoCommands = new();

        // The current instance of the GUI class
        public static ArchipelagoGUI Instance { get; protected set; }

        #region Initializing GUI
        public ArchipelagoGUI()
        {
            ArchDebugPrint.DebugLog(DebugCategory.PluginStartup, "Creating ArchiGungeon GUI");
            Instance = this;
            Logger = BepInEx.Logging.Logger.CreateLogSource("Archipelago Mod Menu");
            return;
        }

        public override void Start()
        {
            InitGUI();   
        }

        private void InitGUI()
        {
            ArchDebugPrint.DebugLog(DebugCategory.PluginStartup, "Initializing text for GUI");
            base.GUI = new SGroup
            {
                Visible = false,
                OnUpdateStyle = delegate (SElement elem)
                {
                    elem.Fill(100); //empty
                    elem.Position = new Vector2(elem.Position.x, elem.Position.y);
                    //elem.Size = new Vector2(elem.Size.x, elem.Size.y - 100f);
                },
                Children =
            {   
                // Initial text elements
                new SGroup
                {
                    Background = new UnityEngine.Color(0f, 153f, 0f, 0f), //0,0,0
                    AutoLayout = (SGroup g) => g.AutoLayoutVertical,
                    ScrollDirection = SGroup.EDirection.Vertical,
                    OnUpdateStyle = delegate(SElement elem)
                    {
                        elem.Fill();
                        elem.Size -= new Vector2(0f, elem.Backend.LineHeight - 4f);
                    },
                    Children =
                    {
                    }
                },

                // Text entry field, with hooks for up/down/escape/submit
                new STextField
                {
                    OnUpdateStyle = delegate(SElement elem)
                    {
                        elem.Size.x = elem.Parent.InnerSize.x;
                        elem.Position.x = 0f;
                        elem.Position.y = elem.Parent.InnerSize.y - elem.Size.y;
                    },

                    OverrideTab = true,
                    OnKey = delegate(STextField field, bool keyDown, KeyCode keyCode)
                    {
                        ParseKeyboardInput(field, keyDown, keyCode);
                    },
                    OnSubmit = delegate(STextField elem, string text)
                    {
                        // Add new entry to entry list
                        int index = lastCommands.Count - 1;
                        if (text != string.Empty && (lastCommands.Count <= 0 || text != lastCommands[index]))
                        {
                            lastCommands.Add(text);
                        }
                        currentCommandIndex = lastCommands.Count;

                        // Parse command
                        if (text.Length != 0)
                        {
                            ParseCommandForArchipelago(text.Trim());
                        }
                    }
                }
            }
            };

            PrintHelpTextToConsole();

            return;
        }
        #endregion

        #region General GUI Commands
        private void PrintHelpTextToConsole()
        {
            foreach (SLabel helpText in ModMenuText.HelpCommandText)
            {
                base.GUI[0].Children.Add(helpText);
                ((SGroup)base.GUI[0]).ScrollPosition.y = float.MaxValue;
            }
        }

        private static void PrintHelpTextToMenu()
        {
            foreach (SLabel helpText in ModMenuText.HelpCommandText)
            {
                ArchipelagoMenu.AddLog(helpText.Text.TrimEnd('\n'));
            }
        }

        private void PrintDebugTextToConsole()
        {

            ConsoleLog("==================  FOLLOWING ARE DEBUG COMMANDS");

            foreach(string commadText in DebugCommands.InputToCommand.Keys)
            {
                ConsoleLog(commadText);
            }

            return;
        }

        protected virtual SLabel _Log(object text, Texture image)
        {
            Logger.LogMessage(text);
            SLabel sLabel = new SLabel(text.ToString())
            {
                Icon = image
            };

            try
            {
                base.GUI[0].Children.Add(sLabel);
            }
            catch (IndexOutOfRangeException) 
            {
                base.GUI[0].Children.Clear();
                ((SGroup)base.GUI[0]).ContentSize.y = 0f;
                ((SGroup)base.GUI[0]).ScrollPosition.y = 0f;

                PrintHelpTextToConsole();

                base.GUI[0].Children.Add(sLabel);

            }

            
            ((SGroup)base.GUI[0]).ScrollPosition.y = float.MaxValue;
            return sLabel;
        }

        // Log internally in Archipelago & ETGModConsole menus
        public static SLabel ConsoleLog(object text, bool debuglog = false)
        {
            DebugFileWriter.AppendToLocalDebugLog(text.ToString());
            ArchipelagoMenu.AddLog(text.ToString());

            if (Instance == null)
            {
                new ArchipelagoGUI();
            }

            SLabel result = Instance?._Log(text, null);
            if (debuglog)
            {
                Debug.Log(text);
            }

            ETGModConsole.Log(text);

            return result;
        }

        private string[] SplitArgsBySpace(string args)
        {
            return args.Split(_SplitArgsCharacters, StringSplitOptions.RemoveEmptyEntries);
        }

        private void ParseKeyboardInput(STextField field, bool keyDown, KeyCode keyCode)
        {
            if ((keyDown && (keyCode != 0) == false))
            {
                return;
            }

            if (keyCode == ETGModGUI.CloseAllKey || keyCode == KeyCode.Escape)
            {
                OnClose();
                return;
            }

            if (keyCode == ETGModGUI.ConsolePreviousCommand || keyCode == ETGModGUI.ConsoleNextCommand)
            {
                IsHoldingInput = false;

                if (lastCommands.Count <= 0)
                {
                    field.Text = string.Empty;
                    return;
                }

                if (keyCode == ETGModGUI.ConsolePreviousCommand)
                {
                    currentCommandIndex--;
                    if (currentCommandIndex <= 0)
                    {
                        currentCommandIndex = 0;
                    }
                }
                else if (keyCode == ETGModGUI.ConsoleNextCommand)
                {
                    currentCommandIndex++;
                    if (currentCommandIndex >= lastCommands.Count)
                    {
                        currentCommandIndex = lastCommands.Count - 1;
                    }
                }

                field.Text = lastCommands[currentCommandIndex];
                field.MoveCursor(field.Text.Length);
            }

            return;
        }

        public override void Update()
        {
            if (IsOpen)
            {
                ApplyInputLockToCurrentPlayers();
            }

            if (!IsHoldingInput)
            {
                IsHoldingInput = true;
            }

            return;
        }

        // The SGUI console is no longer drawn; the ArchipelaGun window (ArchipelagoMenu) replaces it.
        // These stay the single entry points so the menu key, the window and connect all agree.
        public override void OnOpen()
        {
            IsOpen = true;
            SetPlayerInputLock(true);
            ArchipelagoMenu.Instance?.Open();

            return;
        }

        // While the menu is open, typing shouldn't also move, shoot, dodge or use items.
        // Uses the game's own input override (the same lock cutscenes use), keyed by this reason.
        private const string InputLockReason = "ArchipelagoConsole";
        // Players currently holding our lock. Tracked per player because the game can replace the player
        // object while the menu is open (Paradox Mode character swaps), and the new one needs the lock too.
        private static readonly List<PlayerController> lockedPlayers = new List<PlayerController>();

        private static void SetPlayerInputLock(bool locked)
        {
            if (locked)
            {
                ApplyInputLockToCurrentPlayers();
                return;
            }

            foreach (PlayerController player in lockedPlayers)
            {
                if (player != null)
                {
                    player.ClearInputOverride(InputLockReason);
                }
            }

            if (lockedPlayers.Count > 0)
            {
                UnityEngine.Debug.Log($"[ArchipelaGun] Menu closed, player input unlocked ({lockedPlayers.Count} player(s))");
            }
            lockedPlayers.Clear();
        }

        // Called on open and every frame while open, so a newly spawned player is locked as well
        private static void ApplyInputLockToCurrentPlayers()
        {
            if (GameManager.Instance == null || GameManager.Instance.AllPlayers == null)
            {
                return;
            }

            lockedPlayers.RemoveAll(player => player == null);

            foreach (PlayerController player in GameManager.Instance.AllPlayers)
            {
                if (player == null || lockedPlayers.Contains(player))
                {
                    continue;
                }

                player.SetInputOverride(InputLockReason);
                lockedPlayers.Add(player);
                UnityEngine.Debug.Log($"[ArchipelaGun] Menu open, player input locked: {player.name} (input state now {player.CurrentInputState})");
            }
        }


        public override void OnClose()
        {
            if(SessionHandler.IsGoalsTextBoxOpen)
            {
                ArchDebugPrint.DebugLog(DebugCategory.UserInterface, "Closing goals box first");
                SessionHandler.HideGoalsTextbox();
                return;
            }

            ArchipelagoMenu.Instance?.Close();
            OnMenuClose?.Invoke();
            IsOpen = false;
            SetPlayerInputLock(false);

            return;
        }

        #endregion

        #region Archipelago Command Frontend
        public void SendCommandFromEtGConsoleToArchipelago(string[] commands)
        {
            string joinCommands = String.Join(" ", commands);

            ParseCommandForArchipelago(joinCommands);
        }

        private static readonly HashSet<string> knownCommands = new HashSet<string>
        {
            ArchipelConsoleCommandParser.connectCmd, ArchipelConsoleCommandParser.retrieveCmd, ArchipelConsoleCommandParser.enemyShuffleCmd,
            ArchipelConsoleCommandParser.setConnectionParameterCmd, ArchipelConsoleCommandParser.fullConnectCmd,
            ArchipelConsoleCommandParser.disconnectCmd, ArchipelConsoleCommandParser.reconnectCmd, ArchipelConsoleCommandParser.deathlinkCmd,
            ArchipelConsoleCommandParser.debugCmd, ArchipelConsoleCommandParser.unitTestCmd, "help",
        };

        /// <summary>From the menu's Console tab: mod commands run here, anything else goes to the server chat.</summary>
        public void RunCommand(string text)
        {
            string first = SplitArgsBySpace(text).FirstOrDefault() ?? "";

            if (text.StartsWith("!") || !knownCommands.Contains(first.ToLowerInvariant()))
            {
                if (!SessionHandler.IsConnected)
                {
                    ArchipelagoMenu.AddLog("Not connected, so that can't go to the server. Type help for the mod's commands.");
                    return;
                }
                SessionHandler.DataSender.SendChatMessage(text);
                return;
            }

            if (first.ToLowerInvariant() == "help")
            {
                PrintHelpTextToMenu();
                return;
            }

            try
            {
                ParseCommandForArchipelago(first.ToLowerInvariant() + text.Substring(first.Length));
            }
            catch (Exception e)
            {
                ArchipelagoMenu.AddLog($"That command is missing something: {e.Message}. Type help for the command list.");
            }
        }

        protected void ParseCommandForArchipelago(string command)
        {
            string[] commandInputs;
            string commandGroup;

            // Command group, sub command group
            string[] array = SplitArgsBySpace(command);

            commandInputs = array.Skip(1).ToArray();
            commandGroup = array[0];


            string consoleCommand;

            switch (commandGroup)
            {
                case ArchipelConsoleCommandParser.connectCmd:
                {
                        if (commandInputs.Length > 3)
                        {
                            ConsoleLog("ERROR: Too many inputs - [Connect] expected input [IP Address] [Port] [Player Slot Name]");
                            ConsoleLog("If name contains spaces or server has a password, please use the 'set' & 'fullconnect' commands");
                            return;
                        }

                        consoleCommand = $"{ArchipelConsoleCommandParser.archipelagoCommandGroup} {ArchipelConsoleCommandParser.connectCmd} " + command;
                        break;
                }

                case ArchipelConsoleCommandParser.retrieveCmd:
                {
                        consoleCommand = $"{ArchipelConsoleCommandParser.archipelagoCommandGroup} {ArchipelConsoleCommandParser.retrieveCmd} ";
                        break;
                }

                case ArchipelConsoleCommandParser.enemyShuffleCmd:
                {
                        if (commandInputs.Length > 1)
                        {
                            ConsoleLog("ERROR: Too many inputs - [Enemy Shuffle] expected input [Enemy Shuffle Mode Number]");
                            return;
                        }

                        bool IsValid = Int32.TryParse(commandInputs[0], out int convertedInt);

                        if(IsValid)
                        {
                            SessionHandler.SetEnemyShuffleMode(convertedInt);
                        }
                        else
                        {
                            ConsoleLog("ERROR: Invalid input - [Enemy Shuffle] expected input [Enemy Shuffle Mode Number]");
                        }
                        

                        return;
                }

                /*
                case ArchipelConsoleCommandParser.progressCmd:
                {
                        consoleCommand = $"{ArchipelConsoleCommandParser.archipelagoCommandGroup} {ArchipelConsoleCommandParser.progressCmd} ";
                        break;
                }
                */

                case ArchipelConsoleCommandParser.setConnectionParameterCmd:
                {
                        // value 0 is sub command group, value 1 is value to set
                        string fullValue = string.Join(" ", array.Skip(2).ToArray());

                        SetConnectionParameter(commandInputs[0], fullValue);
                        return;
                }

                case ArchipelConsoleCommandParser.fullConnectCmd:
                {
                        if (commandInputs.Length > 2)
                        {
                            ConsoleLog("ERROR: Too many inputs - [fullconnect] expected input [IP Address] [Port]");
                            ConsoleLog("Use 'set' for name & password");
                            return;
                        }

                        SessionHandler.ArchipelagoConnect(commandInputs[0], commandInputs[1], ArchipelagoGUI.manualNameInput, ArchipelagoGUI.passwordInput);

                        return;
                }

                case ArchipelConsoleCommandParser.disconnectCmd:
                {
                        SessionHandler.DisconnectFromSession();
                        return; 
                }

                case ArchipelConsoleCommandParser.reconnectCmd:
                {
                        SessionHandler.ReconnectSession();
                        return;
                }

                case ArchipelConsoleCommandParser.deathlinkCmd:
                {
                        if (commandInputs.Length > 1)
                        {
                            ConsoleLog("ERROR: Too many inputs - [Deathlink] expected input [Deathlink Mode Number]");
                            return;
                        }

                        bool IsValid = Int32.TryParse(commandInputs[0], out int convertedInt);

                        if(IsValid)
                        {
                            SessionHandler.SetDeathLinkMode(convertedInt);
                        }

                        else
                        {
                            ConsoleLog("ERROR: Invalid input - [Deathlink] expected input [Deathlink Mode Number]");
                        }

                        return;
                }

                case ArchipelConsoleCommandParser.debugCmd:
                {
                        if(commandInputs.Length > 1)
                        {
                            DebugCommands.HandleCommand(commandInputs[0], additionalInput: commandInputs[1]);

                            return;
                        }

                        if(commandInputs.Length > 0)
                        {
                            DebugCommands.HandleCommand(commandInputs[0]);
                            
                            return;
                        }

                        PrintDebugTextToConsole();

                        return;
                }
                case ArchipelConsoleCommandParser.unitTestCmd:
                {
                        UnitTests.HandleUnitTestCommand(commandInputs[0]);
                        return;
                }
                case "help":
                {
                        PrintHelpTextToConsole();
                        return;
                }

                default:
                {
                        ConsoleLog($"ERROR: '{commandGroup}' not recognized as a command");
                        return;
                }
            }

            archipelagoCommands.SendETGConsoleCommand(consoleCommand);
            return;
        }

        public static void SetConnectionParameter(string paramToSet, string textValue)
        {
            switch (paramToSet)
            {
                case "name":
                    SetNameInput(textValue);

                    ConsoleLog("Name set to: " + textValue);

                    break;
                case "password":
                    SetPasswordInput(textValue);

                    ConsoleLog("Password set to: " + textValue);

                    break;
                default:
                    ConsoleLog(paramToSet + "is not a valid connection setting to change.");
                    break;
            }

            return;
        }


        private static void SetNameInput(string nameToSet)
        {
            manualNameInput = nameToSet;
            return;
        }

        private static void SetPasswordInput(string password)
        {
            passwordInput = password;
            return;
        }

        #endregion
    }
    #endregion

    #region Command Text References
    public class ModMenuText
    {
        public static List<SLabel> HelpCommandText { get; } = new List<SLabel>()
        {
            new SLabel("==== <color=#ffffffff>Archipelago</color> Gungeon Menu ====") { Foreground = UnityEngine.Color.green },
            new SLabel("Press <color=#f4d03f>ESC</color> at any time to close"){ Foreground = UnityEngine.Color.white},
            new SLabel("Enter <color=#f4d03f>'help'</color> to print all commands\n") { Foreground = UnityEngine.Color.blue },

            new SLabel("The following commands are available:\n") { Foreground = UnityEngine.Color.green },

            // CONNECT
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.connectCmd}</color> [IP address] [port] [player slot]") { Foreground = UnityEngine.Color.white },
            new SLabel("    Connect to Archipelago") { Foreground = UnityEngine.Color.green },

            // RECONNECT!!
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.reconnectCmd}</color>") { Foreground = UnityEngine.Color.white },
            new SLabel("    Reconnect to last valid connection. Disconnects and connects if already online") { Foreground = UnityEngine.Color.green },

            //FULL CONNECT
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.fullConnectCmd}</color> [IP address] [port]") { Foreground = UnityEngine.Color.white },
            new SLabel("    Connect to Archipelago using set Name & Password using the 'set' command") { Foreground = UnityEngine.Color.green },
            new SLabel("    Use for player name with spaces OR server with password") { Foreground = UnityEngine.Color.green },

            // Set 
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.setConnectionParameterCmd}</color> [setting name] [value]") { Foreground = UnityEngine.Color.white },
            new SLabel("    Set 'name' or 'password' for use with the 'fullconnect' command") { Foreground = UnityEngine.Color.green },

            // DISCONNECT
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.disconnectCmd}</color>") { Foreground = UnityEngine.Color.white },
            new SLabel("    Disconnect from any connected sessions\n\n") { Foreground = UnityEngine.Color.green },

            // RETRIEVE
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.retrieveCmd}</color>") { Foreground = UnityEngine.Color.white },
            new SLabel("    Collect items from server (once per item per Run)") { Foreground = UnityEngine.Color.green },

            // DEATHLINK
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.deathlinkCmd}</color> [deathlink mode number]") { Foreground = UnityEngine.Color.white },
            new SLabel("    Set deathlink mode") { Foreground = UnityEngine.Color.green },
            new SLabel("    0 - Deathlink OFF, 1 - Basic Deathlink, 2 - Advanced Co-Op Deathlink") { Foreground = UnityEngine.Color.green },

            // ENEMY
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.enemyShuffleCmd}</color> [enemy shuffle mode number]") { Foreground = UnityEngine.Color.white},
            new SLabel("    Set enemy shuffle mode") { Foreground = UnityEngine.Color.green },
            new SLabel("    0 - Enemy Shuffle OFF, 1 - Enemy Shuffle ON") { Foreground = UnityEngine.Color.green },

            // GOAL
            //new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.progressCmd}</color>") { Foreground = UnityEngine.Color.white },
            //new SLabel("    Display game completion goal progress\n\n") { Foreground = UnityEngine.Color.green },


            // --- Debug
            new SLabel($"<color=#f4d03f>{ArchipelConsoleCommandParser.debugCmd}</color> [command string]") { Foreground = UnityEngine.Color.blue },
            new SLabel("    Handle Mod Debug, leave command empty to print available commands ----------") { Foreground = UnityEngine.Color.yellow },
            new SLabel("") { Foreground = UnityEngine.Color.green }
        };
    }
    #endregion

    #region Archipelago ETG Mod Console Hook
    public class ArchipelConsoleCommandParser
    {

        // Summary:
        // The current instance of the command parser.
        public static ArchipelConsoleCommandParser Instance { get; protected set; }

        public const string archipelagoCommandGroup = "arch";

        public const string connectCmd = "connect";
        public const string reconnectCmd = "reconnect";
        public const string fullConnectCmd = "fullconnect";
        public const string setConnectionParameterCmd = "set";
        public const string disconnectCmd = "disconnect";

        public const string retrieveCmd = "retrieve";
        //public const string progressCmd = "progress";

        public const string deathlinkCmd = "deathlink";
        public const string enemyShuffleCmd = "enemy";

        public const string debugCmd = "debug";
        public const string unitTestCmd = "test";
        
        
        // Instance archipelago commands inside ETGModConsole
        public ArchipelConsoleCommandParser() 
        {
            Instance = this;

            // Add commands for use by base ETGModConsole
            ETGModConsole.CommandDescriptions.Add($"{archipelagoCommandGroup}", "-- Archipelago Multiworld randomizer mod --");
            ETGModConsole.CommandDescriptions.Add($"{archipelagoCommandGroup} {connectCmd}", "[ip] [port] [player name] - Connect to server, seperated by spaces");
            ETGModConsole.CommandDescriptions.Add($"{archipelagoCommandGroup} {reconnectCmd}", "Connect to last valid connection, disconnects and connects if already online");
            ETGModConsole.CommandDescriptions.Add($"{archipelagoCommandGroup} {disconnectCmd}", "Disconnect from server");
            ETGModConsole.CommandDescriptions.Add($"{archipelagoCommandGroup} {retrieveCmd}", "Force pull received location items from server (once per run)");
            ETGModConsole.CommandDescriptions.Add($"{archipelagoCommandGroup} console", "[input #1] [input #2] [etc] - Send commands to Archipelago console manager");
            //ETGModConsole.CommandDescriptions.Add($"{archipelagoCommandGroup} {progressCmd}", "Output randomizer completion progress");

            ETGModConsole.Commands.AddGroup($"{archipelagoCommandGroup}");

            new SessionHandler();

            // Hook archipelago commands to ETG mod console delegate events
            ETGModConsole.Commands.GetGroup($"{archipelagoCommandGroup}").AddGroup($"{connectCmd}", delegate (string[] args)
            {
                // arg[0] is the command group for archipelago so START on args[1]
                if (args.Length > 3)
                {  SessionHandler.ArchipelagoConnect(args[1], args[2], args[3]); }

                else
                { SessionHandler.ArchipelagoConnect(args[0], args[1], args[2]); }

                return;
            });

            ETGModConsole.Commands.GetGroup($"{archipelagoCommandGroup}").AddGroup($"{reconnectCmd}", delegate (string[] args)
            {
                SessionHandler.ReconnectSession();
                return;
            });
            ETGModConsole.Commands.GetGroup($"{archipelagoCommandGroup}").AddGroup($"{disconnectCmd}", delegate (string[] args)
            {
                SessionHandler.DisconnectFromSession();
                return;
            });

            ETGModConsole.Commands.GetGroup($"{archipelagoCommandGroup}").AddGroup($"{retrieveCmd}", delegate (string[] args)
            {
                SessionHandler.RetrieveItemsFromServer();
                return;
            });

            ETGModConsole.Commands.GetGroup($"{archipelagoCommandGroup}").AddGroup($"console", delegate (string[] args)
            {
                ArchipelagoGUI.Instance.SendCommandFromEtGConsoleToArchipelago(args);
                return;
            });

            /*
            ETGModConsole.Commands.GetGroup($"{archipelagoCommandGroup}").AddGroup($"{progressCmd}", delegate (string[] args)
            {
                SessionHandler.OutputGameGoalStatus();
                return;
            });
            */
        }

        public void SendETGConsoleCommand(string command)
        {
            ETGModConsole.Instance?.ParseCommand(command);
        }

    }
    #endregion

}
