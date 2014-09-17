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

        public GameElement(string name, string description, string exe, System.Drawing.Icon icon, MainWindow ui)
        {
            Name = name;
            Description = description;
            exePath = exe;
            iconLazy = new Lazy<ImageSource>(() =>
            {
                return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            });
            this.ui = ui;
        }

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
                        if (possibleInfo.Length > 0)
                        {
                            var infoFiles = possibleInfo.Where(file => System.IO.Path.GetFileNameWithoutExtension(file).IndexOf("Info", StringComparison.InvariantCultureIgnoreCase) >= 0);
                            var infoFile = infoFiles.FirstOrDefault();
                            if (infoFile != null)
                            {
                                using (var info = new System.IO.StreamReader(infoFile))
                                {
                                    name = info.ReadLine();
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
                        games.Add(new GameElement(name, desc, exe, exeIcon, this));
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

        //TODO: need input control to change selected game

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
