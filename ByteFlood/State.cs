﻿using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.ObjectModel;
using System.ComponentModel;
using MonoTorrent;
using System.Xml.Serialization;
using MonoTorrent.Common;
using System.Threading;
using System.IO;
using Jayrock.Json;
using Jayrock.Json.Conversion;

namespace ByteFlood
{
    public class State : INotifyPropertyChanged
    {
        public ObservableCollection<TorrentInfo> Torrents = new ObservableCollection<TorrentInfo>();

        public MainWindow window = (MainWindow)App.Current.MainWindow;

        public SynchronizationContext uiContext;

        public CancellationTokenSource MainTaskCancellationTokenSource;

        public Listener listener;

        #region Global Statistics Properties

        public int GlobalMaxDownloadSpeed
        {
            get 
            {
                using (var s = this.LibtorrentSession.QuerySettings()) 
                {
                    return s.DownloadRateLimit;
                }
            }
            set
            {
                var settings = this.LibtorrentSession.QuerySettings();
                settings.DownloadRateLimit = value;
                this.LibtorrentSession.SetSettings(settings);
                settings.Dispose(); // Not sure about this
            }
        }

        public int GlobalMaxUploadSpeed
        {
            get 
            {
                using (var s = this.LibtorrentSession.QuerySettings())
                {
                    return s.UploadRateLimit;
                }
            }
            set
            {
                var settings = this.LibtorrentSession.QuerySettings();
                settings.UploadRateLimit = value;
                this.LibtorrentSession.SetSettings(settings);
                settings.Dispose(); // Not sure about this
            }
        }

        #endregion

        #region Torrents Counters

        public int DownloadingTorrentCount { get { return Torrents.Count(window.Downloading); } }

        public int SeedingTorrentCount { get { return Torrents.Count(window.Seeding); } }

        public int ActiveTorrentCount { get { return Torrents.Count(window.Active); } }

        public int InactiveTorrentCount { get { return TorrentCount - ActiveTorrentCount; } }

        public int FinishedTorrentCount { get { return Torrents.Count(window.Finished); } }

        public int TorrentCount { get { return Torrents.Count; } }

        #endregion

        public State()
        {
            this.Torrents.CollectionChanged += (s, e) => { NotifySinglePropertyChanged("TorrentCount"); };
            this.Initialize();
        }

        public Ragnar.Session LibtorrentSession { get; private set; }

        public LibTorrentAlertsWatcher LibTorrentAlerts { get; private set; }

        public void Initialize()
        {
            Directory.CreateDirectory(State.StateSaveDirectory);
            Directory.CreateDirectory(State.TorrentsStateSaveDirectory);

            this.LibtorrentSession = new Ragnar.Session();

            this.LibtorrentSession.SetAlertMask(Ragnar.SessionAlertCategory.All);

            this.LibtorrentSession.ListenOn(App.Settings.ListeningPort, App.Settings.ListeningPort);

            this.LibtorrentSession.StartDht();
            this.LibtorrentSession.StartLsd();
            this.LibtorrentSession.StartNatPmp();
            this.LibtorrentSession.StartUpnp();

            this.LibTorrentAlerts = new LibTorrentAlertsWatcher(this.LibtorrentSession);

            this.LibTorrentAlerts.ResumeDataArrived += LibTorrentAlerts_ResumeDataArrived;
            this.LibTorrentAlerts.TorrentAdded += LibTorrentAlerts_TorrentAdded;
            this.LibTorrentAlerts.TorrentStateChanged += LibTorrentAlerts_TorrentStateChanged;
            this.LibTorrentAlerts.TorrentStatsUpdated += LibTorrentAlerts_TorrentStatsUpdated;
            this.LibTorrentAlerts.TorrentFinished += LibTorrentAlerts_TorrentFinished;
            this.LibTorrentAlerts.MetadataReceived += LibTorrentAlerts_MetadataReceived;

            if (File.Exists(LtSessionFilePath))
            {
                this.LibtorrentSession.LoadState(File.ReadAllBytes(LtSessionFilePath));

                foreach (string file in Directory.GetFiles(State.TorrentsStateSaveDirectory, "*.torrent"))
                {
                    Ragnar.AddTorrentParams para = new Ragnar.AddTorrentParams();

                    para.TorrentInfo = new Ragnar.TorrentInfo(File.ReadAllBytes(file));

                    string tjson_file = Path.ChangeExtension(file, "tjson");

                    if (File.Exists(tjson_file))
                    {
                        using (var reader = File.OpenText(tjson_file))
                        {
                            JsonObject data = JsonConvert.Import<JsonObject>(reader);
                            para.SavePath = Convert.ToString(data["SavePath"]);
                        }
                    }
                    else
                    {
                        para.SavePath = App.Settings.DefaultDownloadPath;
                    }

                    string resume_file = Path.ChangeExtension(file, "resume");

                    if (File.Exists(resume_file))
                    {
                        // Loading the resume data will load all torrents settings,
                        // with the exception of byteflood settings {RatioLimit, RanCommand, PickedMovieData, ...}
                        // See TorrentInfo.LoadMiscSettings() method
                        para.ResumeData = File.ReadAllBytes(resume_file);
                    }

                    this.LibtorrentSession.AsyncAddTorrent(para);
                }
            }

            CheckByteFloodAssociation();

            listener = new Listener(this);
            listener.State = this;
        }

        private void CheckByteFloodAssociation()
        {
            if (!App.Settings.AssociationAsked)
            {
                bool assoc = Utility.Associated();
                if (!assoc)
                {
                    if (MessageBox.Show("Do you want to associate ByteFlood with .torrent files?",
                             "Question", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        Utility.SetAssociation();
                        App.Settings.AssociationAsked = true;
                    }
                    else if (MessageBox.Show("Do you want to be reminded about associations again?",
                        "Question", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
                        App.Settings.AssociationAsked = true;
                    else
                        App.Settings.AssociationAsked = false;
                }
            }
        }

        #region LibTorrent Alerts Handling

        void LibTorrentAlerts_MetadataReceived(Ragnar.TorrentHandle handle)
        {
            TorrentInfo ti = new TorrentInfo(handle);

            // This is critical, without it byteflood won't load this torrent at the next startup
            byte[] data = this.BackUpMangetLinkMetadata(handle.TorrentFile);

            ti.OriginalTorrentFilePath = this.SaveMagnetLink(data, handle.TorrentFile.Name);

            this.Torrents.Add(ti);

            NotificationManager.Notify(new MagnetLinkNotification(MagnetLinkNotification.EventType.MetadataDownloadComplete, handle));
        }

        void LibTorrentAlerts_TorrentFinished(Ragnar.TorrentHandle handle)
        {
            var results = this.Torrents.Where(t => t.InfoHash == handle.InfoHash.ToHex());
            if (results.Count() != 0)
            {
                results.First().DoTorrentComplete();
            }
        }

        void LibTorrentAlerts_TorrentStatsUpdated(Ragnar.TorrentStatus status)
        {
            var results = this.Torrents.Where(t => t.InfoHash == status.InfoHash.ToHex());
            if (results.Count() != 0)
            {
                results.First().DoStatsUpdate(status);
            }
        }

        void LibTorrentAlerts_TorrentStateChanged(Ragnar.TorrentHandle handle, Ragnar.TorrentState oldstate, Ragnar.TorrentState newstate)
        {
            var results = this.Torrents.Where(t => t.InfoHash == handle.InfoHash.ToHex());
            if (results.Count() != 0)
            {
                results.First().DoStateChanged(oldstate, newstate);
            }
        }

        void LibTorrentAlerts_TorrentAdded(Ragnar.TorrentHandle handle)
        {
            if (handle.HasMetadata)
            {
                uiContext.Post(_ =>
                {
                    if (!Torrents.Any(t => t.Torrent.InfoHash.ToHex() == handle.InfoHash.ToHex()))
                    {
                        Torrents.Add(new TorrentInfo(handle));
                    }
                }, null);
            }
        }

        void LibTorrentAlerts_ResumeDataArrived(Ragnar.TorrentHandle handle, byte[] data)
        {
            string path = Path.Combine(State.TorrentsStateSaveDirectory, handle.InfoHash.ToHex() + ".resume");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        #endregion

        public void ChangeNetworkInterface()
        {
            throw new NotImplementedException();
            // var new_iface = Utility.GetNetworkInterface(App.Settings.NetworkInterfaceID);
        }

        public void Shutdown()
        {
            SaveSettings();
            MainTaskCancellationTokenSource.Cancel();

            SaveState(true);

            this.LibtorrentSession.StopDht();
            this.LibtorrentSession.StopLsd();
            this.LibtorrentSession.StopNatPmp();
            this.LibtorrentSession.StopUpnp();

            this.LibtorrentSession.Pause();

            this.LibTorrentAlerts.StopWatching();

            while (this.LibTorrentAlerts.IsRunning) ; // Do nothing while waiting for all resume data events to be processed

            this.LibtorrentSession.Dispose();

            listener.Shutdown();
        }

        public void SaveSettings()
        {
            Settings.Save(App.Settings, "./config.xml");
        }

        public static string StateSaveDirectory
        {
            get { return Path.Combine(".", "state"); }
        }

        /// <summary>
        /// This is the directory where .torrent file, resume data (.resume) and
        /// misc settings (.tjson) are saved.
        /// </summary>
        public static string TorrentsStateSaveDirectory
        {
            get
            {
                return Path.Combine(StateSaveDirectory, "torrents-bkp");
            }
        }

        private string LtSessionFilePath
        {
            get
            {
                return Path.Combine(StateSaveDirectory, "ltsession.bin");
            }
        }

        public void SaveState(bool is_shuttingdown = false)
        {
            Directory.CreateDirectory(StateSaveDirectory);

            File.WriteAllBytes(this.LtSessionFilePath, this.LibtorrentSession.SaveState());

            for (int index = 0; index < this.Torrents.Count; index++)
            {
                try
                {
                    TorrentInfo ti = this.Torrents[index];
                    Ragnar.TorrentHandle handle = ti.Torrent;
                    if (!handle.HasMetadata) { continue; }

                    if (handle.NeedSaveResumeData())
                    {
                        handle.SaveResumeData();
                    }

                    if (is_shuttingdown)
                    {
                        //save misc settings

                        JsonObject jo = new JsonObject();
                        jo.Add("SavePath", handle.QueryStatus().SavePath);

                        //jo.Add("PickedMovieData", ti.PickedMovieData.ToString());

                        jo.Add("RanCommand", ti.RanCommand);

                        jo.Add("RatioLimit", ti.RatioLimit);

                        jo.Add("CompletionCommand", ti.CompletionCommand);

                        jo.Add("CustomName", ti.Name);

                        jo.Add("OriginalTorrentFilePath", ti.OriginalTorrentFilePath);

                        jo.Add("IsStopped", ti.IsStopped);

                        using (TextWriter tw = File.CreateText(
                            Path.Combine(State.TorrentsStateSaveDirectory, ti.InfoHash + ".tjson")))
                        {
                            JsonConvert.Export(jo, tw);
                        }
                    }
                }
                catch (System.IndexOutOfRangeException)
                {
                    break;
                }
                catch (Exception)
                {
                    continue;
                }
            }
        }

        public void AddTorrentsByPath(string[] paths)
        {
            foreach (string str in paths)
            {
                AddTorrentByPath(str, false);
            }
        }

        public void AddTorrentByPath(string path, bool notifyIfAdded = true)
        {
            try
            {
                Torrent t = Torrent.Load(path);

                if (this.ContainTorrent(t.InfoHash.ToHex()))
                {
                    if (notifyIfAdded)
                    {
                        NotificationManager.Notify(new TorrentAlreadyAddedNotification(t.Name, t.InfoHash.ToHex()));
                    }
                    return;
                }
                path = BackupTorrent(path, t);
            }
            catch (TorrentException)
            {
                MessageBox.Show(string.Format("Invalid torrent file {0}", path), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Could not load torrent {0}\n{1}", path, ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var handle = this.LibtorrentSession.AddTorrent(new Ragnar.AddTorrentParams()
            {
                TorrentInfo = new Ragnar.TorrentInfo(File.ReadAllBytes(path)),
                SavePath = App.Settings.DefaultDownloadPath,
            });

            handle.AutoManaged = false;
            handle.Pause();


            TorrentInfo ti = new TorrentInfo(handle);
            ti.OriginalTorrentFilePath = path;
            ti.ApplyTorrentSettings(App.Settings.DefaultTorrentProperties);

            uiContext.Send(x =>
            {
                App.Current.MainWindow.Activate();
                AddTorrentDialog atd = new AddTorrentDialog(ti) { Owner = App.Current.MainWindow, Icon = App.Current.MainWindow.Icon };
                atd.ShowDialog();
                if (atd.UserOK)
                {
                    ti.Name = atd.TorrentName;

                    if (atd.TorrentSavePath != ti.SavePath) 
                    {
                        ti.ChangeSavePath(atd.TorrentSavePath);
                    }

                    if (atd.AutoStartTorrent)
                    { ti.Start(); }
                    ti.RatioLimit = atd.RatioLimit;

                    if (!this.Torrents.Contains(ti))
                    {
                        this.Torrents.Add(ti);
                    }
                }
                else
                {
                    this.LibtorrentSession.RemoveTorrent(handle);
                    this.DeleteTorrentStateData(ti.InfoHash);
                    ti.OffMyself();
                    this.Torrents.Remove(ti);
                }
            }, null);
        }

        public void DeleteTorrentStateData(string infohash)
        {
            foreach (string e in (new string[] { ".torrent", ".tjson", ".resume" }))
            {
                string path = Path.Combine(State.TorrentsStateSaveDirectory, infohash + e);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        public bool ContainTorrent(string infoHash)
        {
            var handle = this.LibtorrentSession.FindTorrent(infoHash);
            return handle != null;
        }

        /// <summary>
        /// Copies a torrent to ./torrents-bkp
        /// </summary>
        /// <param name="path">The path of the torrent file to be copied.</param>
        /// <returns>The new path of the torrent file.</returns>
        public string BackupTorrent(string path, Torrent t)
        {
            string newpath = System.IO.Path.Combine(State.TorrentsStateSaveDirectory, t.InfoHash.ToHex() + ".torrent");
            if (new DirectoryInfo(newpath).FullName != new DirectoryInfo(path).FullName)
                File.Copy(path, newpath, true);
            return newpath;
        }

        private byte[] BackUpMangetLinkMetadata(Ragnar.TorrentInfo info)
        {
            Ragnar.TorrentCreator tc = new Ragnar.TorrentCreator(info);
            byte[] f = tc.Generate();
            tc.Dispose();
            string path = System.IO.Path.Combine(State.TorrentsStateSaveDirectory, info.InfoHash + ".torrent");
            File.WriteAllBytes(path, f);
            return f;
        }

        /// <summary>
        /// Used by the feed manager
        /// </summary>
        /// <param name="path"></param>
        /// <param name="entry"></param>
        /// <returns>Return weither the torrent was loaded</returns>
        public bool AddTorrentRss(string path, Services.RSS.RssUrlEntry entry)
        {
            if (!File.Exists(path)) { return false; }

            Ragnar.TorrentInfo torrent = new Ragnar.TorrentInfo(File.ReadAllBytes(path));

            if (torrent.IsValid)
            {
                if (this.ContainTorrent(torrent.InfoHash))
                {
                    return false;
                }
                else
                {
                    Directory.CreateDirectory(entry.DownloadDirectory);

                    this.Torrents.Add(new TorrentInfo(
                     this.LibtorrentSession.AddTorrent(new Ragnar.AddTorrentParams()
                     {
                         SavePath = entry.DownloadDirectory,
                         TorrentInfo = torrent,

                         DownloadLimit = entry.DefaultSettings.MaxDownloadSpeed,
                         MaxConnections = entry.DefaultSettings.MaxConnections,
                         MaxUploads = entry.DefaultSettings.UploadSlots,
                         UploadLimit = entry.DefaultSettings.MaxUploadSpeed
                     })));
                    return true;
                }
            }

            return false;
        }

        public void AddTorrentByMagnet(string magnet, bool notifyIfAdded = true)
        {
            MagnetLink mg = null;

            try { mg = new MagnetLink(magnet); }
            catch { MessageBox.Show("Invalid magnet link", "Error", MessageBoxButton.OK, MessageBoxImage.Error); return; }

            if (this.ContainTorrent(mg.InfoHash.ToHex()))
            {
                if (notifyIfAdded)
                {
                    NotificationManager.Notify(new TorrentAlreadyAddedNotification(mg.Name, mg.InfoHash.ToHex()));
                }
                return;
            }

            if (App.Settings.PreferMagnetCacheWebsites)
            {
                byte[] torrent_data = State.GetMagnetFromCache(mg); // TODO: Move this to a non-blocking call
                if (torrent_data != null)
                {
                    string path = this.SaveMagnetLink(torrent_data, mg.Name);
                    this.AddTorrentByPath(path);
                    return;
                }
            }

            this.LibtorrentSession.AsyncAddTorrent(new Ragnar.AddTorrentParams()
            {
                SavePath = App.Settings.DefaultDownloadPath,
                Url = magnet
            });

            NotificationManager.Notify(new MagnetLinkNotification(MagnetLinkNotification.EventType.MetadataDownloadStarted, mg));
        }

        /// <summary>
        /// Save a .torrent file inside the default download directory using
        /// the provided metadata bytes
        /// </summary>
        /// <param name="data"></param>
        /// <param name="torrent_name"></param>
        /// <returns>A path to where the file was saved</returns>
        private string SaveMagnetLink(byte[] data, string torrent_name)
        {
            string path = Path.Combine(App.Settings.DefaultDownloadPath, Utility.CleanFileName(torrent_name) + ".torrent");
            File.WriteAllBytes(path, data);
            return path;
        }

        #region Magnets From Cache Websites

        [XmlIgnore]
        private static Services.TorrentCache.ITorrentCache[] TorrentCaches = new Services.TorrentCache.ITorrentCache[] 
        {
            new Services.TorrentCache.TorCache(),
            new Services.TorrentCache.ZoinkIT(),     
            new Services.TorrentCache.Torrage()
        };

        public static byte[] GetMagnetFromCache(MagnetLink mg)
        {
            for (int i = 0; i < TorrentCaches.Length; i++)
            {
                byte[] res = TorrentCaches[i].Fetch(mg);

                if (res != null)
                    return res;
            }

            return null;
        }

        public static byte[] GetMagnetFromCache(string uri)
        {
            MagnetLink mg = null;

            try { mg = new MagnetLink(uri); }
            catch { return null; }

            return GetMagnetFromCache(mg);
        }

        #endregion

        #region INotifyPropertyChanged implementation

        public void NotifyChanged(params string[] props)
        {
            foreach (string str in props)
                NotifySinglePropertyChanged(str);
        }

        protected void NotifySinglePropertyChanged([CallerMemberName]string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        #endregion
    }
}
