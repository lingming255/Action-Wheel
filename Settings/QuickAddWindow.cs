using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Action_Wheel.Core;
using Action_Wheel.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Action_Wheel.Settings
{
    /// <summary>A small editor opened directly from the wheel. It never edits a vault's notes.</summary>
    public sealed class QuickAddWindow : Window
    {
        private readonly Grid _root = new();
        private readonly ComboBox _source = new() { Header = "添加什么", HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _url = new() { Header = "网页或笔记链接", PlaceholderText = "粘贴网页地址，也可粘贴 Obsidian 笔记链接" };
        private readonly TextBox _name = new() { Header = "轮盘上显示的名称", PlaceholderText = "留空时使用网站或笔记名称" };
        private readonly ComboBox _vault = new() { Header = "Obsidian 库", HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _search = new() { Header = "查找笔记", PlaceholderText = "输入笔记名称或路径，不必先打开 Obsidian" };
        private readonly ListView _notes = new() { Height = 190, SelectionMode = ListViewSelectionMode.Single };
        private readonly TextBlock _noteStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };
        private readonly ComboBox _destination = new() { Header = "放到哪个位置", PlaceholderText = "选择空位置、分组或要替换的按钮", HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _destinationDescription = new() { TextWrapping = TextWrapping.Wrap };
        private readonly CheckBox _replace = new() { Content = "确认替换上面注明的现有按钮", Visibility = Visibility.Collapsed };
        private readonly InfoBar _status = new() { IsClosable = false, IsOpen = false };
        private readonly Button _save = new() { Content = "添加到轮盘", IsEnabled = false };
        private readonly StackPanel _webPanel = new() { Spacing = 8 };
        private readonly StackPanel _notePanel = new() { Spacing = 8, Visibility = Visibility.Collapsed };
        private readonly TextBlock _boardDescription = new()
        {
            Text = "唤出 Wheel 自带笔记板，随手记录，内容自动保存。",
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource? _noteLoad;
        private IReadOnlyList<QuickAddNote> _allNotes = Array.Empty<QuickAddNote>();
        private QuickAddSession? _session;
        private bool _closed;
        private bool _saving;
        private bool _loaded;
        private WindowPlacementTarget? _placementTarget;

        public event EventHandler? ActionsSaved;

        public QuickAddWindow(IntPtr originalTarget = default, IQuickAddPageSource? pageSource = null, bool preview = false,
            WindowPlacementTarget? placementTarget = null)
        {
            _placementTarget = placementTarget;
            Title = "Action Wheel · 快捷添加";
            AppIcon.ApplyTo(this);
            _source.Items.Add("网页");
            _source.Items.Add("Obsidian 笔记");
            _source.Items.Add("笔记板");
            _source.SelectedIndex = 0;
            _webPanel.Children.Add(_url);
            if (pageSource != null && originalTarget != IntPtr.Zero)
            {
                var readPage = new Button { Content = "读取刚才的网页" };
                readPage.Click += async (_, _) =>
                {
                    try
                    {
                        readPage.IsEnabled = false;
                        var page = await pageSource.TryReadAsync(originalTarget, _lifetime.Token);
                        if (_closed) return;
                        if (page == null)
                            ShowStatus("没有读到网页地址，请粘贴链接。", InfoBarSeverity.Informational);
                        else
                        {
                            _url.Text = page.Url;
                            _name.Text = page.Title;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception) { if (!_closed) ShowStatus("没有读到网页地址，请粘贴链接。", InfoBarSeverity.Informational); }
                    finally { if (!_closed) readPage.IsEnabled = true; }
                };
                _webPanel.Children.Add(readPage);
            }
            _notePanel.Children.Add(_vault);
            _notePanel.Children.Add(_search);
            _notePanel.Children.Add(_notes);
            _notePanel.Children.Add(_noteStatus);

            var content = new StackPanel { Spacing = 14, Padding = new Thickness(24, 20, 24, 20) };
            content.Children.Add(new TextBlock { Text = "快捷添加", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = "选择内容和轮盘位置，添加后立即生效。", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            content.Children.Add(_source);
            content.Children.Add(_webPanel);
            content.Children.Add(_notePanel);
            content.Children.Add(_boardDescription);
            content.Children.Add(_name);
            content.Children.Add(_destination);
            content.Children.Add(_destinationDescription);
            content.Children.Add(_replace);
            content.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            try { _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; } catch { }
            var cancel = new Button { Content = "取消" };
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(_save);
            buttons.Children.Add(cancel);
            content.Children.Add(buttons);
            _root.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            Content = _root;

            _source.SelectionChanged += (_, _) =>
            {
                _webPanel.Visibility = _source.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
                _notePanel.Visibility = _source.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                _boardDescription.Visibility = _source.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                UpdateSaveState();
            };
            _url.TextChanged += (_, _) => UpdateSaveState();
            _notes.SelectionChanged += (_, _) => UpdateSaveState();
            _search.TextChanged += (_, _) => FilterNotes();
            _vault.SelectionChanged += async (_, _) => await LoadNotesAsync();
            _destination.SelectionChanged += (_, _) =>
            {
                var location = _destination.SelectedItem as QuickAddDestination;
                _destinationDescription.Text = location?.Description ?? string.Empty;
                _replace.IsChecked = false;
                _replace.Visibility = location?.ReplacesExisting == true ? Visibility.Visible : Visibility.Collapsed;
                UpdateSaveState();
            };
            _replace.Checked += (_, _) => UpdateSaveState();
            _replace.Unchecked += (_, _) => UpdateSaveState();
            _save.Click += (_, _) => Save();
            _root.KeyDown += (_, args) =>
            {
                if (args.Key == VirtualKey.Escape)
                {
                    args.Handled = true;
                    Close();
                }
            };
            _root.Loaded += async (_, _) =>
            {
                if (_loaded) return;
                _loaded = true;
                WindowSizing.SizeAndCentre(this, _root, 640, 780, "快捷添加", _placementTarget);
                _url.Focus(FocusState.Programmatic);
                if (!preview) await PrefillClipboardAsync();
            };
            Closed += (_, _) =>
            {
                _closed = true;
                _lifetime.Cancel();
                _noteLoad?.Cancel();
                _session?.Dispose();
            };

            try
            {
                _session = new QuickAddSession(preview);
                _destination.ItemsSource = _session.Destinations;
                _destination.SelectedIndex = -1; // Selecting an occupied slot is always explicit.
                if (preview)
                    ShowStatus("预览模式：仅保存到临时副本，不会修改日常轮盘。", InfoBarSeverity.Informational);
            }
            catch (Exception ex) { ShowStatus("无法开始添加：" + ex.Message, InfoBarSeverity.Error); }
            _vault.ItemsSource = QuickAddObsidian.DiscoverVaults();
            if (_vault.Items.Count > 0)
                _vault.SelectedIndex = 0;
            else
                _noteStatus.Text = "尚未找到本机 Obsidian 库。请先在 Obsidian 中打开一次所需的库。";
        }

        public void PlaceOnTarget(WindowPlacementTarget? target)
        {
            if (target == null) return;
            _placementTarget = target;
            if (_loaded) WindowSizing.CentreOnTarget(this, _root, target);
            else WindowSizing.SizeAndCentre(this, _root, 640, 780, "快捷添加", target);
        }

        private async Task PrefillClipboardAsync()
        {
            try
            {
                if (_url.Text.Length != 0 || _source.SelectedIndex != 0) return;
                var clipboard = Clipboard.GetContent();
                if (!clipboard.Contains(StandardDataFormats.Text)) return;
                string text = await clipboard.GetTextAsync();
                if (_closed || _url.Text.Length != 0 || _source.SelectedIndex != 0) return;
                if (!QuickAddActions.TryClipboardLink(text, out string link, out string label)) return;
                _url.Text = link;
                if (_name.Text.Length == 0) _name.Text = label;
            }
            catch (Exception)
            {
                // Clipboard owners may be slow or unavailable. This is a convenience prefill;
                // regular typing and Ctrl+V remain available and clipboard contents stay untouched.
            }
        }

        private async Task LoadNotesAsync()
        {
            _noteLoad?.Cancel();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _noteLoad = cancellation;
            _allNotes = Array.Empty<QuickAddNote>();
            _notes.ItemsSource = _allNotes;
            UpdateSaveState();
            if (_vault.SelectedItem is not QuickAddVault vault)
                return;
            _noteStatus.Text = "正在读取笔记名称…";
            try
            {
                var notes = await Task.Run(() => QuickAddObsidian.ReadNotes(vault, cancellation.Token), cancellation.Token);
                if (_closed || cancellation.IsCancellationRequested) return;
                _allNotes = notes;
                FilterNotes();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed && !cancellation.IsCancellationRequested) _noteStatus.Text = "无法读取这个库：" + ex.Message; }
            finally
            {
                if (ReferenceEquals(_noteLoad, cancellation)) _noteLoad = null;
                cancellation.Dispose();
            }
        }

        private void FilterNotes()
        {
            _notes.ItemsSource = QuickAddObsidian.Search(_allNotes, _search.Text);
            _noteStatus.Text = _allNotes.Count == 0 ? "这个库中没有找到 Markdown 笔记。"
                : $"共 {_allNotes.Count} 篇笔记，最多显示 100 项；可输入名称或路径缩小范围。";
            UpdateSaveState();
        }

        private void UpdateSaveState()
        {
            bool contentReady = _source.SelectedIndex switch
            {
                0 => QuickAddActions.TryLaunchLink(_url.Text, out _, out _),
                1 => _notes.SelectedItem is QuickAddNote,
                2 => true,
                _ => false,
            };
            var destination = _destination.SelectedItem as QuickAddDestination;
            _save.IsEnabled = !_saving && _session != null && contentReady && destination != null
                && (!destination.ReplacesExisting || _replace.IsChecked == true);
        }

        private void Save()
        {
            if (_saving || _session == null || _destination.SelectedItem is not QuickAddDestination destination
                || (destination.ReplacesExisting && _replace.IsChecked != true))
                return;
            string value;
            string suggestedName;
            ActionKind kind = ActionKind.Launch;
            string glyph = "F0AC";
            if (_source.SelectedIndex == 0)
            {
                if (!QuickAddActions.TryLaunchLink(_url.Text, out value, out suggestedName))
                {
                    ShowStatus("请填写有效的 http、https 网页地址或 obsidian://open 笔记链接。", InfoBarSeverity.Warning);
                    return;
                }
            }
            else if (_source.SelectedIndex == 1 && _notes.SelectedItem is QuickAddNote note)
            {
                if (!File.Exists(note.FullPath))
                {
                    ShowStatus("这篇笔记已被移动或删除，请重新选择。", InfoBarSeverity.Warning);
                    return;
                }
                value = QuickAddActions.ObsidianUri(note.FullPath);
                suggestedName = note.Name;
                glyph = "F02D";
            }
            else if (_source.SelectedIndex == 2)
            {
                kind = ActionKind.Function;
                value = WindowsFunctions.NoteBoard;
                suggestedName = "笔记板";
                glyph = "F044";
            }
            else return;

            var addition = new ActionItem
            {
                Label = string.IsNullOrWhiteSpace(_name.Text) ? suggestedName : _name.Text.Trim(),
                Kind = kind,
                Value = value,
                Glyph = glyph,
            };
            _saving = true;
            UpdateSaveState();
            try
            {
                if (!_session.TrySave(destination, addition, out string error))
                {
                    ShowStatus("未添加：" + error, InfoBarSeverity.Error);
                    return;
                }
                ActionsSaved?.Invoke(this, EventArgs.Empty);
                Close();
            }
            catch (Exception ex) { ShowStatus("未能完成添加：" + ex.Message, InfoBarSeverity.Error); }
            finally { _saving = false; if (!_closed) UpdateSaveState(); }
        }

        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            _status.Title = message;
            _status.Severity = severity;
            _status.IsOpen = true;
        }
    }
}
