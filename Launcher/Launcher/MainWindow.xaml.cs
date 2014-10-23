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

namespace Launcher
{
    /* XXX Nifty addition:
     * versions? Possibly keep a cache of the loaded EXEs, if it's a new EXE, then display it as "new". If it was updated (file modification date has changed), display it as "updated". If a period of time passes (say 2 weeks), then remove the new/update display.
     * Quick-find? Games are divided by title's first letter, and each one has a header for that letter. The user can search by letter to quickly go through list (assuming there's many games)
     */

    public class DelegateCommandBase : ICommand
    {
        private Action<object> exec;
        private Func<object, bool> canExec;

        public DelegateCommandBase(Action<object> execute, Func<object, bool> canExecute)
        {
            exec = execute;
            canExec = canExecute;
        }

        public DelegateCommandBase(Action<object> execute)
            : this(execute, arg => true)
        {
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            return canExec(parameter);
        }

        public void Execute(object parameter)
        {
            exec(parameter);
        }

        public void RaiseCanExecuteChanged()
        {
            var handle = CanExecuteChanged;
            if (handle != null)
            {
                handle(this, EventArgs.Empty);
            }
        }
    }

    public sealed class DelegateCommand : DelegateCommandBase
    {
        public DelegateCommand(Action execute, Func<bool> canExecute)
            : base(arg => execute(), arg => canExecute())
        {
        }

        public DelegateCommand(Action execute)
            : this(execute, () => true)
        {
        }
    }

    public class GameElement
    {
        private string exePath;
        private MainWindow ui;
        private Lazy<ImageSource> iconLazy;
        private string args;

        public GameElement(string name, string description, int supportedPlayerCount, string exe, string arguments, Lazy<ImageSource> icon, MainWindow ui)
        {
            Name = name;
            Description = description;
            exePath = exe;
            args = arguments;
            ExeFolder = System.IO.Path.GetDirectoryName(exe);
            SupportedPlayerCount = supportedPlayerCount;
            iconLazy = icon;
            this.ui = ui;
        }

        public string ExeFolder { get; private set; }
        public int SupportedPlayerCount { get; private set; }

        public string Name { get; private set; }
        public string Description { get; private set; }
        public ImageSource Icon
        {
            get
            {
                return iconLazy.Value;
            }
        }
        public DelegateCommand Execute
        {
            get
            {
                return new DelegateCommand(() =>
                {
                    var info = new ProcessStartInfo(exePath);
                    info.UseShellExecute = true;
                    info.WindowStyle = ProcessWindowStyle.Maximized;
                    info.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrWhiteSpace(args))
                    {
                        info.Arguments = args;
                    }

                    var game = new Process();
                    game.StartInfo = info;
                    game.EnableRaisingEvents = true;
                    if (game.Start())
                    {
                        ui.GameRunning(this, game);
                    }
                    else
                    {
                        ui.GameError(null, this, "Could not start game");
                    }
                });
            }
        }
    }

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
        private DateTime lastScrollTime = DateTime.UtcNow;
        private int scrollRepeatCount = 0;

        private Logger log;

        public MainWindow()
        {
            log = LogManager.GetLogger("launcher");

            // Make sure the UI knows where to get data bindings from (yes, this is weird... I thought it should've been implicit, but doesn't seem to be the case)
            this.DataContext = this;

            // Initialize the UI
            log.Info("Setting up UI");
            InitializeComponent();

            // Search for games
            Task.Factory.StartNew(() =>
            {
                var games = new List<GameElement>();
                var gamePath = System.IO.Path.Combine(Environment.CurrentDirectory, "Games");
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
                            var icon = possibleIcons.First();
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
                        if (name == null || desc == null)
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
                        }

                        // Add game
                        games.Add(new GameElement(name, desc, playerCount, exe, args, exeIcon, this));
                    }
                }
                // Alphabetical order
                games.Sort(new Comparison<GameElement>((f1, f2) => { return f1.Name.CompareTo(f2.Name); }));
                log.Info("Finished loading {0} games", games.Count);
                return games.ToArray();
            }).ContinueWith(task =>
            {
                var elements = (ObservableCollection<GameElement>)this.GetValue(AvaliableGamesProperty);
                foreach (var game in task.Result)
                {
                    elements.Add(game);
                }
                this.SetValue(LoadingGamesVisibilityProperty, Visibility.Collapsed);
                if (elements.Count == 0)
                {
                    log.Info("No games loaded");
                    this.SetValue(NoGamesVisibilityProperty, Visibility.Visible);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

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
                        }
                        game.Execute.Execute(game);
                    }
                }
                else
                {
                    GameError(null, game, "Game cannot be run");
                }
            }
            else if (e.Key == Key.NumPad8 || e.Key == Key.R || e.Key == Key.NumPad2 || e.Key == Key.F)
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
