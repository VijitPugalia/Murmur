using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Murmur.Core.Interfaces;
using InputSimulatorStandard;
using InputSimulatorStandard.Native;

namespace Murmur.Core.Services;

/// <summary>
/// Manages clipboard operations using Windows Clipboard APIs (User32.dll P/Invoke).
/// Provides async clipboard access with retry logic, paste simulation, clipboard preservation,
/// private clipboard isolation (excluding pastes from Win+V history), and rich dual-format HTML.
/// </summary>
public sealed class ClipboardManager : IClipboardManager, IDisposable
{
    #region Native Methods and Constants

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const int MaxRetryAttempts = 10;
    private const int InitialRetryDelayMs = 10;
    private const int MaxRetryDelayMs = 100;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);

    #endregion

    private readonly IInputSimulator _inputSimulator;
    private readonly object _lock = new();
    private string? _lastTranscript;
    private bool _disposed;

    /// <summary>
    /// Gets or sets the delay after pasting before restoring the original clipboard contents.
    /// Default is 1 second.
    /// </summary>
    public TimeSpan ClipboardRestoreDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Creates a new ClipboardManager with the default InputSimulator.
    /// </summary>
    public ClipboardManager()
        : this(new InputSimulator())
    {
    }

    /// <summary>
    /// Creates a new ClipboardManager with a custom InputSimulator (for testing).
    /// </summary>
    public ClipboardManager(IInputSimulator inputSimulator)
    {
        _inputSimulator = inputSimulator ?? throw new ArgumentNullException(nameof(inputSimulator));
    }

    /// <inheritdoc />
    public Task PasteTextAsync(string text, bool preserveClipboard = true, CancellationToken cancellationToken = default)
    {
        return PasteTextAsync(text, preserveClipboard, html: null, excludeFromHistory: true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PasteTextAsync(string text, bool preserveClipboard, string? html, bool excludeFromHistory = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Store as last transcript for "Paste Again"
        lock (_lock)
        {
            _lastTranscript = text;
        }

        string? originalClipboardContent = null;

        try
        {
            // Save original clipboard content if preservation is enabled
            if (preserveClipboard)
            {
                originalClipboardContent = await GetTextAsync(cancellationToken);
            }

            // Set the transcript on the clipboard with HTML & private exclusion
            await SetTextAsync(text, html, excludeFromHistory, cancellationToken);

            // Small delay to ensure clipboard is ready
            await Task.Delay(50, cancellationToken);

            // Simulate Ctrl+V to paste
            SimulatePaste();

            // Restore original clipboard content after delay if preservation is enabled
            if (preserveClipboard && originalClipboardContent != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(ClipboardRestoreDelay, CancellationToken.None);
                        await SetTextAsync(originalClipboardContent, html: null, excludeFromHistory: false, CancellationToken.None);
                    }
                    catch
                    {
                        // Silently ignore restore failures
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to paste text: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task PasteLastTranscriptAsync(CancellationToken cancellationToken = default)
    {
        string? lastTranscript;
        lock (_lock)
        {
            lastTranscript = _lastTranscript;
        }

        if (string.IsNullOrEmpty(lastTranscript))
        {
            return;
        }

        // For "Paste Again", we don't preserve clipboard
        await SetTextAsync(lastTranscript, html: null, excludeFromHistory: true, cancellationToken);
        await Task.Delay(50, cancellationToken);
        SimulatePaste();
    }

    /// <inheritdoc />
    public string? GetLastTranscript()
    {
        lock (_lock)
        {
            return _lastTranscript;
        }
    }

    /// <inheritdoc />
    public Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        return RunOnStaThreadAsync(() => GetTextFromClipboardWithRetry(cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        return SetTextAsync(text, html: null, excludeFromHistory: true, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetTextAsync(string text, string? html, bool excludeFromHistory = true, CancellationToken cancellationToken = default)
    {
        return RunOnStaThreadAsync(() =>
        {
            SetTextToClipboardWithRetry(text, html, excludeFromHistory, cancellationToken);
            return (object?)null;
        }, cancellationToken);
    }

    /// <summary>
    /// Simulates a Ctrl+V keystroke to paste from the clipboard.
    /// </summary>
    private void SimulatePaste()
    {
        _inputSimulator.Keyboard.ModifiedKeyStroke(
            VirtualKeyCode.CONTROL,
            VirtualKeyCode.VK_V);
    }

    /// <summary>
    /// Gets text from the clipboard with retry logic.
    /// </summary>
    private string? GetTextFromClipboardWithRetry(CancellationToken cancellationToken)
    {
        var delay = InitialRetryDelayMs;

        for (var attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return GetTextFromClipboard();
            }
            catch (ClipboardLockedException)
            {
                if (attempt == MaxRetryAttempts - 1)
                {
                    throw;
                }

                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, MaxRetryDelayMs);
            }
        }

        return null;
    }

    /// <summary>
    /// Sets text to the clipboard with retry logic.
    /// </summary>
    private void SetTextToClipboardWithRetry(string text, string? html, bool excludeFromHistory, CancellationToken cancellationToken)
    {
        var delay = InitialRetryDelayMs;

        for (var attempt = 0; attempt < MaxRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                SetTextToClipboard(text, html, excludeFromHistory);
                return;
            }
            catch (ClipboardLockedException)
            {
                if (attempt == MaxRetryAttempts - 1)
                {
                    throw;
                }

                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, MaxRetryDelayMs);
            }
        }
    }

    /// <summary>
    /// Gets Unicode text from the clipboard using Windows APIs.
    /// </summary>
    private static string? GetTextFromClipboard()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
        {
            return null;
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            throw new ClipboardLockedException("Failed to open clipboard for reading.");
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Sets Unicode text, optional HTML, and private clipboard exclusion flags.
    /// </summary>
    private static void SetTextToClipboard(string text, string? html, bool excludeFromHistory)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            throw new ClipboardLockedException("Failed to open clipboard for writing.");
        }

        try
        {
            if (!EmptyClipboard())
            {
                throw new InvalidOperationException("Failed to empty clipboard.");
            }

            // 1. Unicode Plain Text (CF_UNICODETEXT)
            SetClipboardUnicodeText(text);

            // 2. Rich HTML Format (if explicitly provided, or auto-converted from Markdown)
            string? effectiveHtml = html;
            if (string.IsNullOrEmpty(effectiveHtml) && HasMarkdown(text))
            {
                effectiveHtml = MarkdownToHtml(text);
            }

            if (!string.IsNullOrEmpty(effectiveHtml))
            {
                SetClipboardHtml(effectiveHtml);
            }

            // 3. Exclude from Windows Clipboard History (Win + V) and Cloud Clipboard
            if (excludeFromHistory)
            {
                SetClipboardDwordFlag("CanIncludeInClipboardHistory", 0);
                SetClipboardDwordFlag("CanUploadToCloudClipboard", 0);
                SetClipboardDwordFlag("ExcludeClipboardContentFromMonitorProcessing", 0);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SetClipboardUnicodeText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var size = (UIntPtr)bytes.Length;

        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, size);
        if (hGlobal == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to allocate global memory for clipboard.");
        }

        var pointer = GlobalLock(hGlobal);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            throw new InvalidOperationException("Failed to lock global memory.");
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            throw new InvalidOperationException("Failed to set clipboard data.");
        }
    }

    private static void SetClipboardHtml(string htmlFragment)
    {
        var format = RegisterClipboardFormat("HTML Format");
        if (format == 0) return;

        var envelope = BuildCfHtmlEnvelope(htmlFragment);
        var bytes = Encoding.UTF8.GetBytes(envelope + '\0');

        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (hGlobal == IntPtr.Zero) return;

        var pointer = GlobalLock(hGlobal);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return;
        }

        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
        }
    }

    private static void SetClipboardDwordFlag(string formatName, uint value)
    {
        var format = RegisterClipboardFormat(formatName);
        if (format == 0) return;

        var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)sizeof(uint));
        if (hGlobal == IntPtr.Zero) return;

        var pointer = GlobalLock(hGlobal);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return;
        }

        try
        {
            Marshal.WriteInt32(pointer, (int)value);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
        }
    }

    #region HTML and Markdown Formatting Helpers

    /// <summary>
    /// Builds the standard Microsoft CF_HTML envelope with byte-accurate offsets.
    /// </summary>
    public static string BuildCfHtmlEnvelope(string htmlFragment)
    {
        const string headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:{0:D8}\r\n" +
            "EndHTML:{1:D8}\r\n" +
            "StartFragment:{2:D8}\r\n" +
            "EndFragment:{3:D8}\r\n";

        const string htmlPrefix = "<!DOCTYPE html><html><body><!--StartFragment-->";
        const string htmlSuffix = "<!--EndFragment--></body></html>";

        string dummyHeader = string.Format(headerTemplate, 0, 0, 0, 0);
        int startHtml = Encoding.UTF8.GetByteCount(dummyHeader);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(htmlPrefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(htmlFragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(htmlSuffix);

        string header = string.Format(headerTemplate, startHtml, endHtml, startFragment, endFragment);
        return header + htmlPrefix + htmlFragment + htmlSuffix;
    }

    /// <summary>
    /// Checks whether text contains Markdown elements (bold, italic, inline code, or lists).
    /// </summary>
    public static bool HasMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("**") ||
               text.Contains('`') ||
               text.Contains("\n- ") || text.StartsWith("- ") ||
               text.Contains("\n* ") || text.StartsWith("* ");
    }

    /// <summary>
    /// Converts a basic Markdown string to an HTML fragment.
    /// </summary>
    public static string MarkdownToHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        bool inList = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                if (!inList)
                {
                    sb.Append("<ul>");
                    inList = true;
                }
                var itemContent = ProcessInlineMarkdown(trimmed.Substring(2));
                sb.Append("<li>").Append(itemContent).Append("</li>");
            }
            else
            {
                if (inList)
                {
                    sb.Append("</ul>");
                    inList = false;
                }
                if (!string.IsNullOrEmpty(trimmed))
                {
                    sb.Append("<p>").Append(ProcessInlineMarkdown(trimmed)).Append("</p>");
                }
            }
        }

        if (inList)
        {
            sb.Append("</ul>");
        }

        return sb.ToString();
    }

    private static string ProcessInlineMarkdown(string text)
    {
        // 1. Basic HTML Escape
        var s = System.Net.WebUtility.HtmlEncode(text);

        // 2. Bold: **text**
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<b>$1</b>");

        // 3. Italic: *text*
        s = Regex.Replace(s, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "<i>$1</i>");

        // 4. Code: `text`
        s = Regex.Replace(s, @"`(.+?)`", "<code>$1</code>");

        return s;
    }

    #endregion

    /// <summary>
    /// Runs an action on an STA thread. Required for clipboard operations in Windows.
    /// </summary>
    private static Task<T?> RunOnStaThreadAsync<T>(Func<T?> action, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T?>();

        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = action();
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        return tcs.Task;
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Exception thrown when the clipboard is locked by another application.
/// </summary>
public class ClipboardLockedException : InvalidOperationException
{
    public ClipboardLockedException(string message)
        : base(message)
    {
    }

    public ClipboardLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
