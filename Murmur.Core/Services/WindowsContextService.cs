using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Murmur.Core.Interfaces;

namespace Murmur.Core.Services;

/// <summary>
/// Captures active foreground window context, process metadata, and selected text on Windows.
/// Mirrors macOS AppContextService functionality.
/// </summary>
public sealed class WindowsContextService : IContextService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <inheritdoc />
    public AppContextSnapshot GetActiveContext()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return new AppContextSnapshot(null, null, null, null, string.Empty);
            }

            // Window title
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            var windowTitle = sb.ToString().Trim();

            // Process info
            GetWindowThreadProcessId(hWnd, out var processId);
            string? processName = null;
            string? appName = null;

            if (processId > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)processId);
                    processName = proc.ProcessName;
                    appName = GetFriendlyAppName(proc);
                }
                catch
                {
                    // Access denied or process exited
                }
            }

            var selectedText = GetSelectedText();

            // Build human/LLM-readable summary
            var summaryParts = new List<string>();
            if (!string.IsNullOrEmpty(appName))
            {
                summaryParts.Add($"App: {appName}");
            }
            else if (!string.IsNullOrEmpty(processName))
            {
                summaryParts.Add($"App: {processName}");
            }

            if (!string.IsNullOrEmpty(windowTitle))
            {
                summaryParts.Add($"Window: {windowTitle}");
            }

            if (!string.IsNullOrEmpty(selectedText))
            {
                var preview = selectedText.Length > 120 
                    ? selectedText.Substring(0, 117) + "..." 
                    : selectedText;
                summaryParts.Add($"Selected: \"{preview}\"");
            }

            var contextSummary = string.Join(", ", summaryParts);
            DebugLogger.Log($"[ContextService] Active context: {contextSummary}\n");

            return new AppContextSnapshot(
                appName ?? processName,
                processName,
                windowTitle,
                selectedText,
                contextSummary);
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"[ContextService] Error collecting context: {ex.Message}\n");
            return new AppContextSnapshot(null, null, null, null, string.Empty);
        }
    }

    /// <inheritdoc />
    public string? GetSelectedText()
    {
        try
        {
            // Use Windows UI Automation COM or standard TextPattern if available
            // Fast STA check or fall back safely
            return null; // Selection capture can be enhanced with UI Automation TextPattern
        }
        catch
        {
            return null;
        }
    }

    private static string GetFriendlyAppName(Process proc)
    {
        try
        {
            var desc = proc.MainModule?.FileVersionInfo?.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc))
            {
                return desc.Trim();
            }
        }
        catch
        {
            // MainModule can throw for 32/64-bit permission boundaries
        }

        return proc.ProcessName switch
        {
            "code" => "Visual Studio Code",
            "devenv" => "Visual Studio",
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" => "Mozilla Firefox",
            "slack" => "Slack",
            "teams" or "ms-teams" => "Microsoft Teams",
            "outlook" => "Microsoft Outlook",
            "winword" => "Microsoft Word",
            "excel" => "Microsoft Excel",
            "powerpnt" => "Microsoft PowerPoint",
            "notepad" => "Notepad",
            "WindowsTerminal" => "Windows Terminal",
            "cmd" => "Command Prompt",
            "powershell" or "pwsh" => "PowerShell",
            "discord" => "Discord",
            "spotify" => "Spotify",
            _ => proc.ProcessName
        };
    }
}
