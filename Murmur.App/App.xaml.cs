using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Murmur.Core.Interfaces;
using Murmur.Core.Services;
using Murmur.App.Services;
using Murmur.App.Windows;

namespace Murmur.App;

/// <summary>
/// Application entry point for Murmur Windows.
/// Configures dependency injection and initializes core services.
/// Requirements: 1.1, 1.6 - Application lifecycle and tray integration
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    private IApplicationLifecycle? _lifecycle;
    private RecordingIndicator? _recordingIndicator;
    private AppState? _appState;
    private bool _recordingSoundPlayed;

    /// <summary>
    /// Gets the service provider for dependency injection.
    /// </summary>
    public static IServiceProvider Services => ((App)Current)._serviceProvider!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DebugLogger.Log($"\n[App.OnStartup Starting with args: {string.Join(" ", e.Args)}]\n");

        // Global crash protection & logging
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            DebugLogger.Log($"\n[FATAL AppDomain UnhandledException] {ex?.Message}\n{ex?.StackTrace}\n");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            DebugLogger.Log($"\n[DispatcherUnhandledException] {args.Exception.Message}\n{args.Exception.StackTrace}\n");
            args.Handled = true; // Prevent silent crash
            MessageBox.Show($"Murmur encountered an error:\n{args.Exception.Message}", "Murmur", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            DebugLogger.Log($"\n[UnobservedTaskException] {args.Exception.Message}\n{args.Exception.StackTrace}\n");
            args.SetObserved();
        };

        try
        {
            // Configure dependency injection
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            DebugLogger.Log("[OnStartup] Resolving lifecycle and initializing...\n");
            _lifecycle = _serviceProvider.GetRequiredService<IApplicationLifecycle>();
            await _lifecycle.InitializeAsync();
            DebugLogger.Log("[OnStartup] Lifecycle initialized!\n");

            // Create AppState to track pipeline state
            var orchestrator = _serviceProvider.GetRequiredService<IPipelineOrchestrator>();
            var settingsManager = _serviceProvider.GetRequiredService<ISettingsManager>();
            _appState = new AppState(orchestrator, settingsManager);
            
            // Create and wire up the recording indicator
            DebugLogger.Log("[OnStartup] Creating RecordingIndicator...\n");
            _recordingIndicator = new RecordingIndicator();
            var windowHelper = new System.Windows.Interop.WindowInteropHelper(_recordingIndicator);
            windowHelper.EnsureHandle();
            _appState.ShowRecordingIndicator += OnShowRecordingIndicator;
            _appState.HideRecordingIndicator += OnHideRecordingIndicator;
            _appState.ErrorOccurred += OnErrorOccurred;
            
            // Wire up audio level changes
            orchestrator.AudioLevelChanged += (s, args) =>
            {
                Dispatcher.Invoke(() => _recordingIndicator?.UpdateAudioLevel(args.Level));
            };

            var soundEffects = _serviceProvider.GetRequiredService<ISoundEffectService>();
            var historyStore = _serviceProvider.GetRequiredService<IHistoryStore>();
            var contextService = _serviceProvider.GetRequiredService<IContextService>();

            // Wire up pipeline state changes for sound cues
            orchestrator.StateChanged += (s, args) =>
            {
                if (args.NewState == Core.Models.PipelineState.Recording)
                {
                    if (!_recordingSoundPlayed)
                    {
                        soundEffects.PlayRecordingStart();
                    }
                }
                else if (args.NewState == Core.Models.PipelineState.Idle || args.NewState == Core.Models.PipelineState.Error)
                {
                    _recordingSoundPlayed = false;
                }
            };

            // Wire up error events to show in the indicator and play audio cue
            orchestrator.Error += (s, args) =>
            {
                _recordingSoundPlayed = false;
                soundEffects.PlayError();
                Dispatcher.Invoke(() => _recordingIndicator?.ShowError(args.Message, TimeSpan.FromSeconds(4)));
            };
            
            // Wire up completion to dismiss the indicator, play success cue, and record history
            orchestrator.Completed += (s, args) =>
            {
                _recordingSoundPlayed = false;
                Dispatcher.Invoke(() => _recordingIndicator?.Dismiss());
                if (!args.WasCancelled && !string.IsNullOrWhiteSpace(args.Transcript))
                {
                    soundEffects.PlayDictationSuccess();

                    try
                    {
                        var context = contextService.GetActiveContext();
                        historyStore.Add(new Core.Models.DictationHistoryItem
                        {
                            CleanTranscript = args.Transcript,
                            RawTranscript = args.RawTranscript,
                            AppName = context.AppName,
                            WindowTitle = context.WindowTitle
                        });
                        var tm = _serviceProvider.GetRequiredService<ISystemTrayManager>();
                        tm.AddToHistory(args.Transcript, args.RawTranscript);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log($"[App] Failed to save history: {ex.Message}\n");
                    }
                }
            };

            // Wire up hotkey events to pipeline
            var hotkeyManager = _serviceProvider.GetRequiredService<IHotkeyManager>();
            
            // Hold mode: start on press, stop on release
            hotkeyManager.HoldHotkeyPressed += async (s, args) =>
            {
                DebugLogger.Log($"\n[{DateTime.Now:HH:mm:ss}] HoldHotkeyPressed event fired! CurrentState={orchestrator.CurrentState}\n");
                if (!orchestrator.IsActive || orchestrator.CurrentState == Core.Models.PipelineState.Error)
                {
                    // Instant optimistic feedback (<15ms)
                    soundEffects.PlayRecordingStart();
                    _recordingSoundPlayed = true;
                    _recordingIndicator?.ShowInitializing(Core.Models.RecordingMode.Hold);

                    DebugLogger.Log("  Starting recording...\n");
                    var settings = settingsManager.Load();
                    await orchestrator.StartAsync(Core.Models.RecordingMode.Hold, settings.SelectedMicrophoneId);
                }
                else
                {
                    DebugLogger.Log($"  Already active in state {orchestrator.CurrentState}, ignoring\n");
                }
            };

            hotkeyManager.HoldHotkeyReleased += async (s, args) =>
            {
                DebugLogger.Log($"\n[{DateTime.Now:HH:mm:ss}] HoldHotkeyReleased event fired! CurrentState={orchestrator.CurrentState}\n");
                if (orchestrator.IsActive)
                {
                    DebugLogger.Log("  Stopping recording...\n");
                    await orchestrator.StopAsync();
                }
                else
                {
                    DebugLogger.Log("  Not active, ignoring\n");
                }
            };

            // Toggle mode: toggle recording on/off
            hotkeyManager.ToggleHotkeyPressed += async (s, args) =>
            {
                DebugLogger.Log($"\n[{DateTime.Now:HH:mm:ss}] ToggleHotkeyPressed event fired! CurrentState={orchestrator.CurrentState}\n");
                if (orchestrator.IsActive && orchestrator.CurrentState != Core.Models.PipelineState.Error)
                {
                    DebugLogger.Log("  Stopping recording...\n");
                    await orchestrator.StopAsync();
                }
                else
                {
                    // Instant optimistic feedback (<15ms)
                    soundEffects.PlayRecordingStart();
                    _recordingSoundPlayed = true;
                    _recordingIndicator?.ShowInitializing(Core.Models.RecordingMode.Toggle);

                    DebugLogger.Log("  Starting recording...\n");
                    var settings = settingsManager.Load();
                    await orchestrator.StartAsync(Core.Models.RecordingMode.Toggle, settings.SelectedMicrophoneId);
                }
            };

            // Wire up system tray settings request to open settings window
            var trayManager = _serviceProvider.GetRequiredService<ISystemTrayManager>();
            trayManager.SettingsRequested += (s, args) =>
            {
                ShowSettingsWindow();
            };

            // Wire up system tray history request to open history window
            trayManager.HistoryRequested += (s, args) =>
            {
                ShowHistoryWindow();
            };
            
            // Wire up Paste Again from tray menu
            trayManager.PasteAgainRequested += async (s, args) =>
            {
                var clipboardManager = _serviceProvider.GetRequiredService<IClipboardManager>();
                await clipboardManager.PasteLastTranscriptAsync();
            };

            // Check if first launch - open settings if API key not configured
            var settings = settingsManager.Load();
            var credentialStore = _serviceProvider.GetRequiredService<ICredentialStore>();
            var apiKey = credentialStore.GetApiKey("api_key");
            
            if (string.IsNullOrWhiteSpace(apiKey) || e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase) || a.Equals("-s", StringComparison.OrdinalIgnoreCase)))
            {
                ShowSettingsWindow();
            }
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"\n[FATAL OnStartup Exception] {ex.Message}\n{ex.StackTrace}\n");
            MessageBox.Show(
                $"Failed to start Murmur: {ex.Message}",
                "Murmur Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnShowRecordingIndicator(object? sender, RecordingIndicatorEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_recordingIndicator == null) return;
            
            switch (e.State)
            {
                case Core.Models.PipelineState.Initializing:
                    _recordingIndicator.ShowInitializing(e.Mode);
                    break;
                case Core.Models.PipelineState.Recording:
                    _recordingIndicator.ShowRecording(e.Mode);
                    break;
                case Core.Models.PipelineState.Transcribing:
                case Core.Models.PipelineState.PostProcessing:
                case Core.Models.PipelineState.Pasting:
                    _recordingIndicator.ShowProcessing();
                    break;
            }
        });
    }

    private void OnHideRecordingIndicator(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _recordingIndicator?.Dismiss();
        });
    }

    private void OnErrorOccurred(object? sender, ErrorEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _recordingIndicator?.ShowError(e.Message, TimeSpan.FromSeconds(4));
        });
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Core services
        services.AddSingleton<ISettingsManager, SettingsManager>();
        services.AddSingleton<ICredentialStore, CredentialStore>();
        services.AddSingleton<IStartupManager, StartupManager>();
        services.AddSingleton<IContextService, WindowsContextService>();
        services.AddSingleton<IHistoryStore, HistoryStore>();
        services.AddSingleton<ISoundEffectService, SoundEffectService>();

        // Audio services
        services.AddSingleton<IAudioRecorder, AudioRecorder>();

        // API services - use factories to allow settings/API key changes without restart
        services.AddSingleton<ITranscriptionService, TranscriptionServiceFactory>();
        services.AddSingleton<IPostProcessingService, PostProcessingServiceFactory>();

        // Input services
        services.AddSingleton<IClipboardManager, ClipboardManager>();
        services.AddSingleton<IHotkeyManager, WpfHotkeyManager>();

        // UI services
        services.AddSingleton<ISystemTrayManager, SystemTrayManager>();
        services.AddSingleton<IToastNotificationService, ToastNotificationService>();

        // Pipeline orchestrator
        services.AddSingleton<IPipelineOrchestrator, PipelineOrchestrator>();

        // Lifecycle manager
        services.AddSingleton<IApplicationLifecycle, ApplicationLifecycle>();

        // View models
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.RecordingIndicatorViewModel>();
    }

    /// <summary>
    /// Shows the settings window.
    /// </summary>
    public void ShowSettingsWindow()
    {
        DebugLogger.Log("\n[ShowSettingsWindow] Called\n");
        try
        {
            // Check if settings window is already open
            foreach (Window window in Windows)
            {
                if (window is SettingsWindow existingWindow)
                {
                    DebugLogger.Log("[ShowSettingsWindow] Found existing window, activating...\n");
                    if (existingWindow.WindowState == WindowState.Minimized)
                    {
                        existingWindow.WindowState = WindowState.Normal;
                    }
                    existingWindow.Activate();
                    existingWindow.Focus();
                    return;
                }
            }

            DebugLogger.Log("[ShowSettingsWindow] Resolving dependencies...\n");
            var serviceProvider = _serviceProvider!;
            var settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();
            var credentialStore = serviceProvider.GetRequiredService<ICredentialStore>();
            var audioRecorder = serviceProvider.GetRequiredService<IAudioRecorder>();

            DebugLogger.Log("[ShowSettingsWindow] Creating SettingsWindow instance...\n");
            var settingsWindow = new SettingsWindow(settingsManager, credentialStore, audioRecorder);
            settingsWindow.Closed += OnSettingsWindowClosed;
            DebugLogger.Log("[ShowSettingsWindow] Calling settingsWindow.Show()...\n");
            settingsWindow.Show();
            settingsWindow.Activate();
            settingsWindow.Focus();
            DebugLogger.Log("[ShowSettingsWindow] Settings window shown and activated successfully!\n");
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"\n[ShowSettingsWindow Exception] {ex.Message}\n{ex.StackTrace}\n");
            if (ex.InnerException != null)
            {
                DebugLogger.Log($"[ShowSettingsWindow InnerException] {ex.InnerException.Message}\n{ex.InnerException.StackTrace}\n");
            }
        }
    }

    private Windows.HistoryWindow? _historyWindow;

    /// <summary>
    /// Shows the dictation history window.
    /// </summary>
    public void ShowHistoryWindow()
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_historyWindow != null && _historyWindow.IsLoaded)
                {
                    if (_historyWindow.WindowState == WindowState.Minimized)
                        _historyWindow.WindowState = WindowState.Normal;
                    _historyWindow.Activate();
                    _historyWindow.Focus();
                    return;
                }

                var historyStore = _serviceProvider!.GetRequiredService<IHistoryStore>();
                var clipboardManager = _serviceProvider!.GetRequiredService<IClipboardManager>();
                _historyWindow = new Windows.HistoryWindow(historyStore, clipboardManager);
                _historyWindow.Closed += (s, e) => _historyWindow = null;
                _historyWindow.Show();
                _historyWindow.Activate();
                _historyWindow.Focus();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ShowHistoryWindow Exception] {ex.Message}\n");
            }
        });
    }
    
    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        DebugLogger.Log($"\n[{DateTime.Now:HH:mm:ss}] OnSettingsWindowClosed called\n");
        
        if (sender is SettingsWindow window)
        {
            window.Closed -= OnSettingsWindowClosed;
            
            // Re-register hotkeys on the UI thread to ensure ComponentDispatcher works correctly
            Dispatcher.BeginInvoke(() =>
            {
                var hotkeyManager = _serviceProvider?.GetRequiredService<IHotkeyManager>();
                var settingsManager = _serviceProvider?.GetRequiredService<ISettingsManager>();
                if (hotkeyManager != null && settingsManager != null)
                {
                    var settings = settingsManager.Load();
                    DebugLogger.Log($"  Loaded HoldHotkey: {settings.HoldHotkey}\n");
                    DebugLogger.Log($"  Loaded ToggleHotkey: {settings.ToggleHotkey}\n");
                    
                    var config = new Core.Models.HotkeyConfiguration
                    {
                        UseUnifiedHotkey = settings.UseUnifiedHotkey,
                        UnifiedHotkey = settings.UnifiedHotkey,
                        HoldHotkey = settings.HoldHotkey,
                        ToggleHotkey = settings.ToggleHotkey,
                        PasteAgainHotkey = null
                    };
                    
                    var result = hotkeyManager.RegisterHotkeys(config);
                    DebugLogger.Log($"  RegisterHotkeys result: {result}\n");
                    DebugLogger.Log($"  IsActive: {hotkeyManager.IsActive}\n");
                }
                else
                {
                    DebugLogger.Log("  hotkeyManager or settingsManager is null!\n");
                }
            });
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        // Handle Windows shutdown/logout
        _lifecycle?.HandleSessionEnding(e.ReasonSessionEnding == ReasonSessionEnding.Logoff);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        DebugLogger.Log($"\n[App.OnExit] Application exiting with code {e.ApplicationExitCode}\nStack:\n{Environment.StackTrace}\n");
        // Clean up recording indicator
        if (_appState != null)
        {
            _appState.ShowRecordingIndicator -= OnShowRecordingIndicator;
            _appState.HideRecordingIndicator -= OnHideRecordingIndicator;
            _appState.ErrorOccurred -= OnErrorOccurred;
            _appState.Dispose();
        }
        _recordingIndicator?.CloseAndDispose();

        // Perform graceful shutdown
        if (_lifecycle != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _lifecycle.ShutdownAsync(cts.Token);
            }
            catch
            {
                // Best effort shutdown
            }
        }

        // Dispose service provider
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }
}
