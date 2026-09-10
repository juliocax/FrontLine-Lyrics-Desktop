using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private const string SetlistFmKeySetting = "SetlistFmApiKey";
        private const string ApiKeyMask = "••••••••••••";

        private readonly ObservableCollection<FestivalPlaylistVm> _festivalPlaylists = [];
        private readonly ObservableCollection<FestivalSongEntry> _festivalSongs = [];

        private bool _festivalModeActive;
        private bool _festivalAwaitingFirstTap;
        private string _lastFestivalSongKey = "";
        private bool _apiKeyMasked;
        private bool _apiKeySuppressChange;
        private bool _resizedForFestivalHub;
        private double _preFestivalHubWidth;
        private double _preFestivalHubHeight;
        private string _setlistApiKey = "";
        private FestivalPlaylistEntry? _editingPlaylist;
        private FestivalPlaylistEntry? _activeFestival;

        private bool IsFestivalHubOpen => FestivalHubPanel?.Visibility == Visibility.Visible;

        private void InitFestivalMode()
        {
            try
            {
                FestivalPlaylistList.ItemsSource = _festivalPlaylists;
                FestivalSongList.ItemsSource = _festivalSongs;
                LoadStoredApiKey();
                RefreshFestivalList();
            }
            catch (Exception ex) { CrashReporter.Log(ex, "InitFestivalMode"); }
        }

        private void LoadStoredApiKey()
        {
            _setlistApiKey = AppSettings.GetString(SetlistFmKeySetting, "") ?? "";
            _apiKeySuppressChange = true;
            try
            {
                if (!string.IsNullOrEmpty(_setlistApiKey))
                {
                    TxtFestivalApiKey.Password = ApiKeyMask;
                    _apiKeyMasked = true;
                }
                else
                {
                    TxtFestivalApiKey.Password = "";
                    _apiKeyMasked = false;
                }
            }
            finally { _apiKeySuppressChange = false; }
            RefreshApiKeyStateLabel();
        }

        private void RefreshApiKeyStateLabel()
        {
            if (LblFestivalApiKeyState == null) return;
            var t = uiStrings[currentAppLanguage];
            LblFestivalApiKeyState.Text = !string.IsNullOrEmpty(_setlistApiKey)
                ? t["FestivalApiKeySaved"]
                : t["FestivalApiKeyHint"];
        }

        private void TxtFestivalApiKey_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_apiKeySuppressChange) return;
            if (_apiKeyMasked)
            {
                _apiKeyMasked = false;
                string typed = TxtFestivalApiKey.Password ?? "";
                if (typed.StartsWith(ApiKeyMask, StringComparison.Ordinal))
                    typed = typed[ApiKeyMask.Length..];
                else if (typed.Contains('•'))
                    typed = typed.Replace("•", "");
                _apiKeySuppressChange = true;
                try { TxtFestivalApiKey.Password = typed; }
                finally { _apiKeySuppressChange = false; }
            }
        }

        private void BtnFestivalSaveKey_Click(object sender, RoutedEventArgs e)
        {
            if (_apiKeyMasked) return;
            string key = (TxtFestivalApiKey.Password ?? "").Trim();
            _setlistApiKey = key;
            AppSettings.SetString(SetlistFmKeySetting, key);
            LoadStoredApiKey();
        }

        private void FestivalApiLink_Click(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "FestivalApiLink");
                MessageBox.Show("Não foi possível abrir o link: " + ex.Message);
            }
            e.Handled = true;
        }

        private void BtnFestival_Click(object sender, RoutedEventArgs e)
        {
            OpenFestivalHub(showEditor: false);
        }

        internal void OpenFestivalHub(bool showEditor)
        {
            try
            {
                BtnMenu.IsChecked = false;
                SearchInputPanel.Visibility = Visibility.Collapsed;
                EnsureFestivalHubSize();
                LoadStoredApiKey();
                RefreshFestivalList();
                FestivalHubPanel.Visibility = Visibility.Visible;
                HomeControls.Visibility = Visibility.Collapsed;
                if (showEditor && _editingPlaylist != null)
                    ShowFestivalEditor(_editingPlaylist);
                else
                    ShowFestivalList();
                if (!isResizing) UpdateVisualState(false);
                Dispatcher.BeginInvoke(() =>
                {
                    try { TxtFestivalArtist.Focus(); } catch { }
                });
            }
            catch (Exception ex) { CrashReporter.Log(ex, "OpenFestivalHub"); }
        }

        private void EnsureFestivalHubSize()
        {
            if (_resizedForFestivalHub) return;
            _preFestivalHubWidth = Width;
            _preFestivalHubHeight = Height;
            if (Width < 760) Width = 760;
            if (Height < 430) Height = 430;
            _resizedForFestivalHub = true;
        }

        private void RestoreFestivalHubSize()
        {
            if (!_resizedForFestivalHub) return;
            Width = _preFestivalHubWidth;
            Height = _preFestivalHubHeight;
            _resizedForFestivalHub = false;
        }

        private void CloseFestivalHub(bool restoreHome)
        {
            FestivalHubPanel.Visibility = Visibility.Collapsed;
            LblFestivalFetchStatus.Text = "";
            if (restoreHome && !_festivalModeActive && currentAppStatus == "IDLE")
            {
                HomeControls.Visibility = Visibility.Visible;
                RestoreFestivalHubSize();
            }
            if (!isResizing) UpdateVisualState(isGhostMode);
        }

        private void PersistFestivalOnExit()
        {
            try
            {
                PersistEditingPlaylist();
                if (_activeFestival != null)
                    FestivalPlaylistStore.Upsert(_activeFestival);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "PersistFestivalOnExit"); }
        }

        private void BtnFestivalHubClose_Click(object sender, RoutedEventArgs e)
        {
            PersistEditingPlaylist();
            if (_festivalModeActive)
            {
                CloseFestivalHub(restoreHome: false);
                return;
            }
            CloseFestivalHub(restoreHome: true);
        }

        private void ShowFestivalList()
        {
            FestivalListView.Visibility = Visibility.Visible;
            FestivalEditorView.Visibility = Visibility.Collapsed;
            _editingPlaylist = _festivalModeActive ? _activeFestival : null;
        }

        private void ShowFestivalEditor(FestivalPlaylistEntry entry)
        {
            _editingPlaylist = entry;
            FestivalListView.Visibility = Visibility.Collapsed;
            FestivalEditorView.Visibility = Visibility.Visible;
            TxtFestivalPlaylistName.Text = string.IsNullOrWhiteSpace(entry.Name) ? entry.Artist : entry.Name;
            ReloadEditorSongs();
            var t = uiStrings[currentAppLanguage];
            BtnFestivalStart.Content = _festivalModeActive ? t["FestivalDone"] : t["FestivalStart"];
        }

        private void ReloadEditorSongs()
        {
            _festivalSongs.Clear();
            if (_editingPlaylist?.Songs == null) return;
            foreach (var s in _editingPlaylist.Songs)
                _festivalSongs.Add(s);
            LblFestivalSongsEmpty.Visibility = _festivalSongs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshFestivalList()
        {
            _festivalPlaylists.Clear();
            var t = uiStrings[currentAppLanguage];
            foreach (var entry in FestivalPlaylistStore.Load())
            {
                int n = entry.Songs?.Count ?? 0;
                string count = n == 1 ? t["FestivalSongOne"] : string.Format(t["FestivalSongMany"], n);
                string when = entry.UpdatedAtUtc.ToLocalTime().ToString("g");
                _festivalPlaylists.Add(new FestivalPlaylistVm(entry, count + " · " + when));
            }
            LblFestivalEmpty.Visibility = _festivalPlaylists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TxtFestivalArtist_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnFestivalCreate_Click(sender, e);
        }

        private async void BtnFestivalCreate_Click(object sender, RoutedEventArgs e)
        {
            string artist = (TxtFestivalArtist.Text ?? "").Trim();
            if (string.IsNullOrEmpty(artist)) return;

            var t = uiStrings[currentAppLanguage];
            BtnFestivalCreate.IsEnabled = false;
            LblFestivalFetchStatus.Text = t["FestivalFetching"];
            List<FestivalSongEntry>? songs = null;
            try
            {
                if (!string.IsNullOrEmpty(_setlistApiKey))
                    songs = await SetlistFmClient.FetchLatestSetlist(_setlistApiKey, artist);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "FestivalCreate"); }
            finally
            {
                BtnFestivalCreate.IsEnabled = true;
            }

            var entry = new FestivalPlaylistEntry
            {
                Name = artist,
                Artist = artist,
                Songs = songs ?? [],
            };
            FestivalPlaylistStore.Upsert(entry);
            TxtFestivalArtist.Text = "";
            RefreshFestivalList();
            LblFestivalFetchStatus.Text = songs == null || songs.Count == 0 ? t["FestivalNoSetlist"] : "";
            ShowFestivalEditor(entry);
        }

        private void FestivalPlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelectedPlaylist();
        }

        private void FestivalPlaylistList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (FindParentButton(e.OriginalSource as DependencyObject) != null)
                return;
            OpenSelectedPlaylist();
        }

        private void OpenSelectedPlaylist()
        {
            if (FestivalPlaylistList.SelectedItem is FestivalPlaylistVm vm)
                ShowFestivalEditor(vm.Entry);
        }

        private void BtnFestivalDeletePlaylist_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement fe || fe.DataContext is not FestivalPlaylistVm vm)
                return;
            var t = uiStrings[currentAppLanguage];
            var result = MessageBox.Show(t["FestivalDeleteConfirm"], t["FestivalMode"], MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            FestivalPlaylistStore.Delete(vm.Entry.Id);
            if (_editingPlaylist?.Id == vm.Entry.Id) _editingPlaylist = null;
            if (_activeFestival?.Id == vm.Entry.Id && _festivalModeActive)
                StopFestival(returnToHub: true);
            RefreshFestivalList();
        }

        private void BtnFestivalEditorBack_Click(object sender, RoutedEventArgs e)
        {
            PersistEditingPlaylist();
            if (_festivalModeActive)
            {
                CloseFestivalHub(restoreHome: false);
                return;
            }
            ShowFestivalList();
            RefreshFestivalList();
        }

        private void TxtFestivalPlaylistName_LostFocus(object sender, RoutedEventArgs e)
        {
            PersistEditingPlaylist();
        }

        private void PersistEditingPlaylist()
        {
            if (_editingPlaylist == null) return;
            string name = (TxtFestivalPlaylistName.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(name))
                _editingPlaylist.Name = name;
            _editingPlaylist.Songs = _festivalSongs.ToList();
            FestivalPlaylistStore.Upsert(_editingPlaylist);
            if (_activeFestival?.Id == _editingPlaylist.Id)
                _activeFestival = _editingPlaylist;
        }

        private void TxtFestivalAdd_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnFestivalAddSong_Click(sender, e);
        }

        private void BtnFestivalAddSong_Click(object sender, RoutedEventArgs e)
        {
            if (_editingPlaylist == null) return;
            string artist = (TxtFestivalAddArtist.Text ?? "").Trim();
            string song = (TxtFestivalAddSong.Text ?? "").Trim();
            if (string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(song)) return;

            var row = new FestivalSongEntry { Artist = artist, Song = song };
            _festivalSongs.Add(row);
            TxtFestivalAddArtist.Text = "";
            TxtFestivalAddSong.Text = "";
            LblFestivalSongsEmpty.Visibility = Visibility.Collapsed;
            PersistEditingPlaylist();
            if (_festivalModeActive)
            {
                SendCommand("FESTIVAL_ADD_SONG", artist: artist, song: song, extra: new Dictionary<string, object?> { ["make_current"] = false });
            }
        }

        private FestivalSongEntry? SongFromSender(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as FestivalSongEntry;
        }

        private void BtnFestivalSongUp_Click(object sender, RoutedEventArgs e)
        {
            var row = SongFromSender(sender);
            if (row == null) return;
            int i = _festivalSongs.IndexOf(row);
            if (i <= 0) return;
            _festivalSongs.Move(i, i - 1);
            PersistEditingPlaylist();
            if (_festivalModeActive)
                SendCommand("FESTIVAL_REORDER", extra: new Dictionary<string, object?> { ["from_index"] = i, ["to_index"] = i - 1 });
        }

        private void BtnFestivalSongDown_Click(object sender, RoutedEventArgs e)
        {
            var row = SongFromSender(sender);
            if (row == null) return;
            int i = _festivalSongs.IndexOf(row);
            if (i < 0 || i >= _festivalSongs.Count - 1) return;
            _festivalSongs.Move(i, i + 1);
            PersistEditingPlaylist();
            if (_festivalModeActive)
                SendCommand("FESTIVAL_REORDER", extra: new Dictionary<string, object?> { ["from_index"] = i, ["to_index"] = i + 1 });
        }

        private void BtnFestivalSongDelete_Click(object sender, RoutedEventArgs e)
        {
            var row = SongFromSender(sender);
            if (row == null) return;
            int i = _festivalSongs.IndexOf(row);
            if (i < 0) return;
            _festivalSongs.RemoveAt(i);
            LblFestivalSongsEmpty.Visibility = _festivalSongs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PersistEditingPlaylist();
            if (_festivalModeActive)
                SendCommand("FESTIVAL_REMOVE_SONG", extra: new Dictionary<string, object?> { ["index"] = i });
        }

        private void BtnFestivalStart_Click(object sender, RoutedEventArgs e)
        {
            if (_editingPlaylist == null) return;
            PersistEditingPlaylist();

            if (_festivalModeActive)
            {
                CloseFestivalHub(restoreHome: false);
                return;
            }

            StartFestival(_editingPlaylist);
        }

        private void StartFestival(FestivalPlaylistEntry playlist)
        {
            TurnAutoOff();
            _activeFestival = playlist;
            _festivalModeActive = true;
            _festivalAwaitingFirstTap = true;
            CloseFestivalHub(restoreHome: false);
            HomeControls.Visibility = Visibility.Collapsed;

            var songs = playlist.Songs.Select(s => new Dictionary<string, string>
            {
                ["artist"] = s.Artist,
                ["song"] = s.Song,
            }).ToList();

            SendCommand("FESTIVAL_ENTER", extra: new Dictionary<string, object?>
            {
                ["name"] = string.IsNullOrWhiteSpace(playlist.Name) ? playlist.Artist : playlist.Name,
                ["songs"] = songs,
            });

            ApplyFestivalPlayingChrome(true);
            if (!isResizing) UpdateVisualState(false);
        }

        private void BtnFestivalStop_Click(object sender, RoutedEventArgs e) => StopFestival(returnToHub: true);

        private void StopFestival(bool returnToHub)
        {
            try
            {
                if (_activeFestival != null)
                    FestivalPlaylistStore.Upsert(_activeFestival);
                SendCommand("FESTIVAL_EXIT");
                _festivalModeActive = false;
                _festivalAwaitingFirstTap = false;
                isManualSyncMode = false;
                FullLyricsList.Visibility = Visibility.Collapsed;
                LyricsNormalView.Visibility = Visibility.Visible;
                ApplyFestivalPlayingChrome(false);
                _activeFestival = null;
                if (returnToHub)
                    OpenFestivalHub(showEditor: false);
            }
            catch (Exception ex) { CrashReporter.Log(ex, "StopFestival"); }
        }

        private void BtnFestivalEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_activeFestival == null) return;
            _editingPlaylist = _activeFestival;
            OpenFestivalHub(showEditor: true);
        }

        private void BtnFestivalPrevLine_Click(object sender, RoutedEventArgs e)
        {
            _festivalAwaitingFirstTap = false;
            isManualSyncMode = false;
            FullLyricsList.Visibility = Visibility.Collapsed;
            LyricsNormalView.Visibility = Visibility.Visible;
            SendCommand("FESTIVAL_JUMP_LINE", extra: new Dictionary<string, object?> { ["delta"] = -1 });
        }

        private void BtnFestivalNextLine_Click(object sender, RoutedEventArgs e)
        {
            _festivalAwaitingFirstTap = false;
            isManualSyncMode = false;
            FullLyricsList.Visibility = Visibility.Collapsed;
            LyricsNormalView.Visibility = Visibility.Visible;
            SendCommand("FESTIVAL_JUMP_LINE", extra: new Dictionary<string, object?> { ["delta"] = 1 });
        }

        private void ApplyFestivalPlayingChrome(bool on)
        {
            if (PlayingControlsNormal == null) return;
            PlayingControlsNormal.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            PlayingControlsFestival.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyFestivalFromServer(bool festivalMode, string artist, string song)
        {
            bool wasActive = _festivalModeActive;
            _festivalModeActive = festivalMode;
            ApplyFestivalPlayingChrome(festivalMode);

            string key = $"{artist}|{song}".ToLowerInvariant();
            if (festivalMode && !string.IsNullOrEmpty(song) && key != _lastFestivalSongKey)
            {
                _lastFestivalSongKey = key;
                _festivalAwaitingFirstTap = true;
            }
            if (festivalMode && !wasActive)
                _festivalAwaitingFirstTap = true;
            if (!festivalMode)
                _lastFestivalSongKey = "";

            if (festivalMode && _festivalAwaitingFirstTap && !string.IsNullOrEmpty(song) && !IsFestivalHubOpen)
            {
                isManualSyncMode = true;
                if (FullLyricsList.ItemsSource != null)
                {
                    FullLyricsList.Visibility = Visibility.Visible;
                    LyricsNormalView.Visibility = Visibility.Collapsed;
                }
            }

            if (!festivalMode && wasActive && !IsFestivalHubOpen)
            {
                _festivalAwaitingFirstTap = false;
                isManualSyncMode = false;
                FullLyricsList.Visibility = Visibility.Collapsed;
                ApplyFestivalPlayingChrome(false);
            }
        }

        private void OnFestivalLinePicked()
        {
            _festivalAwaitingFirstTap = false;
        }

        private void RememberFestivalSearch(string artist, string song)
        {
            if (!_festivalModeActive) return;
            var target = _activeFestival ?? _editingPlaylist;
            if (target == null) return;

            bool exists = target.Songs.Any(s =>
                string.Equals(s.Artist, artist, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Song, song, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                var row = new FestivalSongEntry { Artist = artist, Song = song };
                target.Songs.Add(row);
                if (_editingPlaylist?.Id == target.Id)
                    _festivalSongs.Add(row);
            }
            FestivalPlaylistStore.Upsert(target);
            SendCommand("FESTIVAL_ADD_SONG", artist: artist, song: song, extra: new Dictionary<string, object?> { ["make_current"] = true });
        }

        internal void ApplyFestivalUiLanguage()
        {
            if (!uiStrings.TryGetValue(currentAppLanguage, out var t)) return;
            BtnFestivalBig.Content = t["Festival"];
            LblFestivalTitle.Text = t["FestivalMode"];
            LblFestivalIntro.Text = t["FestivalIntro"];
            LblFestivalApiKey.Text = t["FestivalApiKey"];
            BtnFestivalSaveKey.Content = t["FestivalSaveKey"];
            TxtFestivalApiLink.Text = t["FestivalApiLink"];
            BtnFestivalHubClose.Content = t["FestivalClose"];
            LblFestivalPlaylists.Text = t["FestivalPlaylists"];
            LblFestivalNewArtist.Text = t["FestivalNewArtist"];
            BtnFestivalCreate.Content = t["FestivalCreate"];
            LblFestivalEmpty.Text = t["FestivalEmpty"];
            LblFestivalEditorTitle.Text = t["FestivalEditorTitle"];
            BtnFestivalEditorBack.Content = t["FestivalBack"];
            LblFestivalSongsEmpty.Text = t["FestivalSongsEmpty"];
            LblFestivalAddArtist.Text = t["Artist"].TrimEnd(':');
            LblFestivalAddSong.Text = t["Song"].TrimEnd(':');
            BtnFestivalAddSong.Content = t["FestivalAdd"];
            BtnFestivalStart.Content = _festivalModeActive ? t["FestivalDone"] : t["FestivalStart"];
            BtnFestivalEdit.Content = t["FestivalEdit"];
            BtnFestivalStop.Content = t["FestivalStop"];
            BtnFestivalPrevLine.ToolTip = t["FestivalPrevLine"];
            BtnFestivalNextLine.ToolTip = t["FestivalNextLine"];
            RefreshApiKeyStateLabel();
            RefreshFestivalList();
        }
    }

    internal sealed class FestivalPlaylistVm
    {
        public FestivalPlaylistEntry Entry { get; }
        public string Title { get; }
        public string Meta { get; }

        public FestivalPlaylistVm(FestivalPlaylistEntry entry, string meta)
        {
            Entry = entry;
            Title = string.IsNullOrWhiteSpace(entry.Name) ? entry.Artist : entry.Name;
            Meta = meta;
        }
    }
}
