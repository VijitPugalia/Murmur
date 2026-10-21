using Murmur.Core.Interfaces;
using Murmur.Core.Models;
using Murmur.Core.Services;

namespace Murmur.App.Services;

/// <summary>
/// Manages application lifecycle including startup initialization and graceful shutdown.
/// Coordinates cleanup of all services and resources when the application exits.
/// Requirements: 11.4, 11.5 - Graceful shutdown handling
/// </summary>
public class ApplicationLifecycle : IApplicationLifecycle
{
    private readonly IPipelineOrchestrator _pipelineOrchestrator;
    private readonly IHotkeyManager _hotkeyManager;
    private readonly ISystemTrayManager _systemTrayManager;
    private readonly ISettingsManager _settingsManager;
    private readonly IStartupManager _startupManager;

    private readonly object _stateLock = new();
    private bool _isInitialized;
    private bool _isShuttingDown;
    private bool _isDisposed;

    /// <inheritdoc/>
    public event EventHandler? ShutdownInitiated;

    /// <inheritdoc/>
    public event EventHandler? ShutdownCompleted;

    /// <inheritdoc/>
    public bool IsShuttingDown
    {
        get
        {
            lock (_stateLock)
            {
                return _isShuttingDown;
            }
        }
    }

    /// <inheritdoc/>
    public bool IsInitialized
    {
        get
        {
            lock (_stateLock)
            {
                return _isInitialized;
            }
        }
    }

    /// <summary>
    /// Creates a new ApplicationLifecycle with the specified dependencies.
    /// </summary>
    public ApplicationLifecycle(
        IPipelineOrchestrator pipelineOrchestrator,
        IHotkeyManager hotkeyManager,
        ISystemTrayManager systemTrayManager,
        ISettingsManager settingsManager,
        IStartupManager startupManager)
    {
        _pipelineOrchestrator = pipelineOrchestrator ?? throw new ArgumentNullException(nameof(pipelineOrchestrator));
        _hotkeyManager = hotkeyManager ?? throw new ArgumentNullException(nameof(hotkeyManager));
        _systemTrayManager = systemTrayManager ?? throw new ArgumentNullException(nameof(systemTrayManager));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _startupManager = startupManager ?? throw new ArgumentNullException(nameof(startupManager));
    }

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        lock (_stateLock)
        {
            if (_isInitialized)
            {
                return;
            }
        }

        try
        {
            DebugLogger.Log("[InitializeAsync] 1. Loading settings...\n");
            var settings = _settingsManager.Load();

            DebugLogger.Log("[InitializeAsync] 2. Initializing system tray...\n");
            _systemTrayManager.Initialize();
            _systemTrayManager.ExitRequested += OnExitRequested;
            _systemTrayManager.SettingsRequested += OnSettingsRequested;

            DebugLogger.Log("[InitializeAsync] 3. Registering hotkeys...\n");
            await RegisterHotkeysAsync(settings);

            DebugLogger.Log("[InitializeAsync] 4. Updating startup registration...\n");
            _startupManager.SetStartupEnabled(settings.StartWithWindows);

            DebugLogger.Log("[InitializeAsync] 5. Subscribing to pipeline events...\n");
            _pipelineOrchestrator.StateChanged += OnPipelineStateChanged;
            _pipelineOrchestrator.Error += OnPipelineError;

            lock (_stateLock)
            {
                _isInitialized = true;
            }
            DebugLogger.Log("[InitializeAsync] Done successfully!\n");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"\n[InitializeAsync Exception] {ex.Message}\n{ex.StackTrace}\n");
            await ShutdownAsync();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_isShuttingDown)
            {
                return;
            }
            _isShuttingDown = true;
        }

        DebugLogger.Log($"\n[ShutdownAsync] Initiated from:\n{Environment.StackTrace}\n");
        try
        {
            // Notify that shutdown is starting
            ShutdownInitiated?.Invoke(this, EventArgs.Empty);

            // Cancel any active pipeline operation
            if (_pipelineOrchestrator.IsActive)
            {
                _pipelineOrchestrator.Cancel();

                // Give it a moment to clean up
                try
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Timeout is fine, continue shutdown
                }
            }

            // Unsubscribe from events
            _pipelineOrchestrator.StateChanged -= OnPipelineStateChanged;
            _pipelineOrchestrator.Error -= OnPipelineError;
            _systemTrayManager.ExitRequested -= OnExitRequested;
            _systemTrayManager.SettingsRequested -= OnSettingsRequested;

            // Unregister all hotkeys
            _hotkeyManager.UnregisterAll();

            // Dispose the system tray manager
            if (_systemTrayManager is IDisposable disposableTray)
            {
                disposableTray.Dispose();
            }

            // Dispose other disposable services
            if (_hotkeyManager is IDisposable disposableHotkey)
            {
                disposableHotkey.Dispose();
            }

            // Notify that shutdown is complete
            ShutdownCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // Best effort shutdown - don't throw
        }
        finally
        {
            lock (_stateLock)
            {
                _isInitialized = false;
            }
        }
    }

    /// <inheritdoc/>
    public void HandleSessionEnding(bool isLoggingOff)
    {
        // Perform synchronous shutdown for session ending events
        // We can't await here as the session may end before async completes

        lock (_stateLock)
        {
            if (_isShuttingDown)
            {
                return;
            }
            _isShuttingDown = true;
        }

        try
        {
            // Cancel any active pipeline operation immediately
            if (_pipelineOrchestrator.IsActive)
            {
                _pipelineOrchestrator.Cancel();
            }

            // Unregister hotkeys
            _hotkeyManager.UnregisterAll();

            // Clean up tray icon
            if (_systemTrayManager is IDisposable disposableTray)
            {
                disposableTray.Dispose();
            }
        }
        catch
        {
            // Best effort - session is ending regardless
        }
    }

    /// <inheritdoc/>
    public void RequestExit()
    {
        // Trigger the exit through the WPF application
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current?.Shutdown();
        });
    }

    /// <summary>
    /// Registers hotkeys based on the current settings.
    /// </summary>
    private Task RegisterHotkeysAsync(AppSettings settings)
    {
        DebugLogger.Log($"\n[{DateTime.Now:HH:mm:ss}] RegisterHotkeysAsync called\n");
        DebugLogger.Log($"  HoldHotkey: {settings.HoldHotkey}\n");
        DebugLogger.Log($"  ToggleHotkey: {settings.ToggleHotkey}\n");
        
        try
        {
            // Build hotkey configuration from settings
            var config = new HotkeyConfiguration
            {
                UseUnifiedHotkey = settings.UseUnifiedHotkey,
                UnifiedHotkey = settings.UnifiedHotkey,
                HoldHotkey = settings.HoldHotkey,
                ToggleHotkey = settings.ToggleHotkey
            };

            // Register all hotkeys
            var result = _hotkeyManager.RegisterHotkeys(config);
            DebugLogger.Log($"  RegisterHotkeys result: {result}\n");
            DebugLogger.Log($"  IsActive: {_hotkeyManager.IsActive}\n");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  Exception: {ex.Message}\n");
            DebugLogger.Log($"  Stack: {ex.StackTrace}\n");
            // Log hotkey registration failures but don't fail initialization
            // User can configure different hotkeys in settings
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles exit requests from the system tray.
    /// </summary>
    private void OnExitRequested(object? sender, EventArgs e)
    {
        RequestExit();
    }

    /// <summary>
    /// Handles settings requests from the system tray.
    /// </summary>
    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        // This would be handled by the main window/app to show settings
        // For now, just raise an event that can be subscribed to
    }

    /// <summary>
    /// Updates the tray icon based on pipeline state changes.
    /// </summary>
    private void OnPipelineStateChanged(object? sender, PipelineStateChangedEventArgs e)
    {
        var status = e.NewState switch
        {
            PipelineState.Idle => AppStatus.Idle,
            PipelineState.Initializing => AppStatus.Recording,
            PipelineState.Recording => AppStatus.Recording,
            PipelineState.Transcribing => AppStatus.Processing,
            PipelineState.PostProcessing => AppStatus.Processing,
            PipelineState.Pasting => AppStatus.Processing,
            PipelineState.Error => AppStatus.Error,
            _ => AppStatus.Idle
        };

        _systemTrayManager.UpdateIcon(status);
        _systemTrayManager.SetRecordingState(status == AppStatus.Recording);
    }

    /// <summary>
    /// Handles pipeline errors.
    /// </summary>
    private void OnPipelineError(object? sender, PipelineErrorEventArgs e)
    {
        _systemTrayManager.ShowBalloonNotification(
            "Murmur Error",
            e.Message,
            NotificationType.Error);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // Perform synchronous shutdown
        HandleSessionEnding(isLoggingOff: false);

        GC.SuppressFinalize(this);
    }
}
