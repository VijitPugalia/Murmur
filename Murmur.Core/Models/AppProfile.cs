namespace Murmur.Core.Models;

/// <summary>
/// Defines specialized prompt and formatting rules for a specific application.
/// </summary>
public class AppProfile : IEquatable<AppProfile>
{
    /// <summary>
    /// Target process or application name (e.g. "code", "devenv", "slack", "outlook").
    /// </summary>
    public string AppPattern { get; set; } = string.Empty;

    /// <summary>
    /// Friendly display name for the profile.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Specific system prompt instructions applied when dictating in this application.
    /// </summary>
    public string CustomPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Whether this application profile is currently active.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public bool Equals(AppProfile? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return AppPattern == other.AppPattern &&
               DisplayName == other.DisplayName &&
               CustomPrompt == other.CustomPrompt &&
               Enabled == other.Enabled;
    }

    public override bool Equals(object? obj) => Equals(obj as AppProfile);

    public override int GetHashCode() => HashCode.Combine(AppPattern, DisplayName, CustomPrompt, Enabled);

    public AppProfile Clone() => new()
    {
        AppPattern = AppPattern,
        DisplayName = DisplayName,
        CustomPrompt = CustomPrompt,
        Enabled = Enabled
    };
}
