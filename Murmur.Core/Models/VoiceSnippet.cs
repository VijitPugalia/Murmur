namespace Murmur.Core.Models;

/// <summary>
/// Defines a spoken shortcut trigger phrase that automatically expands into text.
/// </summary>
public class VoiceSnippet : IEquatable<VoiceSnippet>
{
    /// <summary>
    /// Spoken phrase that activates the snippet (e.g. "my calendar link", "my address").
    /// </summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// The text that replaces the trigger phrase.
    /// </summary>
    public string Replacement { get; set; } = string.Empty;

    /// <summary>
    /// Whether this snippet expansion is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public bool Equals(VoiceSnippet? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Trigger, other.Trigger, StringComparison.OrdinalIgnoreCase) &&
               Replacement == other.Replacement &&
               Enabled == other.Enabled;
    }

    public override bool Equals(object? obj) => Equals(obj as VoiceSnippet);

    public override int GetHashCode() => HashCode.Combine(Trigger?.ToLowerInvariant(), Replacement, Enabled);

    public VoiceSnippet Clone() => new()
    {
        Trigger = Trigger,
        Replacement = Replacement,
        Enabled = Enabled
    };
}
