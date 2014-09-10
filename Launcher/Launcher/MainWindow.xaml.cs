using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launcher
{
    //XXX Nifty addition: versions? Possibly keep a cache of the loaded EXEs, if it's a new EXE, then display it as "new". If it was updated (file modification date has changed), display it as "updated". If a period of time passes (say 2 weeks), then remove the new/update display.

    public class GameElement
    {
        public GameElement(string name, string description, string exe, ImageSource icon)
        {
            Name = name;
            Description = description;
            Icon = icon;
        }

        public string Name { get; private set; }
        public string Description { get; private set; }
        public ImageSource Icon { get; private set; }
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static readonly DependencyProperty NoGamesVisibilityProperty = DependencyProperty.Register("NoGamesVisibility", typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Collapsed));
        public static readonly DependencyProperty LoadingGamesVisibilityProperty = DependencyProperty.Register("LoadingGamesVisibility", typeof(Visibility), typeof(MainWindow), new PropertyMetadata(Visibility.Visible));
        public static readonly DependencyProperty AvaliableGamesProperty = DependencyProperty.Register("AvaliableGames", typeof(ObservableCollection<GameElement>), typeof(MainWindow), new PropertyMetadata(new ObservableCollection<GameElement>()));

        private ObservableCollection<GameElement> elements;

        public MainWindow()
        {
            InitializeComponent();

            // Get elements collection
            elements = (ObservableCollection<GameElement>)this.GetValue(AvaliableGamesProperty);

            // Search for games
            Task.Factory.StartNew(() =>
            {
                var gamePath = System.IO.Path.Combine(Environment.CurrentDirectory, "Games");
                foreach (var dir in System.IO.Directory.EnumerateDirectories(gamePath))
                {
                    var possibleExes = System.IO.Directory.GetFiles(dir, "*.exe", System.IO.SearchOption.TopDirectoryOnly);
                    if (possibleExes.Length > 0)
                    {
                        var exe = possibleExes.First(); // May not be the best way to get exes
                        var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                        var exeImageSource = Imaging.CreateBitmapSourceFromHIcon(exeIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        //TODO: get game name and description
                        elements.Add(new GameElement("", "", exe, exeImageSource));
                    }
                }
            }).ContinueWith(task =>
            {
                this.SetValue(LoadingGamesVisibilityProperty, Visibility.Collapsed);
                if (elements.Count == 0)
                {
                    this.SetValue(NoGamesVisibilityProperty, Visibility.Visible);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
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
    }
}
