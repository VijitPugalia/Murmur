namespace Murmur.Core.Models;

/// <summary>
/// Configuration for all application hotkeys.
/// </summary>
public class HotkeyConfiguration
{
    /// <summary>
    /// Whether to use unified hotkey mode (one hotkey for both hold and double-tap toggle).
    /// </summary>
    public bool UseUnifiedHotkey { get; set; } = false;

    /// <summary>
    /// Hotkey binding for unified mode (hold to talk, double-tap to toggle). Default: Ctrl + Windows.
    /// </summary>
    public HotkeyBinding UnifiedHotkey { get; set; } = new()
    {
        Modifiers = ModifierKeys.Ctrl | ModifierKeys.Win,
        Key = VirtualKey.None
    };

    /// <summary>
    /// Hotkey binding for hold-to-talk mode.
    /// </summary>
    public HotkeyBinding HoldHotkey { get; set; } = new()
    {
        Modifiers = ModifierKeys.Ctrl | ModifierKeys.Shift,
        Key = VirtualKey.Space
    };

    /// <summary>
    /// Hotkey binding for toggle mode.
    /// </summary>
    public HotkeyBinding ToggleHotkey { get; set; } = new()
    {
        Modifiers = ModifierKeys.Ctrl | ModifierKeys.Alt,
        Key = VirtualKey.Space
    };

    /// <summary>
    /// Optional hotkey binding for "Paste Again" functionality.
    /// </summary>
    public HotkeyBinding? PasteAgainHotkey { get; set; }
}
