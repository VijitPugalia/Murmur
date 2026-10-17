using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Murmur.Core.Interfaces;
using Murmur.Core.Models;

namespace Murmur.App.Windows;

/// <summary>
/// Interaction logic for HistoryWindow.xaml.
/// Displays searchable dictation history and allows 1-click "Paste Again" into target windows.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly IHistoryStore _historyStore;
    private readonly IClipboardManager _clipboardManager;

    public HistoryWindow(IHistoryStore historyStore, IClipboardManager clipboardManager)
    {
        InitializeComponent();
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _clipboardManager = clipboardManager ?? throw new ArgumentNullException(nameof(clipboardManager));

        _historyStore.HistoryChanged += OnHistoryChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshList();
        SearchBox.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _historyStore.HistoryChanged -= OnHistoryChanged;
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RefreshList);
    }

    private void RefreshList()
    {
        var query = SearchBox.Text;
        var items = _historyStore.Search(query);

        var viewModels = items.Select(x => new HistoryItemViewModel(x)).ToList();
        HistoryItemsList.ItemsSource = viewModels;

        ItemCountText.Text = viewModels.Count == 1 
            ? "1 dictation saved" 
            : $"{viewModels.Count} dictations saved";

        EmptyStatePanel.Visibility = viewModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryScrollViewer.Visibility = viewModels.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshList();
    }

    private async void PasteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItemViewModel vm)
        {
            // Close or hide window first so focus returns to the previous active window
            Hide();

            // Give Windows a brief moment to restore focus to the previous active window
            await Task.Delay(120);

            try
            {
                await _clipboardManager.PasteTextAsync(vm.CleanTranscript, preserveClipboard: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to paste: {ex.Message}", "Murmur", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            Close();
        }
    }

    private void CopyItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItemViewModel vm)
        {
            try
            {
                Clipboard.SetText(vm.CleanTranscript);
                btn.Content = "Copied!";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (s, args) =>
                {
                    btn.Content = "Copy";
                    timer.Stop();
                };
                timer.Start();
            }
            catch
            {
                // Clipboard lock protection
            }
        }
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItemViewModel vm)
        {
            _historyStore.Delete(vm.Id);
        }
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to clear your entire dictation history?",
            "Clear History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _historyStore.Clear();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}

public class HistoryItemViewModel
{
    private readonly DictationHistoryItem _item;

    public HistoryItemViewModel(DictationHistoryItem item)
    {
        _item = item;
    }

    public string Id => _item.Id;
    public string CleanTranscript => _item.CleanTranscript;
    public string? AppName => _item.AppName;
    public Visibility AppNameVisibility => string.IsNullOrWhiteSpace(_item.AppName) ? Visibility.Collapsed : Visibility.Visible;

    public string RelativeTime
    {
        get
        {
            var diff = DateTime.Now - _item.Timestamp;
            if (diff.TotalSeconds < 60) return "Just now";
            if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            return _item.Timestamp.ToString("MMM d, h:mm tt");
        }
    }

    public string DurationText => _item.DurationSeconds > 0 
        ? $"({_item.DurationSeconds:0.0}s)" 
        : string.Empty;

    public string RawTranscriptPreview => !string.IsNullOrWhiteSpace(_item.RawTranscript) && _item.RawTranscript != _item.CleanTranscript
        ? $"Original: \"{_item.RawTranscript}\""
        : string.Empty;

    public Visibility RawVisibility => string.IsNullOrEmpty(RawTranscriptPreview) ? Visibility.Collapsed : Visibility.Visible;
}
