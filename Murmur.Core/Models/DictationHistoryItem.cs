namespace Murmur.Core.Models;

/// <summary>
/// Represents an entry in the persistent dictation history.
/// </summary>
public record DictationHistoryItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string CleanTranscript { get; init; } = string.Empty;
    public string? RawTranscript { get; init; }
    public string? AppName { get; init; }
    public string? WindowTitle { get; init; }
    public double DurationSeconds { get; init; }
    public bool WasTransformed { get; init; }
}
