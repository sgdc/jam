using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;
using NLog;

namespace Launcher
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static readonly DependencyProperty NoGamesVisibilityProperty = DependencyProperty.Register("NoGamesVisibility", typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Collapsed));
        public static readonly DependencyProperty LoadingGamesVisibilityProperty = DependencyProperty.Register("LoadingGamesVisibility", typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Visible));
        public static readonly DependencyProperty AvaliableGamesProperty = DependencyProperty.Register("AvaliableGames", typeof(ObservableCollection<GameElement>), typeof(MainWindow), new PropertyMetadata(new ObservableCollection<GameElement>()));
        public static readonly DependencyProperty GameExecutingProperty = DependencyProperty.Register("GameExecuting", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        private static int versionNotificationReset; // (Days) If game is new or updated, how many days before it is no longer "updated" or "new"?

        #region Input Variables

        private const int KEYS_START = 0;
        private const int KEYS_START_OFFSET = KEYS_START + 1;
        private const int KEYS_START_COUNT = KEYS_START_OFFSET + 1;
        private const int KEYS_UP = KEYS_START_OFFSET + 1;
        private const int KEYS_DOWN = KEYS_UP + 1;
        private const int KEYS_LEFT_RIGHT_INDEX_START = KEYS_DOWN + 1;
        private const int KEYS_LEFT = KEYS_DOWN + 1;
        private const int KEYS_RIGHT = KEYS_LEFT + 1;

        private double scrollRepeatDelay; // ms between scrolling when pressing and holding a key
        private DateTime lastScrollTime = DateTime.UtcNow;
        private int scrollRepeatCount = 0;
        private Key[][] inputKeys;

        #endregion

        //XXX would much prefer a enumeration, but CompareExchange doesn't like enums
        private const int GAME_EXEC_STATE_NOT_RUNNING = 0;
        private const int GAME_EXEC_STATE_STARTING = 1;
        private const int GAME_EXEC_STATE_RUNNING = 2;
        private int gameRunningState = GAME_EXEC_STATE_NOT_RUNNING;

        private Logger log;
        private IDisposable versionUpdateTimer = null;
        private AudioController<CoreAudioDevice> audioController;

        private InputHook hook;
        private IDisposable hookTimer = null;

        private TimeSpan gameRespondingPolling;
        private TimeSpan closeGameOnNoInputTimeout;

        #region Configs

        private static Config GameConfig;
        private static Config LauncherConfig;

        static MainWindow()
        {
            Func<string, System.IO.StreamReader, Logger, object> id = (firstLine, sr, log) =>
            {
                return firstLine;
            };

            #region Game Config

            /*
             * "*info*.ini
             * 
             * Format ([key]
             *         value
             *         % Comment):
             * - [optional] int "SupportedPlayers" <supported player count. Between 2 and 4>
             * - string "Title" <game name>
             * - [optional] string "Description" <game description, can be multiple lines>
             * - [optional] string "Arguments" <game arguments>
             * - [optional] string "Version" <game version X.X.X.X>
             * - [optional] int "Volume" <game volume vetween 0 and 100>
             */

            GameConfig = new Config(new ConfigParseOption
            {
                Name = "SupportedPlayers",
                Parser = (firstLine, sr, log) =>
                {
                    int playerCount;
                    if (int.TryParse(firstLine, out playerCount))
                    {
                        log.Info("Got player count: {0}", playerCount);
                        if (playerCount < 2 || playerCount > 4)
                        {
                            log.Warn("Player count must be between 2 and 4");
                        }
                        return Math.Max(2, Math.Min(4, playerCount));
                    }
                    else
                    {
                        log.Warn("Could not parse player count: \"{0}\"", firstLine);
                    }
                    return null;
                }
            }, new ConfigParseOption
            {
                Name = "Title",
                Parser = id
            }, new ConfigParseOption
            {
                Name = "Description",
                Parser = (firstLine, sr, log) =>
                {
                    var builder = new StringBuilder();
                    var c = -1;
                    do
                    {
                        if (builder.Length > 0)
                        {
                            builder.Append(Environment.NewLine);
                            builder.Append(sr.ReadLine());
                        }
                        else
                        {
                            builder.Append(firstLine);
                        }
                        c = sr.Peek();
                    } while (c != '[' && c > 0);
                    return builder.ToString().TrimEnd();
                }
            }, new ConfigParseOption
            {
                Name = "Arguments",
                Parser = id
            }, new ConfigParseOption
            {
                Name = "Version",
                Parser = (firstLine, sr, log) =>
                {
                    return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
                }
            }, new ConfigParseOption
            {
                Name = "Volume",
                Parser = (firstLine, sr, log) =>
                {
                    int volume;
                    if (!int.TryParse(firstLine, out volume))
                    {
                        return null;
                    }
                    return volume;
                }
            });

            #endregion

            #region Launcher Config

            /*
             * Config.ini
             * 
             * Everything is optional
             * 
             * Format ([key]
             *         value
             *         % Comment):
             * - Key[,Key,...] "PlayerStartKeys" <The keys used to start a game, ordered: 1 player, 2 player, 3 player, etc.>
             * - Key[,Key,...] "PlayerStartKeyOffsets" <Used in conjunction with PlayerStartKeys, it is used to subtract the key value from the start key to get the player number. 
             *                  So if '1' is pressed, and the offset is '0', it will indicate 1 player. The first value will be used if not enough offsets exist for all keys>
             * - Key[,Key,...] "LeftMovementKeys" <Keys used to indicate "move left" to the launcher UI>
             * - Key[,Key,...] "RightMovementKeys" <Keys used to indicate "move right" to the launcher UI>
             * - Key[,Key,...] "UpMovementKeys" <Keys used to indicate "move up" to the launcher UI>
             * - Key[,Key,...] "DownMovementKeys" <Keys used to indicate "move down" to the launcher UI>
             * - double "ScrollRepeatDelay" <How long to delay scrolling, in milliseconds, without a new key input to scroll. If short, then it will very rapidly start to scroll 
             *                  through all games. Else, it will take a while>
             * - double "DaysBeforeVersionNotificationReset" <How long, in days, before notifcations on new/updated games are reset so they don't indicate new or updated.>
             * - TimeSpan "GameRespondingCheck" <How often to check if the game is responding. A 2.5 sec delay occurs before this runs. Look at TimeSpan.Parse remarks on MSDN for format. Must be >= 0.>
             * - TimeSpan "CloseGameAfterNoInputTimeout" <How long before a game is closed from lack of input, so the Launcher shows again. Look at TimeSpan.Parse remarks on MSDN for format. Must be >= 0.>
             * - Folder[,Folder,...] "DisableKeyboardHookGames" <Don't use keyboard hooks on the following games>
             * - bool "DisableKeyboardHook" <Don't use keyboard hooks on any game>
             * - bool "AlwaysLogGameHookCompatability" <Always test keyboard hook compatability and log failures. Ignores DisableKeyboardHook and DisableKeyboardHookGames, but if hooks are disabled for a game(s), it will not enable hooks. This could crash some games>
             * - bool "SkipGameHookCompatabilityCheck" <If hooks are enabled, skip the compatability check. This could cause the launcher, game, or both to crash. Ignores AlwaysLogGameHookCompatability>
             */

            Func<string, System.IO.StreamReader, Logger, object> keyArrayParser = (firstLine, sr, log) =>
            {
                var keys = firstLine.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                List<Key> keyArray = new List<Key>();
                foreach (var key in keys)
                {
                    Key parsedKey;
                    if (Enum.TryParse(key, out parsedKey))
                    {
                        keyArray.Add(parsedKey);
                    }
                    else
                    {
                        log.Warn("Could not parse '{0}', unknown key.", key);
                    }
                }
                return keyArray.Count > 0 ? keyArray.ToArray() : null;
            };
            Func<string, System.IO.StreamReader, Logger, object> timespanParser = (firstLine, sr, log) =>
            {
                TimeSpan span;
                if (TimeSpan.TryParse(firstLine, out span) && span > TimeSpan.Zero)
                {
                    return span;
                }
                return null;
            };
            Func<string, System.IO.StreamReader, Logger, object> boolParser = (firstLine, sr, log) =>
            {
                bool val;
                if (Boolean.TryParse(firstLine, out val))
                {
                    return val;
                }
                return null;
            };
            Func<string, System.IO.StreamReader, Logger, object> stringArrayParser = (firstLine, sr, log) =>
            {
                var strings = firstLine.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> stringArray = new List<string>();
                foreach (var str in strings)
                {
                    string stringValue = str.Trim();
                    if (string.IsNullOrEmpty(stringValue))
                    {
                        log.Warn("Could not parse '{0}', string is null, empty, or whitespace.", stringValue);
                    }
                    else
                    {
                        stringArray.Add(stringValue);
                    }
                }
                return stringArray.Count > 0 ? stringArray.ToArray() : null;
            };

            LauncherConfig = new Config(new ConfigParseOption
            {
                Name = "PlayerStartKeys",
                Parser = keyArrayParser
            }, new ConfigParseOption
            {
                Name = "PlayerStartKeyOffsets",
                Parser = keyArrayParser
            }, new ConfigParseOption
            {
                Name = "LeftMovementKeys",
                Parser = keyArrayParser
            }, new ConfigParseOption
            {
                Name = "RightMovementKeys",
                Parser = keyArrayParser
            }, new ConfigParseOption
            {
                Name = "UpMovementKeys",
                Parser = keyArrayParser
            }, new ConfigParseOption
            {
                Name = "DownMovementKeys",
                Parser = keyArrayParser
            }, new ConfigParseOption
            {
                Name = "ScrollRepeatDelay",
                Parser = (firstLine, sr, log) =>
                {
                    double result;
                    if (!double.TryParse(firstLine, out result))
                    {
                        log.Warn("Could not parse scroll repeat delay: {0}", firstLine);
                        return null;
                    }
                    return result;
                }
            }, new ConfigParseOption
            {
                Name = "DaysBeforeVersionNotificationReset",
                Parser = (firstLine, sr, log) =>
                {
                    int result;
                    if (!int.TryParse(firstLine, out result))
                    {
                        log.Warn("Could not parse the number of days before the version notification was reset: {0}", firstLine);
                        return null;
                    }
                    return result;
                }
            }, new ConfigParseOption
            {
                Name = "GameRespondingCheck",
                Parser = timespanParser
            }, new ConfigParseOption
            {
                Name = "CloseGameAfterNoInputTimeout",
                Parser = timespanParser
            }, new ConfigParseOption
            {
                Name = "DisableKeyboardHookGames",
                Parser = stringArrayParser
            }, new ConfigParseOption
            {
                Name = "DisableKeyboardHook",
                Parser = boolParser
            }, new ConfigParseOption
            {
                Name = "AlwaysLogGameHookCompatability",
                Parser = boolParser
            }, new ConfigParseOption
            {
                Name = "SkipGameHookCompatabilityCheck",
                Parser = boolParser
            });

            #endregion
        }

        #endregion

        public MainWindow()
        {
            // Get log. First thing
            log = LogManager.GetLogger("launcher");

            // Make sure the UI knows where to get data bindings from (yes, this is weird... I thought it should've been implicit, but doesn't seem to be the case)
            this.DataContext = this;

            // Setup input hook
            hook = new InputHook();

            #region Launcher Config

            // Load config
            var configPath = System.IO.Path.Combine(Environment.CurrentDirectory, "Config.ini");
            if (System.IO.File.Exists(configPath))
            {
                log.Info("Loading launcher config from '%s'", configPath);
                LauncherConfig.Load(configPath, log);
            }
            // Default keys are associated with Page 6 of X-Arcade Manual.
            Key[] configStartKeys = LauncherConfig.GetValue<Key[]>("PlayerStartKeys", new Key[] { Key.D1, Key.D2, Key.D3, Key.D4 });
            Key[] configStartOffsetKeys = LauncherConfig.GetValue<Key[]>("PlayerStartKeyOffsets", new Key[] { Key.D0 });
            Key[] configLeftKeys = LauncherConfig.GetValue<Key[]>("LeftMovementKeys", new Key[] { Key.NumPad4, Key.D });
            Key[] configRightKeys = LauncherConfig.GetValue<Key[]>("RightMovementKeys", new Key[] { Key.NumPad6, Key.G });
            Key[] configUpKeys = LauncherConfig.GetValue<Key[]>("UpMovementKeys", new Key[] { Key.NumPad8, Key.R });
            Key[] configDownKeys = LauncherConfig.GetValue<Key[]>("DownMovementKeys", new Key[] { Key.NumPad2, Key.F });

            if (configStartOffsetKeys.Length < configStartKeys.Length)
            {
                configStartOffsetKeys = configStartOffsetKeys.Concat(Enumerable.Repeat(configStartOffsetKeys[0], configStartKeys.Length - configStartOffsetKeys.Length)).ToArray();
            }
            inputKeys = new Key[][]
            {
                configStartKeys,
                configStartOffsetKeys,
                configUpKeys,
                configDownKeys,
                configLeftKeys,
                configRightKeys
            };

            scrollRepeatDelay = LauncherConfig.GetValue<double>("ScrollRepeatDelay", 250.0);
            versionNotificationReset = LauncherConfig.GetValue<int>("DaysBeforeVersionNotificationReset", 14);

            gameRespondingPolling = LauncherConfig.GetValue<TimeSpan>("GameRespondingCheck", TimeSpan.FromMilliseconds(5000));
            closeGameOnNoInputTimeout = LauncherConfig.GetValue<TimeSpan>("CloseGameAfterNoInputTimeout", TimeSpan.FromMinutes(8));

            #endregion

            // Initialize the UI
            log.Info("Setting up UI");
            InitializeComponent();

            // Prepare for audio
            audioController = new CoreAudioController();

            // Search for games
            Task.Factory.StartNew(new Func<GameElement[]>(LoadGames)).ContinueWith(task =>
            {
                // Build game list and set visibility
                var elements = (ObservableCollection<GameElement>)this.GetValue(AvaliableGamesProperty);
                var needsUpdateTimer = false;
                foreach (var game in task.Result)
                {
                    needsUpdateTimer |= game.NewGameVisibility == System.Windows.Visibility.Visible || game.UpdatedGameVisibility == System.Windows.Visibility.Visible;
                    elements.Add(game);
                }
                this.SetValue(LoadingGamesVisibilityProperty, Visibility.Collapsed);
                if (elements.Count == 0)
                {
                    log.Info("No games loaded");
                    this.SetValue(NoGamesVisibilityProperty, Visibility.Visible);
                }

                if (needsUpdateTimer)
                {
                    // Setup timer to update changes
                    log.Info("Setting up game \"new-ness\" timer");
                    versionUpdateTimer = new System.Threading.Timer(state =>
                    {
                        log.Info("Running game \"new-ness\" timer");
                        Task.Factory.StartNew(() =>
                        {
                            if (!UpdateGameVersions(AvaliableGames, false, log))
                            {
                                // No longer needs to process
                                log.Info("Game \"new-ness\" timer is no longer needed");
                                versionUpdateTimer.Dispose();
                                versionUpdateTimer = null;
                            }
                            log.Info("Game \"new-ness\" timer finished");
                        }, System.Threading.CancellationToken.None, TaskCreationOptions.None, TaskScheduler.FromCurrentSynchronizationContext()).Wait();
                    }, null, TimeSpan.FromHours(1), TimeSpan.FromDays(1));
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            log.Info("Shutting down");
            base.OnClosing(e);
            hook.Dispose();
            if (versionUpdateTimer != null)
            {
                versionUpdateTimer.Dispose();
            }
            if (hookTimer != null)
            {
                hookTimer.Dispose();
            }
        }

        private void SetVolume(int volume)
        {
            volume = Math.Min(Math.Max(volume, 0), 100);

            try
            {
                audioController.DefaultPlaybackDevice.Volume = volume;
            }
            catch(Exception exp)
            {
                log.Warn(exp, "Error changing system volume for game");
            }
        }

        private void GameInputTimer(object state)
        {
            // On input timer's timout, close the main window
            var pair = (KeyValuePair<GameElement, Process>)state;

            log.Warn("No input recieved for {0}, returning to launcher", pair.Key.Name);
            try
            {
                pair.Value.CloseMainWindow();
            }
            catch
            {
                log.Error("Could not request closing the main window of {0}", pair.Key.Name);
            }
        }

        #region UpdateGameVersions

        // Expects to be run in the proper threading context
        private static bool UpdateGameVersions(ICollection<GameElement> gamesToUpdate, bool buildingList, Logger log)
        {
            // Game list file is in format of <game name>{new line}<exe modification time, as a long>

            var time = DateTime.Now;
            var table = new Dictionary<string, DateTime>();
            var gamelist = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "GameList.dat"));
            var updatesRequired = false;
            var listUpdated = false;

            if (System.IO.File.Exists(gamelist))
            {
                // Get current game list, if they exist
                log.Info("Opening original game list");
                using (var sr = new System.IO.StreamReader(gamelist))
                {
                    string name;
                    while ((name = sr.ReadLine()) != null)
                    {
                        DateTime modTime;
                        long fileTime;
                        if (long.TryParse(sr.ReadLine(), out fileTime))
                        {
                            modTime = DateTime.FromFileTime(fileTime);
                        }
                        else
                        {
                            modTime = time;
                        }
                        table.Add(name, modTime);
                    }
                }
                log.Info("Loaded {0} games from game list", table.Count);
            }

            // Check games to update
            var diff = TimeSpan.FromDays(versionNotificationReset);
            for (var i = 0; i < gamesToUpdate.Count; i++)
            {
                var element = gamesToUpdate.ElementAt(i);
                var fileTime = System.IO.File.GetLastWriteTime(element.ExeFile);
                if (table.ContainsKey(element.Name))
                {
                    if (time - table[element.Name] >= diff)
                    {
                        // Game is now "old"
                        if (element.NewGameVisibility == Visibility.Visible || element.UpdatedGameVisibility == Visibility.Visible)
                        {
                            listUpdated = true;
                            element.NewGameVisibility = Visibility.Collapsed;
                            element.UpdatedGameVisibility = Visibility.Collapsed;
                            table[element.Name] = fileTime;
                        }
                    }
                    else
                    {
                        if (fileTime > table[element.Name])
                        {
                            // Game was updated
                            updatesRequired = true;
                            element.NewGameVisibility = Visibility.Collapsed;
                            element.UpdatedGameVisibility = Visibility.Visible;
                        }
                        else if (buildingList)
                        {
                            // Game is "new" for our purproses. Might make people look at it instead of disregard it
                            updatesRequired = true;
                            element.NewGameVisibility = Visibility.Visible;
                            element.UpdatedGameVisibility = Visibility.Collapsed;
                        }
                    }
                }
                else
                {
                    // Add to table
                    updatesRequired = true;
                    listUpdated = true;
                    table.Add(element.Name, fileTime);
                    element.NewGameVisibility = Visibility.Visible;
                    element.UpdatedGameVisibility = Visibility.Collapsed;
                }
            }

            // Remove old games if no longer listed
            if (buildingList && gamesToUpdate.Count != table.Count)
            {
                log.Info("Game list mismatch. Removing old games.");
                listUpdated = true;
                var gamesListed = new List<string>(table.Keys);
                foreach (var game in gamesToUpdate)
                {
                    if (gamesListed.Contains(game.Name))
                    {
                        gamesListed.Remove(game.Name);
                    }
                }
                foreach (var gameToRemove in gamesListed)
                {
                    table.Remove(gameToRemove);
                }
            }

            // Write new list if updated
            if (listUpdated)
            {
                log.Info("Writing new game list");
                using (var sw = new System.IO.StreamWriter(gamelist))
                {
                    foreach (var tableElement in table)
                    {
                        sw.WriteLine(tableElement.Key);
                        sw.WriteLine(tableElement.Value.ToFileTime());
                    }
                }
            }

            return updatesRequired;
        }

        #endregion

        #region LoadGames

        private GameElement[] LoadGames()
        {
            var gamePath = System.IO.Path.Combine(Environment.CurrentDirectory, "Games");
            if (!System.IO.Directory.Exists(gamePath))
            {
                return new GameElement[0];
            }
            var enableKeyboardHook = !LauncherConfig.GetValue<bool>("DisableKeyboardHook");
            if (!enableKeyboardHook)
            {
                log.Info("Keyboard hook is disabled");
            }

            var games = new List<GameElement>();
            var gameNames = new HashSet<string>();
            foreach (var dir in System.IO.Directory.EnumerateDirectories(gamePath))
            {
                log.Info("Checking folder for game \"{0}\"", dir);
                var possibleExes = System.IO.Directory.GetFiles(dir, "*.exe", System.IO.SearchOption.TopDirectoryOnly);
                if (possibleExes.Length > 0)
                {
                    // Exe
                    var exe = possibleExes.First(); // May not be the best way to get exes

                    log.Info("Found EXE \"{0}\"", exe);

                    // Icon
                    Lazy<ImageSource> exeIcon = null;
                    var possibleIcons = System.IO.Directory.EnumerateFiles(dir)
                                                           .Where(file => System.IO.Path.GetExtension(file).Equals(".ico", StringComparison.InvariantCultureIgnoreCase) ||
                                                                          System.IO.Path.GetExtension(file).Equals(".png", StringComparison.InvariantCultureIgnoreCase))
                                                           .ToArray();
                    if (possibleIcons.Length > 0)
                    {
                        var filtered = possibleIcons.Where(file => System.IO.Path.GetFileNameWithoutExtension(file).IndexOf("Icon") >= 0);
                        var icon = filtered.FirstOrDefault() ?? possibleIcons.First();
                        log.Info("Found icon \"{0}\"", icon);
                        if (System.IO.Path.GetExtension(icon).Equals(".ico", StringComparison.InvariantCultureIgnoreCase))
                        {
                            exeIcon = new Lazy<ImageSource>(() =>
                            {
                                return Imaging.CreateBitmapSourceFromHIcon(new System.Drawing.Icon(icon).Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            });
                        }
                        else
                        {
                            exeIcon = new Lazy<ImageSource>(() =>
                            {
                                return new BitmapImage(new Uri(icon));
                            });
                        }
                    }
                    else
                    {
                        log.Info("Loading icon from EXE");
                        exeIcon = new Lazy<ImageSource>(() =>
                        {
                            return Imaging.CreateBitmapSourceFromHIcon(System.Drawing.Icon.ExtractAssociatedIcon(exe).Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        });
                    }

                    // Name/Description
                    var possibleInfo = System.IO.Directory.GetFiles(dir, "*.ini", System.IO.SearchOption.TopDirectoryOnly);
                    string name = null;
                    string desc = null;
                    string args = null;
                    string ver = null;
                    var volume = -1;
                    var playerCount = 2;
                    if (possibleInfo.Length > 0)
                    {
                        var infoFiles = possibleInfo.Where(file => System.IO.Path.GetFileNameWithoutExtension(file).IndexOf("Info", StringComparison.InvariantCultureIgnoreCase) >= 0);
                        var infoFile = infoFiles.FirstOrDefault();
                        if (infoFile != null)
                        {
                            log.Info("Loading info from file \"{0}\"", infoFile);
                            GameConfig.Load(infoFile, log);

                            playerCount = GameConfig.GetValue<int>("SupportedPlayers", playerCount);
                            name = GameConfig.GetValue<string>("Title");
                            desc = GameConfig.GetValue<string>("Description");
                            args = GameConfig.GetValue<string>("Arguments");
                            ver = GameConfig.GetValue<string>("Version");
                            volume = GameConfig.GetValue<int>("Volume", volume);
                        }
                    }
                    if (name == null || desc == null || ver == null)
                    {
                        var info = FileVersionInfo.GetVersionInfo(exe);
                        if (name == null)
                        {
                            name = info.ProductName ?? System.IO.Path.GetFileNameWithoutExtension(exe);
                            playerCount = 2;
                        }
                        if (desc == null)
                        {
                            desc = info.FileDescription ?? string.Format("An awesome game called {0}", name);
                        }
                        if (ver == null)
                        {
                            if (string.IsNullOrWhiteSpace(info.ProductVersion))
                            {
                                ver = "1.0";
                            }
                            else
                            {
                                ver = info.ProductVersion;
                            }
                        }
                    }

                    // Add game
                    if (!gameNames.Contains(name))
                    {
                        gameNames.Add(name);
                        var game = new GameElement(name, desc, playerCount, ver, exe, args, exeIcon, this);
                        if (volume >= 0 && volume <= 100)
                        {
                            game.DesiredVolume = volume;
                        }
                        if (enableKeyboardHook)
                        {
                            var gameFolder = System.IO.Path.GetFileName(dir);
                            var disableGameFolder = LauncherConfig.GetValue<string[]>("DisableKeyboardHookGames", new string[0]);

                            if (!disableGameFolder.Contains(gameFolder))
                            {
                                game.KeyHooksEnabled = true;
                            }
                        }
                        games.Add(game);
                    }
                    else
                    {
                        log.Warn("Can't add game \"{0}\" because a game with the same name already exists.", name);
                    }
                }
            }

            // Alphabetical order
            games.Sort(new Comparison<GameElement>((f1, f2) => { return f1.Name.CompareTo(f2.Name); }));
            log.Info("Finished loading {0} games", games.Count);

            // Build new-ness list
            UpdateGameVersions(games, true, log);

            return games.ToArray();
        }

        #endregion

        #region Input Handlers

        private void ItemListKeyUpHandler(object sender, KeyEventArgs e)
        {
            int index;
            if ((index = Array.IndexOf(inputKeys[KEYS_START], e.Key)) >= 0)
            {
                var game = GameItems.SelectedItem as GameElement;
                if (game.Execute.CanExecute(game))
                {
                    var playerCount = e.Key - inputKeys[KEYS_START_OFFSET][index];
                    if (playerCount > 0 && playerCount <= game.SupportedPlayerCount)
                    {
                        var playerConf = System.IO.Path.Combine(game.ExeFolder, "Startup.cfg");
                        using (var fs = new System.IO.StreamWriter(playerConf))
                        {
                            fs.WriteLine("PlayerCount: {0}", playerCount);
                            fs.WriteLine("StartTime: {0}", DateTime.Now);
                        }
                        SetVolume(game.DesiredVolume);
                        game.Execute.Execute(game);
                    }
                }
                else
                {
                    GameError(null, game, "Game cannot be run");
                }
            }
            // Check all movement keys to see if any were touched. If so, then reset scroll repeat
            if (inputKeys.Skip(KEYS_START_COUNT).SelectMany(keys => keys).Contains(e.Key))
            {
                scrollRepeatCount = 0;
            }
        }

        private void ItemListKeyDownHandler(object sender, KeyEventArgs e)
        {
            // Prevent opening the alt-space menu (which lets you close the app).
            // Also disables the "alt" keys in general (though alt-F4 still works), as it's possible to tap alt, then after a few seconds press space and for the menu to open
            if ((e.SystemKey == Key.Space && (e.KeyboardDevice.IsKeyDown(Key.LeftAlt) || e.KeyboardDevice.IsKeyDown(Key.RightAlt))) ||
                (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
            {
                e.Handled = true;
                return;
            }

            // Do everything to this index, so we can make sure the list scrolls...
            int? selectedIndex = null;
            if (Array.IndexOf(inputKeys[KEYS_UP], e.Key) >= 0)
            {
                // Up
                if (GameItems.SelectedIndex == 0)
                {
                    selectedIndex = AvaliableGames.Count - 1;
                }
                else
                {
                    selectedIndex = GameItems.SelectedIndex - 1;
                }
            }
            else if (Array.IndexOf(inputKeys[KEYS_DOWN], e.Key) >= 0)
            {
                // Down
                if (GameItems.SelectedIndex == AvaliableGames.Count - 1)
                {
                    selectedIndex = 0;
                }
                else
                {
                    selectedIndex = GameItems.SelectedIndex + 1;
                }
            }
            else if (inputKeys.Skip(KEYS_LEFT_RIGHT_INDEX_START).SelectMany(keys => keys).Contains(e.Key))
            {
                // Side movement (skip-alphabet)
                var curSelectedIndex = GameItems.SelectedIndex;
                var lastLetter = char.ToLower((GameItems.SelectedItem as GameElement).Name[0]);
                var itemsToSelect = AvaliableGames.Select((ele, idx) => { return new { ele, idx }; });
                if (Array.IndexOf(inputKeys[KEYS_LEFT], e.Key) >= 0)
                {
                    // Left
                    selectedIndex =
                        itemsToSelect
                            .Where(ele => { return ele.idx < curSelectedIndex && char.ToLower(ele.ele.Name[0]) < lastLetter; })
                            .Select(ele => { return ele.idx; })
                            .LastOrDefault();
                }
                else
                {
                    // Right
                    selectedIndex =
                        itemsToSelect
                            .SkipWhile(ele => { return ele.idx <= curSelectedIndex; })
                            .Where(ele => { return char.ToLower(ele.ele.Name[0]) > lastLetter; })
                            .Select(ele => { return ele.idx; })
                            .FirstOrDefault();
                    if (selectedIndex == 0)
                    {
                        // Ended up at default? Just go to the last value
                        selectedIndex = AvaliableGames.Count - 1;
                    }
                }
            }

            // If we have an index, then we want to scroll if 1). This is a unique scoll press 2). The repeat of scolling has exceeded the delay we have. Repeat scrolling speeds up over time.
            if (selectedIndex.HasValue && (!e.IsRepeat || (DateTime.UtcNow - lastScrollTime).Milliseconds >= scrollRepeatDelay / Math.Log(scrollRepeatCount + Math.E)))
            {
                lastScrollTime = DateTime.UtcNow;
                GameItems.SelectedIndex = selectedIndex.Value;
                GameItems.ScrollIntoView(GameItems.SelectedItem);
                if (e.IsRepeat)
                {
                    scrollRepeatCount++;
                }
                else
                {
                    scrollRepeatCount = 0;
                }
            }
        }

        #endregion

        #region Game Launch State Handlers

        public bool LoadingGame(GameElement element)
        {
            if (System.Threading.Interlocked.CompareExchange(ref gameRunningState, GAME_EXEC_STATE_STARTING, GAME_EXEC_STATE_NOT_RUNNING) != GAME_EXEC_STATE_NOT_RUNNING)
            {
                GameError(null, element, "Another game has been started already or is running");
                return false;
            }
            return true;
        }

        public void GameError(Exception err, GameElement element, string message)
        {
            if (err != null)
            {
                log.Error(err, "Error with {0}: {1}", element.Name, message);
            }
            else
            {
                log.Warn("Issue with {0}: {1}", element.Name, message);
            }
        }

        #region GameRunning Helper Functions

        private bool CanUseKeyboardHook(GameElement element, Process game)
        {
            if (!element.KeyHooksEnabled && !LauncherConfig.GetValue<bool>("AlwaysLogGameHookCompatability"))
            {
                // If we don't always want to log compatability, and we don't have hooks enabled, early exit
                return false;
            }
            else if (element.KeyHooksEnabled && LauncherConfig.GetValue<bool>("SkipGameHookCompatabilityCheck"))
            {
                // If we want to skip compatability checks, and key hooks are enabled, then (try) to use them
                return true;
            }
            Exception hookProcessError;
            bool canUseKeyboardHook = InputHook.CanHookProcess(game, out hookProcessError);
            if (!canUseKeyboardHook)
            {
                if (hookProcessError != null)
                {
                    var we = hookProcessError as System.ComponentModel.Win32Exception;
                    if (we != null && (uint)we.HResult == 0x80004005) //XXX this value corisponds to "unspecified error", but given the context, it seems to always correlate to "32 bit launcher, 64 bit game. Can't use together"
                    {
                        log.Warn(we, "Launcher is running as a 32 bit process and the game ({0}) is running as a 64 bit process. Keyboard tracking can't be used between the two. Try running the launcher as a 64 bit process.", element.Name);
                    }
                    else
                    {
                        log.Warn(hookProcessError, "{0}'s process is incompatible with the launcher's keyboard tracking system. View exception and make ticket so it can be looked at.", element.Name);
                    }
                }
                else
                {
                    log.Warn("{0} could not be hooked for keyboard tracking because it does not match the launcher's process size: Launcher {1} 64 bit, Game {2} 64 bit.",
                        element.Name,
                        (Environment.Is64BitProcess ? "is" : "is not"),
                        (canUseKeyboardHook ? "is" : "is not"));
                }
            }
            else if (LauncherConfig.GetValue<bool>("AlwaysLogGameHookCompatability"))
            {
                // If we always want to log the result, then log success.
                log.Info("{0} can use keyboard tracking.", element.Name);
            }
            return canUseKeyboardHook && element.KeyHooksEnabled;
        }

        #endregion

        public void GameRunning(GameElement element, Process game)
        {
            if (System.Threading.Interlocked.CompareExchange(ref gameRunningState, GAME_EXEC_STATE_RUNNING, GAME_EXEC_STATE_STARTING) != GAME_EXEC_STATE_STARTING)
            {
                // Should not happen
                GameError(null, element, "Another game may have started or is running. Exiting to prevent deadlocking.");
                try
                {
                    game.Dispose();
                }
                catch (Exception e)
                {
                    GameError(e, element, "Game to start refuses to exit");
                }
            }
            else
            {
                log.Info("Starting {0}", element.Name);
                this.SetValue(GameExecutingProperty, true);

                bool useKeyboardHook = CanUseKeyboardHook(element, game);

                // Timed controls
                EventHandler<Key> inputRecieved = (e, key) =>
                {
                    // On input, simply reset the timer
                    if (hookTimer != null)
                    {
                        ((System.Threading.Timer)hookTimer).Change(closeGameOnNoInputTimeout, System.Threading.Timeout.InfiniteTimeSpan);
                    }
                };
                var processingTest = new System.Threading.Timer(process =>
                {
                    // Test if process is responding. Kill it if it isn't.
                    var runningProcess = (Process)process;
                    if (!runningProcess.Responding) //XXX Actual crash isn't triggering this...
                    {
                        log.Info("{0} stopped responding", element.Name);
                        try
                        {
                            //XXX unsure if Kill will still call the Exited handler. If not, then input hook, game running state, and game executing property need to be set here too (so it should be refactored to seperate function)
                            runningProcess.Kill();
                        }
                        catch (Exception e)
                        {
                            GameError(e, element, "Process stopped responding");
                        }
                    }
                }, game, TimeSpan.FromMilliseconds(2500), gameRespondingPolling); // delay of first run (let it start for 2.5 sec, then poll), repeat interval

                // Exit handler
                game.Exited += (s, e) =>
                {
                    log.Info("{0} exited", element.Name);

                    // Shutdown timed controls
                    processingTest.Dispose();
                    if (hookTimer != null)
                    {
                        hookTimer.Dispose();
                        hookTimer = null;
                    }

                    // Test exit results
                    var exitedProcess = (Process)s;
                    if (exitedProcess.ExitCode != 0)
                    {
                        GameError(new System.ComponentModel.Win32Exception(exitedProcess.ExitCode), element, "Game didn't exit cleanly");
                    }

                    if (useKeyboardHook)
                    {
                        // Unhook input
                        if (!hook.Unhook())
                        {
                            log.Warn("{0} could not be unhooked from keyboard tracking.", element.Name);
                        }
                        hook.KeyDown -= inputRecieved;
                    }

                    // Update game running state
                    if (System.Threading.Interlocked.CompareExchange(ref gameRunningState, GAME_EXEC_STATE_NOT_RUNNING, GAME_EXEC_STATE_RUNNING) != GAME_EXEC_STATE_RUNNING)
                    {
                        GameError(null, element, "Launcher state was not running a game, but exit callback should only be invoked for a running game.");
                    }
                    this.Dispatcher.InvokeAsync(() => this.SetValue(GameExecutingProperty, false)).Wait();
                };

                if (useKeyboardHook)
                {
                    // Hook game input
                    if (hook.Hookup(game))
                    {
                        hook.KeyDown += inputRecieved;
                        hookTimer = new System.Threading.Timer(GameInputTimer, new KeyValuePair<GameElement, Process>(element, game), closeGameOnNoInputTimeout, System.Threading.Timeout.InfiniteTimeSpan);
                    }
                    else
                    {
                        log.Warn("{0} could not be hooked for keyboard tracking.", element.Name);
                    }
                }
            }
        }

        #endregion

        // UI properties
        public Visibility NoGamesVisibility
        {
            get
            {
                return (Visibility)this.GetValue(NoGamesVisibilityProperty);
            }
        }
        public Visibility LoadingGamesVisibility
        {
            get
            {
                return (Visibility)this.GetValue(LoadingGamesVisibilityProperty);
            }
        }
        public ICollection<GameElement> AvaliableGames
        {
            get
            {
                return (ICollection<GameElement>)this.GetValue(AvaliableGamesProperty);
            }
        }
        public bool GameExecuting
        {
            get
            {
                return (bool)this.GetValue(GameExecutingProperty);
            }
        }
    }
}
