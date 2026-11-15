using System;
using System.Collections.Generic;
using System.IO;
using Action_Wheel.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using Windows.Storage.Pickers;

namespace Action_Wheel.Settings
{
    /// <summary>A single persistent scratch pad. App owns the window's singleton lifetime.</summary>
    public sealed class NoteBoardWindow : Window
    {
        private readonly NoteBoardStore _store;
        private readonly bool _rememberLocation;
        private readonly Grid _root;
        private readonly TextBox _editor;
        private readonly TextBlock _status;
        private readonly InfoBar _error;
        private readonly Button _retryLoad;
        private readonly Button _saveTo;
        private readonly TextBlock _location;
        private readonly DispatcherQueueTimer _saveTimer;
        private bool _loading;
        private bool _loadSucceeded;
        private bool _dirty;
        private bool _choosingLocation;
        private bool _composingText;

        public NoteBoardWindow(string? filePath = null) : this(new NoteBoardStore(filePath), filePath == null) { }

        internal NoteBoardWindow(NoteBoardStore store, bool rememberLocation = false)
        {
            _store = store;
            _rememberLocation = rememberLocation;
            Title = "Action Wheel · 笔记板";
            AppIcon.ApplyTo(this);

            _root = new Grid { Padding = new Thickness(20), RowSpacing = 12 };
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = "随手记", FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
            var save = new Button { Content = "保存" };
            save.Click += (s, e) => TrySave();
            Grid.SetColumn(save, 1);
            header.Children.Add(save);
            _saveTo = new Button { Content = "保存到…" };
            _saveTo.Click += SaveTo_Click;
            Grid.SetColumn(_saveTo, 2);
            header.Children.Add(_saveTo);
            _root.Children.Add(header);

            _location = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true };
            Grid.SetRow(_location, 4);
            _root.Children.Add(_location);

            _error = new InfoBar
            {
                IsOpen = false,
                IsClosable = false,
                Severity = InfoBarSeverity.Error,
                Title = "笔记仍保留在窗口中",
            };
            Grid.SetRow(_error, 1);
            _root.Children.Add(_error);

            _editor = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 16,
                PlaceholderText = "记下想法、临时内容……输入会自动保存。",
                Padding = new Thickness(12),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
            _editor.TextChanged += Editor_TextChanged;
            _editor.TextCompositionStarted += (s, e) => _composingText = true;
            _editor.TextCompositionEnded += (s, e) => _composingText = false;
            Grid.SetRow(_editor, 2);
            _root.Children.Add(_editor);

            var footer = new Grid { ColumnSpacing = 12 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            footer.Children.Add(_status);
            _retryLoad = new Button { Content = "重新读取", Visibility = Visibility.Collapsed };
            _retryLoad.Click += RetryLoad_Click;
            Grid.SetColumn(_retryLoad, 1);
            footer.Children.Add(_retryLoad);
            Grid.SetRow(footer, 3);
            _root.Children.Add(footer);
            Content = _root;

            _saveTimer = DispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (s, e) => TrySave();
            _root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
            _root.Loaded += (s, e) =>
            {
                WindowSizing.SizeAndCentre(this, _root, 620, 560, "the note board");
                FocusEditor();
            };
            AppWindow.Closing += (s, e) =>
            {
                _saveTimer.Stop();
                if (_choosingLocation || (_dirty && !TrySave()))
                    e.Cancel = true;
            };
            Closed += (s, e) => _saveTimer.Stop();
            LoadDraft();
        }

        public void FocusEditor() => _editor.Focus(FocusState.Programmatic);

        /// <summary>App must honour false before exiting; a forced application exit cannot save a failed draft.</summary>
        public bool TrySave()
        {
            _saveTimer.Stop();
            if (!_loadSucceeded)
                return !_dirty;
            if (!_store.TrySave(_editor.Text, out string error))
            {
                ShowError("保存失败：" + error);
                return false;
            }
            _dirty = false;
            _error.IsOpen = false;
            _retryLoad.Visibility = Visibility.Collapsed;
            _status.Text = $"{_editor.Text.Length} 字 · 已保存 · Esc 关闭";
            UpdateLocation();
            return true;
        }

        private void LoadDraft()
        {
            // A retry is only offered after an initial read failure, when editing is disabled.
            // Never replace a live, dirty editor with a reload that could discard its contents.
            if (_dirty)
                return;
            if (_rememberLocation)
            {
                if (!NoteBoardLocationSettings.TryLoad(out string path, out string locationError))
                {
                    _loadSucceeded = false;
                    _editor.IsEnabled = _saveTo.IsEnabled = false;
                    ShowError("无法读取保存位置，已阻止覆盖旧笔记：" + locationError);
                    return;
                }
                if (File.Exists(NoteBoardLocationSettings.FilePath) && !File.Exists(path))
                {
                    _loadSucceeded = false;
                    _editor.IsEnabled = _saveTo.IsEnabled = false;
                    ShowError("所选笔记文件不存在或暂时无法访问。保存位置保持原样；恢复文件或连接驱动器后重新读取。");
                    return;
                }
                // Load the remembered target without writing it. A temporarily unavailable drive
                // reports a read error instead of silently starting a different empty board.
                if (!string.Equals(Path.GetFullPath(_store.FilePath), path, StringComparison.OrdinalIgnoreCase))
                {
                    _store.SetLoadPath(path);
                }
            }
            _loadSucceeded = _store.TryLoad(out string text, out string error);
            _editor.IsEnabled = _saveTo.IsEnabled = _loadSucceeded;
            _retryLoad.Visibility = _loadSucceeded ? Visibility.Collapsed : Visibility.Visible;
            if (!_loadSucceeded)
            {
                ShowError("无法读取笔记，原文件保持原样：" + error);
                return;
            }
            _loading = true;
            _editor.Text = text;
            _loading = false;
            _error.IsOpen = false;
            _status.Text = $"{text.Length} 字 · 自动保存 · Esc 关闭";
            UpdateLocation();
            FocusEditor();
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || !_loadSucceeded)
                return;
            _dirty = true;
            _status.Text = $"{_editor.Text.Length} 字 · 正在保存…";
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void ShowError(string message)
        {
            _error.Message = message;
            _error.IsOpen = true;
            _retryLoad.Content = _dirty ? "另存副本并重新读取" : "重新读取";
            _retryLoad.Visibility = Visibility.Visible;
            _status.Text = "未保存；请保留此窗口，修复后点“保存”重试。";
        }

        private void RetryLoad_Click(object sender, RoutedEventArgs e)
        {
            string copyPath = string.Empty;
            if (_dirty)
            {
                if (!_store.TrySaveCopy(_editor.Text, out copyPath, out string error))
                {
                    ShowError("无法保留当前输入，已取消重新读取：" + error);
                    return;
                }
                _dirty = false;
            }
            LoadDraft();
            if (_loadSucceeded && copyPath.Length > 0)
                _status.Text = "已重新读取。原输入副本：" + copyPath;
        }

        private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Escape first belongs to an IME's candidate/composition UI. Closing the board on
            // that same handled key would discard a Chinese input session unexpectedly.
            if (e.Key == VirtualKey.Escape && !_composingText && !e.Handled)
            {
                e.Handled = true;
                Close();
            }
        }

        private void UpdateLocation() => _location.Text = "自动保存到：" + _store.FilePath;

        private async void SaveTo_Click(object sender, RoutedEventArgs e)
        {
            if (_choosingLocation || !_loadSucceeded)
                return;
            _saveTimer.Stop();
            // Save the old location first where possible. If it is no longer writable, choosing a
            // new file still offers a recovery route for the text retained in this editor.
            if (_dirty) TrySave();
            string text = _editor.Text;
            _choosingLocation = true;
            _saveTo.IsEnabled = _editor.IsEnabled = false;
            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = Path.GetFileNameWithoutExtension(_store.FilePath),
                };
                picker.FileTypeChoices.Add("文本笔记", new List<string> { ".txt" });
                picker.FileTypeChoices.Add("Markdown 笔记", new List<string> { ".md" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (file == null)
                    return;
                if (string.IsNullOrWhiteSpace(file.Path))
                {
                    ShowError("请选择可以通过本机路径访问的文件。");
                    return;
                }
                if (!_store.TrySaveAs(file.Path, text, _rememberLocation, out string error))
                {
                    ShowError(error);
                    return;
                }
                _dirty = false;
                _error.IsOpen = false;
                _retryLoad.Visibility = Visibility.Collapsed;
                _status.Text = $"{text.Length} 字 · 已保存；后续输入自动写入此位置";
                UpdateLocation();
            }
            catch (Exception ex)
            {
                ShowError("无法选择保存位置：" + ex.Message);
            }
            finally
            {
                _choosingLocation = false;
                _saveTo.IsEnabled = _editor.IsEnabled = _loadSucceeded;
                FocusEditor();
            }
        }
    }
}
