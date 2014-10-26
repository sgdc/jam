using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Launcher
{
    #region DelegateCommand

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

    #endregion

    public class GameElement : System.ComponentModel.INotifyPropertyChanged
    {
        private MainWindow ui;
        private Lazy<ImageSource> iconLazy;
        private string args;
        private Visibility newGame;
        private Visibility updateGame;

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public GameElement(string name, string description, int supportedPlayerCount, string gameVersion, string exe, string arguments, Lazy<ImageSource> icon, MainWindow ui)
        {
            Name = name;
            Description = description;
            Version = gameVersion;
            ExeFile = exe;
            args = arguments;
            ExeFolder = System.IO.Path.GetDirectoryName(exe);
            SupportedPlayerCount = supportedPlayerCount;
            iconLazy = icon;
            this.ui = ui;

            newGame = Visibility.Collapsed;
            updateGame = Visibility.Collapsed;
        }

        private void OnPropertyChanged(string name)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
        }

        public string ExeFile { get; private set; }
        public string ExeFolder { get; private set; }
        public int SupportedPlayerCount { get; private set; }

        public Visibility NewGameVisibility
        {
            get
            {
                return newGame;
            }
            set
            {
                if (value != newGame)
                {
                    newGame = value;
                    OnPropertyChanged("NewGameVisibility");
                }
            }
        }
        public Visibility UpdatedGameVisibility
        {
            get
            {
                return updateGame;
            }
            set
            {
                if (value != updateGame)
                {
                    updateGame = value;
                    OnPropertyChanged("UpdatedGameVisibility");
                }
            }
        }

        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Version { get; private set; }
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
                    var info = new ProcessStartInfo(ExeFile);
                    info.UseShellExecute = true;
                    info.WindowStyle = ProcessWindowStyle.Maximized;
                    info.WorkingDirectory = System.IO.Path.GetDirectoryName(ExeFile);
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
}
