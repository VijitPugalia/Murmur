using Murmur.Core.Models;

namespace Murmur.Core.Interfaces;

/// <summary>
/// Manages persistence, retrieval, and search of dictation history.
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Gets all history items in reverse chronological order (newest first).
    /// </summary>
    IReadOnlyList<DictationHistoryItem> GetAll();

    /// <summary>
    /// Adds a new item to history and persists to storage.
    /// </summary>
    void Add(DictationHistoryItem item);

    /// <summary>
    /// Deletes a specific item by ID.
    /// </summary>
    bool Delete(string id);

    /// <summary>
    /// Clears all history items.
    /// </summary>
    void Clear();

    /// <summary>
    /// Searches history items matching query in transcript, app name, or window title.
    /// </summary>
    IReadOnlyList<DictationHistoryItem> Search(string query);

    /// <summary>
    /// Fired when history items are added, deleted, or cleared.
    /// </summary>
    event EventHandler? HistoryChanged;
}
