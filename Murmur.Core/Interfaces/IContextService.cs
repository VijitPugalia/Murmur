namespace Murmur.Core.Interfaces;

/// <summary>
/// Snapshot of the active foreground window and application context.
/// Matches macOS AppSelectionSnapshot / AppContext.
/// </summary>
public record AppContextSnapshot(
    string? AppName,
    string? ProcessName,
    string? WindowTitle,
    string? SelectedText,
    string ContextSummary);

/// <summary>
/// Service to capture active foreground application context and text selection on Windows.
/// </summary>
public interface IContextService
{
    /// <summary>
    /// Captures the current active foreground application context, window title, and selected text.
    /// </summary>
    AppContextSnapshot GetActiveContext();

    /// <summary>
    /// Captures the current text selection in the foreground window, if any.
    /// </summary>
    string? GetSelectedText();
}
