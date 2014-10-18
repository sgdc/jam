using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
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

        public GameElement(string name, string description, int supportedPlayerCount, string exe, System.Drawing.Icon icon, MainWindow ui)
        {
            Name = name;
            Description = description;
            exePath = exe;
            ExeFolder = System.IO.Path.GetDirectoryName(exe);
            SupportedPlayerCount = supportedPlayerCount;
            iconLazy = new Lazy<ImageSource>(() =>
            {
                return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            });
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

        public MainWindow()
        {
            // Make sure the UI knows where to get data bindings from (yes, this is weird... I thought it should've been implicit, but doesn't seem to be the case)
            this.DataContext = this;

            // Initialize the UI
            InitializeComponent();

            // Search for games
            Task.Factory.StartNew(() =>
            {
                var games = new List<GameElement>();
                var gamePath = System.IO.Path.Combine(Environment.CurrentDirectory, "Games");
                foreach (var dir in System.IO.Directory.EnumerateDirectories(gamePath))
                {
                    var possibleExes = System.IO.Directory.GetFiles(dir, "*.exe", System.IO.SearchOption.TopDirectoryOnly);
                    if (possibleExes.Length > 0)
                    {
                        // Exe
                        var exe = possibleExes.First(); // May not be the best way to get exes

                        // Icon
                        System.Drawing.Icon exeIcon = null;
                        var possibleIcons = System.IO.Directory.GetFiles(dir, "*.ico", System.IO.SearchOption.TopDirectoryOnly);
                        if (possibleIcons.Length > 0)
                        {
                            exeIcon = new System.Drawing.Icon(possibleIcons.First()); // May not be the best way to get icons
                        }
                        else
                        {
                            exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                        }

                        // Name/Description
                        var possibleInfo = System.IO.Directory.GetFiles(dir, "*.txt", System.IO.SearchOption.TopDirectoryOnly);
                        string name = null;
                        string desc = null;
                        int playerCount = 2;
                        if (possibleInfo.Length > 0)
                        {
                            var infoFiles = possibleInfo.Where(file => System.IO.Path.GetFileNameWithoutExtension(file).IndexOf("Info", StringComparison.InvariantCultureIgnoreCase) >= 0);
                            var infoFile = infoFiles.FirstOrDefault();
                            if (infoFile != null)
                            {
                                /*
                                 * Format:
                                 * - [optional] int <supported player count. Between 2 and 4>
                                 * - string <game name>
                                 * - [optional] string <game description>
                                 */

                                using (var info = new System.IO.StreamReader(infoFile))
                                {
                                    var line = info.ReadLine();
                                    if (int.TryParse(line, out playerCount))
                                    {
                                        playerCount = Math.Max(2, Math.Min(4, playerCount));
                                        name = info.ReadLine();
                                    }
                                    else
                                    {
                                        playerCount = 2;
                                        name = line;
                                    }
                                    desc = info.ReadToEnd();
                                }
                            }
                        }
                        if (name == null)
                        {
                            var info = FileVersionInfo.GetVersionInfo(exe);
                            name = info.ProductName ?? System.IO.Path.GetFileNameWithoutExtension(exe);
                            desc = info.FileDescription ?? string.Format("An awesome game called {0}", name);
                        }

                        // Add game
                        games.Add(new GameElement(name, desc, playerCount, exe, exeIcon, this));
                    }
                }
                // Alphabetical order
                games.Sort(new Comparison<GameElement>((f1, f2) => { return f1.Name.CompareTo(f2.Name); }));
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

            // If we have an index, then we want to scroll if 1). This is a unique scoll press 2). The repeat of scolling has exceeded the delay we have
            if (selectedIndex.HasValue && (!e.IsRepeat || (DateTime.UtcNow - lastScrollTime).Milliseconds >= SCROLL_REPEAT_DELAY))
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
            //TODO
        }

        public void GameRunning(GameElement element, Process game)
        {
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
                        try
                        {
                            runningProcess.Kill();
                        }
                        catch (Exception e)
                        {
                            GameError(e, element, "Process stopped responding");
                        }
                    }
                }, game, 1000, 5000);
                game.Exited += (s, e) =>
                {
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
