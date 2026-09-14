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
using System.Threading.Tasks;

namespace FrontLineOverlay
{
    public partial class MainWindow
    {
        private const string SetlistFmKeySetting = "SetlistFmApiKey";
        private const string ApiKeyMask = "••••••••••••";

        private readonly ObservableCollection<FestivalPlaylistVm> _festivalPlaylists = [];
        private readonly ObservableCollection<FestivalSongEntry> _festivalSongs = [];
        private readonly ObservableCollection<SetlistFmResult> _festivalSearchResults = [];

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
                FestivalSetlistSearchList.ItemsSource = _festivalSearchResults;
                LoadStoredApiKey();
                RefreshFestivalList();
                RefreshFestivalCreateMode();
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
            RefreshFestivalCreateMode();
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
                AlertDark("Não foi possível abrir o link: " + ex.Message);
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
                SetOverlayBackVisible(true);
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
            if (Height < 470) Height = 470;
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
            ClearFestivalSearchResults();
            if (SearchInputPanel?.Visibility != Visibility.Visible)
                SetOverlayBackVisible(false);
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

        private void SetOverlayBackVisible(bool visible)
        {
            if (BtnOverlayBack != null)
                BtnOverlayBack.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnOverlayBack_Click(object sender, RoutedEventArgs e)
        {
            if (SearchInputPanel.Visibility == Visibility.Visible)
            {
                BtnSearchCancel_Click(sender, e);
                return;
            }
            if (FestivalEditorView.Visibility == Visibility.Visible)
            {
                BtnFestivalEditorBack_Click(sender, e);
                return;
            }
            BtnFestivalHubClose_Click(sender, e);
        }

        private bool HasSetlistApiKey => !string.IsNullOrWhiteSpace(_setlistApiKey);

        private void RefreshFestivalCreateMode()
        {
            if (LblFestivalNewArtist == null) return;
            var t = uiStrings[currentAppLanguage];
            if (FestivalSearchFilters != null)
                FestivalSearchFilters.Visibility = HasSetlistApiKey ? Visibility.Visible : Visibility.Collapsed;
            if (HasSetlistApiKey)
            {
                LblFestivalNewArtist.Text = t["FestivalSearchArtist"];
                BtnFestivalCreate.Content = t["FestivalSearch"];
                LblFestivalEmpty.Text = t["FestivalEmptySearch"];
            }
            else
            {
                LblFestivalNewArtist.Text = t["FestivalPlaylistName"];
                BtnFestivalCreate.Content = t["FestivalCreate"];
                LblFestivalEmpty.Text = t["FestivalEmpty"];
                ClearFestivalSearchResults();
            }
        }

        private void ClearFestivalSearchResults()
        {
            _festivalSearchResults.Clear();
            if (FestivalSearchResultsPanel != null)
                FestivalSearchResultsPanel.Visibility = Visibility.Collapsed;
            if (BtnFestivalCreateFromResult != null)
                BtnFestivalCreateFromResult.Visibility = Visibility.Collapsed;
            if (LblFestivalNoResults != null)
                LblFestivalNoResults.Visibility = Visibility.Collapsed;
            if (FestivalSetlistSearchList != null)
                FestivalSetlistSearchList.Visibility = Visibility.Visible;
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
            string query = (TxtFestivalArtist.Text ?? "").Trim();
            if (string.IsNullOrEmpty(query)) return;

            if (!HasSetlistApiKey)
            {
                CreateBlankPlaylist(query);
                return;
            }

            await SearchSetlists(query);
        }

        private void CreateBlankPlaylist(string name)
        {
            var entry = new FestivalPlaylistEntry
            {
                Name = name,
                Artist = name,
                Songs = [],
            };
            FestivalPlaylistStore.Upsert(entry);
            TxtFestivalArtist.Text = "";
            RefreshFestivalList();
            LblFestivalFetchStatus.Text = "";
            ShowFestivalEditor(entry);
        }

        private async Task SearchSetlists(string artist)
        {
            var t = uiStrings[currentAppLanguage];
            BtnFestivalCreate.IsEnabled = false;
            LblFestivalFetchStatus.Text = t["FestivalFetching"];
            ClearFestivalSearchResults();
            try
            {
                var results = await SetlistFmClient.FetchRecentSetlists(
                    _setlistApiKey,
                    artist,
                    TxtFestivalCountry.Text,
                    TxtFestivalVenue.Text,
                    TxtFestivalYear.Text,
                    10);
                _festivalSearchResults.Clear();
                foreach (var r in results)
                    _festivalSearchResults.Add(r);
                bool empty = results.Count == 0;
                FestivalSearchResultsPanel.Visibility = Visibility.Visible;
                FestivalSetlistSearchList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                LblFestivalNoResults.Text = t["FestivalNoResults"];
                LblFestivalNoResults.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                LblFestivalFetchStatus.Text = "";
                BtnFestivalCreateFromResult.Visibility = Visibility.Collapsed;
            }
            catch (InvalidOperationException)
            {
                LblFestivalFetchStatus.Text = t["FestivalApiKeyBad"];
            }
            catch (Exception ex)
            {
                CrashReporter.Log(ex, "FestivalSearch");
                FestivalSearchResultsPanel.Visibility = Visibility.Visible;
                FestivalSetlistSearchList.Visibility = Visibility.Collapsed;
                LblFestivalNoResults.Text = t["FestivalNoResults"];
                LblFestivalNoResults.Visibility = Visibility.Visible;
                LblFestivalFetchStatus.Text = "";
            }
            finally
            {
                BtnFestivalCreate.IsEnabled = true;
            }
        }

        private void FestivalSetlistSearchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnFestivalCreateFromResult.Visibility =
                FestivalSetlistSearchList.SelectedItem is SetlistFmResult ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnFestivalCreateFromResult_Click(object sender, RoutedEventArgs e)
        {
            if (FestivalSetlistSearchList.SelectedItem is not SetlistFmResult pick) return;
            var entry = new FestivalPlaylistEntry
            {
                Name = pick.SuggestedName,
                Artist = pick.Artist,
                Songs = pick.Songs.ToList(),
            };
            FestivalPlaylistStore.Upsert(entry);
            TxtFestivalArtist.Text = "";
            TxtFestivalCountry.Text = "";
            TxtFestivalVenue.Text = "";
            TxtFestivalYear.Text = "";
            ClearFestivalSearchResults();
            RefreshFestivalList();
            LblFestivalFetchStatus.Text = "";
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
            if (!ConfirmDark(t["FestivalMode"], t["FestivalDeleteConfirm"])) return;
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

        private Point _festivalDragStart;
        private FestivalSongEntry? _festivalDragItem;
        private ListBoxItem? _festivalDragContainer;
        private Brush? _festivalDragBorderBrush;
        private Brush? _festivalDragBackground;

        private void FestivalSongList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindVisualAncestor<Button>(e.OriginalSource as DependencyObject) != null)
            {
                _festivalDragItem = null;
                return;
            }
            _festivalDragStart = e.GetPosition(null);
            _festivalDragItem = FestivalSongAt(e.OriginalSource as DependencyObject)
                ?? FestivalSongAtPoint(e.GetPosition(FestivalSongList));
        }

        private void FestivalSongList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _festivalDragItem == null) return;
            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _festivalDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pos.Y - _festivalDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            var payload = _festivalDragItem;
            BeginFestivalDragVisual(payload);
            try
            {
                DragDrop.DoDragDrop(FestivalSongList, payload, DragDropEffects.Move);
            }
            finally
            {
                EndFestivalDragVisual();
                _festivalDragItem = null;
            }
        }

        private void FestivalSongList_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            e.UseDefaultCursors = false;
            Mouse.SetCursor(Cursors.SizeAll);
            e.Handled = true;
        }

        private void FestivalSongList_DragOver(object sender, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(typeof(FestivalSongEntry));
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            if (ok)
                ShowFestivalDropCue(FestivalInsertIndex(e.GetPosition(FestivalSongListHost)));
            else if (FestivalDropCue != null)
                FestivalDropCue.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        private void FestivalSongList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(FestivalSongEntry)) is not FestivalSongEntry moved) return;
            int from = _festivalSongs.IndexOf(moved);
            if (from < 0) return;
            int insertAt = FestivalInsertIndex(e.GetPosition(FestivalSongListHost));
            if (from < insertAt) insertAt--;
            insertAt = Math.Clamp(insertAt, 0, Math.Max(0, _festivalSongs.Count - 1));
            if (from == insertAt) return;
            _festivalSongs.Move(from, insertAt);
            PersistEditingPlaylist();
            if (_festivalModeActive)
            {
                SendCommand("FESTIVAL_REORDER", extra: new Dictionary<string, object?>
                {
                    ["from_index"] = from,
                    ["to_index"] = insertAt,
                });
            }
            e.Handled = true;
        }

        private int FestivalInsertIndex(Point posInHost)
        {
            int count = _festivalSongs.Count;
            if (count == 0) return 0;
            for (int i = 0; i < count; i++)
            {
                if (FestivalSongList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
                    continue;
                Point top = item.TranslatePoint(new Point(0, 0), FestivalSongListHost);
                if (posInHost.Y < top.Y + item.ActualHeight / 2)
                    return i;
            }
            return count;
        }

        private void ShowFestivalDropCue(int insertAt)
        {
            if (FestivalDropCue == null || FestivalSongListHost == null) return;
            double y;
            int count = _festivalSongs.Count;
            if (count == 0)
            {
                y = 6;
            }
            else if (insertAt >= count)
            {
                if (FestivalSongList.ItemContainerGenerator.ContainerFromIndex(count - 1) is not ListBoxItem last)
                {
                    FestivalDropCue.Visibility = Visibility.Collapsed;
                    return;
                }
                y = last.TranslatePoint(new Point(0, last.ActualHeight), FestivalSongListHost).Y;
            }
            else
            {
                if (FestivalSongList.ItemContainerGenerator.ContainerFromIndex(insertAt) is not ListBoxItem item)
                {
                    FestivalDropCue.Visibility = Visibility.Collapsed;
                    return;
                }
                y = item.TranslatePoint(new Point(0, 0), FestivalSongListHost).Y;
            }

            FestivalDropCue.Width = Math.Max(48, FestivalSongListHost.ActualWidth - 16);
            Canvas.SetLeft(FestivalDropCue, 8);
            Canvas.SetTop(FestivalDropCue, y - 1.5);
            FestivalDropCue.Visibility = Visibility.Visible;
        }

        private void BeginFestivalDragVisual(FestivalSongEntry song)
        {
            _festivalDragContainer = FestivalSongList.ItemContainerGenerator.ContainerFromItem(song) as ListBoxItem;
            if (_festivalDragContainer == null) return;
            _festivalDragContainer.Opacity = 0.4;
            _festivalDragContainer.RenderTransformOrigin = new Point(0.5, 0.5);
            _festivalDragContainer.RenderTransform = new ScaleTransform(0.97, 0.97);
            if (_festivalDragContainer.Template?.FindName("Bd", _festivalDragContainer) is Border bd)
            {
                _festivalDragBackground = bd.Background;
                _festivalDragBorderBrush = bd.BorderBrush;
                bd.Background = new SolidColorBrush(Color.FromArgb(0x70, 0x7A, 0x53, 0xE4));
                bd.BorderBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0xC4, 0xB5, 0xFD));
            }
        }

        private void EndFestivalDragVisual()
        {
            if (FestivalDropCue != null)
                FestivalDropCue.Visibility = Visibility.Collapsed;
            if (_festivalDragContainer != null)
            {
                _festivalDragContainer.Opacity = 1;
                _festivalDragContainer.RenderTransform = Transform.Identity;
                if (_festivalDragContainer.Template?.FindName("Bd", _festivalDragContainer) is Border bd)
                {
                    bd.Background = _festivalDragBackground ?? Brushes.Transparent;
                    bd.BorderBrush = _festivalDragBorderBrush ?? Brushes.Transparent;
                }
            }
            _festivalDragContainer = null;
            _festivalDragBackground = null;
            _festivalDragBorderBrush = null;
        }

        private FestivalSongEntry? FestivalSongAt(DependencyObject? source)
        {
            var item = FindVisualAncestor<ListBoxItem>(source);
            return item?.DataContext as FestivalSongEntry;
        }

        private FestivalSongEntry? FestivalSongAtPoint(Point p)
        {
            return FestivalSongAt(FestivalSongList.InputHitTest(p) as DependencyObject);
        }

        private static T? FindVisualAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
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

        private void BtnFestivalPrevTrack_Click(object sender, RoutedEventArgs e)
        {
            SendCommand("FESTIVAL_PREV_SONG");
        }

        private void BtnFestivalNextTrack_Click(object sender, RoutedEventArgs e)
        {
            SendCommand("FESTIVAL_NEXT_SONG");
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
            SyncFestivalLineSkip();
        }

        private void ApplyFestivalFromServer(bool festivalMode, string artist, string song, string status, string? nextSong, string? nextArtist)
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

            bool offerAdvance = festivalMode && !string.IsNullOrWhiteSpace(nextSong);
            UpdateFestivalAdvancePanel(offerAdvance, nextSong, nextArtist);

            bool hasLyricRows = FullLyricsList.ItemsSource is System.Collections.IEnumerable rows
                && rows.Cast<object>().Any();
            bool showFirstTap = festivalMode && _festivalAwaitingFirstTap && !offerAdvance
                && status == "SYNCED" && hasLyricRows && !IsFestivalHubOpen;

            if (offerAdvance)
            {
                isManualSyncMode = false;
                FullLyricsList.Visibility = Visibility.Collapsed;
                LyricsNormalView.Visibility = Visibility.Collapsed;
            }
            else if (showFirstTap)
            {
                isManualSyncMode = true;
                FullLyricsList.Visibility = Visibility.Visible;
                LyricsNormalView.Visibility = Visibility.Collapsed;
            }
            else if (festivalMode && (status == "NOT_FOUND" || status == "SEARCHING"))
            {
                isManualSyncMode = false;
                FullLyricsList.Visibility = Visibility.Collapsed;
                if (status == "NOT_FOUND")
                    LyricsNormalView.Visibility = Visibility.Visible;
            }

            if (!festivalMode && wasActive && !IsFestivalHubOpen)
            {
                _festivalAwaitingFirstTap = false;
                isManualSyncMode = false;
                FullLyricsList.Visibility = Visibility.Collapsed;
                if (FestivalAdvanceView != null) FestivalAdvanceView.Visibility = Visibility.Collapsed;
                ApplyFestivalPlayingChrome(false);
            }
        }

        private void UpdateFestivalAdvancePanel(bool show, string? song, string? artist)
        {
            if (FestivalAdvanceView == null) return;
            if (!show)
            {
                FestivalAdvanceView.Visibility = Visibility.Collapsed;
                return;
            }

            var t = uiStrings[currentAppLanguage];
            LblFestivalAdvanceHint.Text = t["FestivalNextUp"];
            LblFestivalAdvanceSong.Text = song ?? "";
            LblFestivalAdvanceArtist.Text = artist ?? "";
            BtnFestivalAdvance.Content = t["FestivalContinue"];
            FestivalAdvanceView.Visibility = Visibility.Visible;
        }

        private void BtnFestivalAdvance_Click(object sender, RoutedEventArgs e)
        {
            if (FestivalAdvanceView != null)
                FestivalAdvanceView.Visibility = Visibility.Collapsed;
            SendCommand("FESTIVAL_NEXT_SONG");
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
            BtnFestivalBig.ToolTip = t["TipFestival"];
            TxtFestivalBig.Text = t["Festival"];
            LblFestivalTitle.Text = t["FestivalMode"];
            LblFestivalIntro.Text = t["FestivalIntro"];
            LblFestivalApiKey.Text = t["FestivalApiKey"];
            BtnFestivalSaveKey.Content = t["FestivalSaveKey"];
            TxtFestivalApiLink.Text = t["FestivalApiLink"];
            LblFestivalPlaylists.Text = t["FestivalPlaylists"];
            LblFestivalSearchCountry.Text = t["FestivalSearchCountry"];
            LblFestivalSearchVenue.Text = t["FestivalSearchVenue"];
            LblFestivalSearchYear.Text = t["FestivalSearchYear"];
            LblFestivalSearchResults.Text = t["FestivalSearchResults"];
            LblFestivalNoResults.Text = t["FestivalNoResults"];
            BtnFestivalCreateFromResult.Content = t["FestivalCreateFromResult"];
            LblFestivalEditorTitle.Text = t["FestivalEditorTitle"];
            LblFestivalSongsEmpty.Text = t["FestivalSongsEmpty"];
            if (FestivalSongList != null) FestivalSongList.ToolTip = t["FestivalDragHint"];
            LblFestivalAddArtist.Text = t["Artist"].TrimEnd(':');
            LblFestivalAddSong.Text = t["Song"].TrimEnd(':');
            BtnFestivalAddSong.Content = t["FestivalAdd"];
            BtnFestivalStart.Content = _festivalModeActive ? t["FestivalDone"] : t["FestivalStart"];
            if (BtnFestivalAdvance != null) BtnFestivalAdvance.Content = t["FestivalContinue"];
            if (LblFestivalAdvanceHint != null) LblFestivalAdvanceHint.Text = t["FestivalNextUp"];
            TxtFestivalEdit.Text = t["FestivalEdit"];
            TxtFestivalStop.Text = t["FestivalStop"];
            RefreshFestivalCreateMode();
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
