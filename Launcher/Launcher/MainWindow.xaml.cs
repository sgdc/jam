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
using NLog;
using AudioSwitcher.AudioApi;
using AudioSwitcher.AudioApi.CoreAudio;

namespace Launcher
{
    /* XXX Addition:
     * Count the number of times a game has been played
     * Quick-find? Games are divided by title's first letter, and each one has a header for that letter. The user can search by letter to quickly go through list (assuming there's many games)
     */

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static readonly DependencyProperty NoGamesVisibilityProperty = DependencyProperty.Register("NoGamesVisibility", typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Collapsed));
        public static readonly DependencyProperty LoadingGamesVisibilityProperty = DependencyProperty.Register("LoadingGamesVisibility", typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Visible));
        public static readonly DependencyProperty AvaliableGamesProperty = DependencyProperty.Register("AvaliableGames", typeof(ObservableCollection<GameElement>), typeof(MainWindow), new PropertyMetadata(new ObservableCollection<GameElement>()));
        public static readonly DependencyProperty GameExecutingProperty = DependencyProperty.Register("GameExecuting", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        private const double SCROLL_REPEAT_DELAY = 250.0; // 250 ms between scrolling when pressing and holding a key
        private const int DAYS_BEFORE_VERSION_NOTIFICATION_RESET = 14; // If game is new or updated, how many days before it is no longer "updated" or "new"?
        private DateTime lastScrollTime = DateTime.UtcNow;
        private int scrollRepeatCount = 0;

        private Logger log;
        private IDisposable versionUpdateTimer = null;
        private AudioController<CoreAudioDevice> audioController;

        public MainWindow()
        {
            log = LogManager.GetLogger("launcher");

            // Make sure the UI knows where to get data bindings from (yes, this is weird... I thought it should've been implicit, but doesn't seem to be the case)
            this.DataContext = this;

            // Initialize the UI
            log.Info("Setting up UI");
            InitializeComponent();

            // Prepare for audio
            audioController = new CoreAudioController();

            // Search for games
            Task.Factory.StartNew(new Func<GameElement[]>(LoadGames)).ContinueWith(task =>
            {
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
            if (versionUpdateTimer != null)
            {
                versionUpdateTimer.Dispose();
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
                log.Warn("Error changing system volume for game", exp);
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
            var diff = TimeSpan.FromDays(DAYS_BEFORE_VERSION_NOTIFICATION_RESET);
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
                            /*
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

                            log.Info("Loading info from file \"{0}\"", infoFile);

                            using (var info = new System.IO.StreamReader(infoFile))
                            {
                                string line;
                                while ((line = info.ReadLine()) != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("%"))
                                    {
                                        switch (line.ToLower())
                                        {
                                            case "[supportedplayers]":
                                                var tmpLine = info.ReadLine();
                                                if (int.TryParse(tmpLine, out playerCount))
                                                {
                                                    log.Info("Got player count: {0}", playerCount);
                                                    if (playerCount < 2 || playerCount > 4)
                                                    {
                                                        log.Warn("Player count must be between 2 and 4");
                                                    }
                                                    playerCount = Math.Max(2, Math.Min(4, playerCount));
                                                }
                                                else
                                                {
                                                    log.Warn("Could not parse player count: \"{0}\"", tmpLine);
                                                }
                                                break;
                                            case "[title]":
                                                name = info.ReadLine();
                                                break;
                                            case "[description]":
                                                var builder = new StringBuilder();
                                                var c = -1;
                                                do
                                                {
                                                    if (builder.Length > 0)
                                                    {
                                                        builder.Append(Environment.NewLine);
                                                    }
                                                    builder.Append(info.ReadLine());
                                                    c = info.Peek();
                                                } while (c != '[' && c > 0);
                                                desc = builder.ToString().TrimEnd();
                                                break;
                                            case "[arguments]":
                                                args = info.ReadLine();
                                                break;
                                            case "[version]":
                                                ver = info.ReadLine();
                                                if (string.IsNullOrWhiteSpace(ver))
                                                {
                                                    ver = null;
                                                }
                                                break;
                                            case "[volume]":
                                                if (!int.TryParse(info.ReadLine(), out volume))
                                                {
                                                    volume = -1;
                                                }
                                                break;
                                            default:
                                                if (line.StartsWith("[") && line.EndsWith("]"))
                                                {
                                                    log.Info("Unknown Info key: \"{0}\"", line);
                                                }
                                                break;
                                        }
                                    }
                                }
                            }
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
            if (e.Key == Key.D1 || e.Key == Key.D2 || e.Key == Key.D3 || e.Key == Key.D4)
            {
                var game = GameItems.SelectedItem as GameElement;
                if (game.Execute.CanExecute(game))
                {
                    var playerCount = e.Key - Key.D0;
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
            if (e.Key == Key.NumPad8 || e.Key == Key.R || e.Key == Key.NumPad2 || e.Key == Key.F ||
                e.Key == Key.NumPad4 || e.Key == Key.D || e.Key == Key.NumPad6 || e.Key == Key.G)
            {
                scrollRepeatCount = 0;
            }
        }

        private void ItemListKeyDownHandler(object sender, KeyEventArgs e)
        {
            // Do everything to this index, so we can make sure the list scrolls...
            int? selectedIndex = null;
            if (e.Key == Key.NumPad8 || e.Key == Key.R)
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
            else if (e.Key == Key.NumPad2 || e.Key == Key.F)
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
            else if (e.Key == Key.NumPad4 || e.Key == Key.D || 
                e.Key == Key.NumPad6 || e.Key == Key.G)
            {
                // Side movement (skip-alphabet)
                var curSelectedIndex = GameItems.SelectedIndex;
                var lastLetter = char.ToLower((GameItems.SelectedItem as GameElement).Name[0]);
                var itemsToSelect = AvaliableGames.Select((ele, idx) => { return new { ele, idx }; });
                if (e.Key == Key.NumPad4 || e.Key == Key.D)
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
            if (selectedIndex.HasValue && (!e.IsRepeat || (DateTime.UtcNow - lastScrollTime).Milliseconds >= SCROLL_REPEAT_DELAY / Math.Log(scrollRepeatCount + Math.E)))
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

        public void GameError(Exception err, GameElement element, string message)
        {
            if (err != null)
            {
                log.Error(string.Format("Error with {0}: {1}", element.Name, message), err);
            }
            else
            {
                log.Warn("Issue with {0}: {1}", element.Name, message);
            }
        }

        public void GameRunning(GameElement element, Process game)
        {
            log.Info("Starting {0}", element.Name);
            if (GameExecuting)
            {
                // Should not happen
                try
                {
                    game.Dispose();
                }
                catch (Exception e)
                {
                    GameError(e, element, "Another game is running, but the game to start refuses to exit");
                }
            }
            else
            {
                this.SetValue(GameExecutingProperty, true);
                var processingTest = new System.Threading.Timer(process =>
                {
                    var runningProcess = (Process)process;
                    if (!runningProcess.Responding) //XXX Actual crash isn't triggering this...
                    {
                        log.Info("{0} stopped responding", element.Name);
                        try
                        {
                            runningProcess.Kill();
                        }
                        catch (Exception e)
                        {
                            GameError(e, element, "Process stopped responding");
                        }
                    }
                }, game, 1000, 5000); // ms delay of first run, ms repeat interval
                game.Exited += (s, e) =>
                {
                    log.Info("{0} exited", element.Name);
                    processingTest.Dispose();
                    var exitedProcess = (Process)s;
                    if (exitedProcess.ExitCode != 0)
                    {
                        GameError(new System.ComponentModel.Win32Exception(exitedProcess.ExitCode), element, "Game didn't exit cleanly");
                    }
                    this.Dispatcher.InvokeAsync(() => this.SetValue(GameExecutingProperty, false)).Wait();
                };
            }
        }

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
