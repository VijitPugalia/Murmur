using System.Text.Json;
using Murmur.Core.Interfaces;
using Murmur.Core.Models;

namespace Murmur.Core.Services;

/// <summary>
/// Persists dictation history to a JSON file in %APPDATA%\Murmur\history.json.
/// Thread-safe and supports isolated testing paths.
/// </summary>
public class HistoryStore : IHistoryStore
{
    private const int MaxItems = 500;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<DictationHistoryItem> _items = new();

    public event EventHandler? HistoryChanged;

    public HistoryStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Murmur");
        _filePath = Path.Combine(dir, "history.json");
        Load();
    }

    public HistoryStore(string customFilePath)
    {
        _filePath = customFilePath ?? throw new ArgumentNullException(nameof(customFilePath));
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var loaded = JsonSerializer.Deserialize<List<DictationHistoryItem>>(json, JsonOptions);
                    if (loaded != null)
                    {
                        _items = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[HistoryStore] Failed to load history: {ex.Message}\n");
                _items = new List<DictationHistoryItem>();
            }
        }
    }

    private void SaveInternal()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_items, JsonOptions);
            var tempPath = _filePath + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[HistoryStore] Failed to save history: {ex.Message}\n");
        }
    }

    public IReadOnlyList<DictationHistoryItem> GetAll()
    {
        lock (_lock)
        {
            return _items.ToList();
        }
    }

    public void Add(DictationHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_lock)
        {
            _items.Insert(0, item);
            if (_items.Count > MaxItems)
            {
                _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            }
            SaveInternal();
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        bool removed;
        lock (_lock)
        {
            var index = _items.FindIndex(x => x.Id == id);
            if (index >= 0)
            {
                _items.RemoveAt(index);
                removed = true;
                SaveInternal();
            }
            else
            {
                removed = false;
            }
        }

        if (removed)
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            SaveInternal();
        }

        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<DictationHistoryItem> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GetAll();
        }

        var normalized = query.Trim();
        lock (_lock)
        {
            return _items.Where(x =>
                (x.CleanTranscript?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.RawTranscript?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.AppName?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.WindowTitle?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
        }
    }
}
