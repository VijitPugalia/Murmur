using System.Text.RegularExpressions;
using Murmur.Core.Interfaces;
using Murmur.Core.Models;
using InputSimulatorStandard;
using InputSimulatorStandard.Native;
using NAudio.CoreAudioApi;

namespace Murmur.Core.Services;

/// <summary>
/// Orchestrates the dictation pipeline: recording → transcription → post-processing → pasting.
/// Implements a state machine that coordinates between all pipeline services.
/// </summary>
public class PipelineOrchestrator : IPipelineOrchestrator, IDisposable
{
    private readonly IAudioRecorder _audioRecorder;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IPostProcessingService _postProcessingService;
    private readonly IClipboardManager _clipboardManager;
    private readonly ISettingsManager _settingsManager;
    private readonly IContextService _contextService;

    private readonly object _stateLock = new();
    private PipelineState _currentState = PipelineState.Idle;
    private RecordingMode? _activeRecordingMode;
    private CancellationTokenSource? _pipelineCts;
    private string? _currentAudioFilePath;
    private string? _lastTranscript;
    private string? _rawTranscript;
    private TaskCompletionSource<bool>? _initializationTcs;
    private AppContextSnapshot? _activeContext;
    private MMDevice? _mutedDevice;
    private bool _didMuteAudio;
    private bool _disposed;

    private static readonly Regex TrailingPressEnterRegex = new(
        @"(?i)[\s,\.]+(?:press|hit)\s+enter[\s\.]*$", 
        RegexOptions.Compiled);

    /// <inheritdoc />
    public event EventHandler<PipelineStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<PipelineCompletedEventArgs>? Completed;

    /// <inheritdoc />
    public event EventHandler<PipelineErrorEventArgs>? Error;

    /// <inheritdoc />
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

    /// <inheritdoc />
    public PipelineState CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    /// <inheritdoc />
    public bool IsActive => CurrentState != PipelineState.Idle;

    /// <inheritdoc />
    public RecordingMode? ActiveRecordingMode
    {
        get
        {
            lock (_stateLock)
            {
                return _activeRecordingMode;
            }
        }
    }

    /// <inheritdoc />
    public string? LastTranscript => _lastTranscript;

    /// <summary>
    /// Creates a new PipelineOrchestrator with the specified dependencies.
    /// </summary>
    public PipelineOrchestrator(
        IAudioRecorder audioRecorder,
        ITranscriptionService transcriptionService,
        IPostProcessingService postProcessingService,
        IClipboardManager clipboardManager,
        ISettingsManager settingsManager,
        IContextService? contextService = null)
    {
        _audioRecorder = audioRecorder ?? throw new ArgumentNullException(nameof(audioRecorder));
        _transcriptionService = transcriptionService ?? throw new ArgumentNullException(nameof(transcriptionService));
        _postProcessingService = postProcessingService ?? throw new ArgumentNullException(nameof(postProcessingService));
        _clipboardManager = clipboardManager ?? throw new ArgumentNullException(nameof(clipboardManager));
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _contextService = contextService ?? new WindowsContextService();

        // Subscribe to audio level changes for UI feedback
        _audioRecorder.AudioLevelChanged += OnAudioLevelChanged;
    }

    /// <inheritdoc />
    public async Task StartAsync(RecordingMode mode, string? deviceId = null)
    {
        DebugLogger.Log($"\n[{DateTime.Now:HH:mm:ss}] StartAsync called, mode={mode}, deviceId={deviceId}\n");
        
        lock (_stateLock)
        {
            if (_currentState == PipelineState.Error)
            {
                DebugLogger.Log($"  Recovering from Error state, resetting to Idle before start\n");
                _currentState = PipelineState.Idle;
            }
            else if (_currentState != PipelineState.Idle)
            {
                DebugLogger.Log($"  Already in state {_currentState}, throwing\n");
                throw new InvalidOperationException(
                    $"Cannot start pipeline: already in state {_currentState}");
            }
            _initializationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Create a new cancellation token for this pipeline run
        _pipelineCts = new CancellationTokenSource();
        _activeRecordingMode = mode;
        _currentAudioFilePath = null;
        _rawTranscript = null;

        try
        {
            // Transition to Initializing
            DebugLogger.Log("  Transitioning to Initializing\n");
            SetState(PipelineState.Initializing);

            var settings = _settingsManager.Load();
            HandleAudioMutingStart(settings);
            _activeContext = _contextService.GetActiveContext();

            // Start recording
            DebugLogger.Log("  Starting audio recording...\n");
            await _audioRecorder.StartRecordingAsync(deviceId);

            // Transition to Recording
            DebugLogger.Log("  Transitioning to Recording\n");
            SetState(PipelineState.Recording);
            _initializationTcs?.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            DebugLogger.Log("  Cancelled during initialization\n");
            _initializationTcs?.TrySetCanceled();
            HandleCancellation();
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  Exception: {ex.Message}\n");
            _initializationTcs?.TrySetException(ex);
            HandleError(PipelineState.Initializing, "Failed to start recording", ex);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        var currentState = CurrentState;
        DebugLogger.Log($"\n[{DateTime.Now:HH:mm:ss}] StopAsync called, currentState={currentState}\n");
        
        // Handle hold-mode race condition: if Stop is called while still initializing audio device
        if (currentState == PipelineState.Initializing)
        {
            DebugLogger.Log("  StopAsync called during Initializing, waiting for initialization to complete...\n");
            try
            {
                if (_initializationTcs != null)
                {
                    await Task.WhenAny(_initializationTcs.Task, Task.Delay(1000));
                }
            }
            catch
            {
            }
            currentState = CurrentState;
            DebugLogger.Log($"  After waiting, currentState={currentState}\n");
        }

        if (currentState != PipelineState.Recording)
        {
            DebugLogger.Log($"  Not recording (currentState={currentState}), ensuring reset to Idle\n");
            if (currentState == PipelineState.Initializing || currentState == PipelineState.Error)
            {
                HandleCancellation();
            }
            return;
        }

        var ct = _pipelineCts?.Token ?? CancellationToken.None;

        try
        {
            // Stop recording and get the audio file path
            DebugLogger.Log("  Stopping audio recording...\n");
            _currentAudioFilePath = await _audioRecorder.StopRecordingAsync();
            DebugLogger.Log($"  Audio file: {_currentAudioFilePath}\n");

            if (string.IsNullOrEmpty(_currentAudioFilePath))
            {
                DebugLogger.Log("  No audio file, completing with empty\n");
                HandleCompletion(string.Empty, wasCancelled: false);
                return;
            }

            // Check for cancellation
            ct.ThrowIfCancellationRequested();

            // Transition to Transcribing
            DebugLogger.Log("  Transitioning to Transcribing\n");
            SetState(PipelineState.Transcribing);

            // Transcribe the audio
            DebugLogger.Log("  Starting transcription...\n");
            var transcriptionResult = await _transcriptionService.TranscribeAsync(_currentAudioFilePath, ct);
            DebugLogger.Log($"  Transcription result: success={transcriptionResult.Success}\n");

            if (!transcriptionResult.Success)
            {
                DebugLogger.Log($"  Transcription failed: {transcriptionResult.Error?.Message}\n");
                HandleError(
                    PipelineState.Transcribing,
                    transcriptionResult.Error?.Message ?? "Transcription failed");
                return;
            }

            _rawTranscript = transcriptionResult.Transcript;
            DebugLogger.Log($"  Raw transcript: {_rawTranscript}\n");

            // Check for empty transcript (silence/noise)
            if (string.IsNullOrWhiteSpace(_rawTranscript))
            {
                DebugLogger.Log("  Empty transcript (silence/noise)\n");
                HandleCompletion(string.Empty, wasCancelled: false);
                return;
            }

            ct.ThrowIfCancellationRequested();

            // Transition to PostProcessing
            DebugLogger.Log("  Transitioning to PostProcessing\n");
            SetState(PipelineState.PostProcessing);

            // Load settings for post-processing
            var settings = _settingsManager.Load();
            var vocabularyLines = ParseVocabulary(settings.CustomVocabulary);
            string finalTranscript;

            if (settings.PreserveExactWording)
            {
                DebugLogger.Log("  PreserveExactWording is enabled - using raw transcript\n");
                finalTranscript = _rawTranscript.Trim();
            }
            else
            {
                var contextSummary = _activeContext?.ContextSummary ?? string.Empty;
                var selectedText = _activeContext?.SelectedText;
                var isEditMode = settings.EnableEditMode && !string.IsNullOrWhiteSpace(selectedText);

                var outputLang = string.IsNullOrWhiteSpace(settings.OutputLanguage) || 
                                 settings.OutputLanguage.Equals("Same as spoken", StringComparison.OrdinalIgnoreCase)
                    ? null 
                    : settings.OutputLanguage;

                PostProcessingResult postProcessingResult;
                if (isEditMode)
                {
                    DebugLogger.Log("  Executing Edit Mode (Command Transform on selected text)...\n");
                    postProcessingResult = await _postProcessingService.ProcessCommandModeAsync(
                        voiceCommand: _rawTranscript,
                        selectedText: selectedText!,
                        contextSummary: contextSummary,
                        customVocabulary: vocabularyLines,
                        outputLanguage: outputLang,
                        cancellationToken: ct);
                }
                else
                {
                    DebugLogger.Log("  Starting post-processing...\n");
                    postProcessingResult = await _postProcessingService.ProcessAsync(
                        _rawTranscript,
                        contextSummary: contextSummary,
                        customVocabulary: vocabularyLines,
                        customSystemPrompt: string.IsNullOrWhiteSpace(settings.CustomPrompt) ? null : settings.CustomPrompt,
                        outputLanguage: outputLang,
                        cancellationToken: ct);
                }

                if (!postProcessingResult.Success)
                {
                    DebugLogger.Log("  Post-processing failed, using raw transcript\n");
                    finalTranscript = _rawTranscript.Trim();
                }
                else if (string.IsNullOrWhiteSpace(postProcessingResult.CleanedTranscript))
                {
                    DebugLogger.Log("  Post-processing returned empty (noise)\n");
                    HandleCompletion(string.Empty, wasCancelled: false);
                    return;
                }
                else
                {
                    finalTranscript = postProcessingResult.CleanedTranscript!;
                }
            }

            // Check for trailing spoken "press enter" / "hit enter"
            bool shouldPressEnter = settings.PressEnterAfterPaste;
            if (TrailingPressEnterRegex.IsMatch(finalTranscript))
            {
                finalTranscript = TrailingPressEnterRegex.Replace(finalTranscript, "").TrimEnd();
                shouldPressEnter = true;
                DebugLogger.Log("  Detected trailing 'press enter' phrase in transcript\n");
            }

            DebugLogger.Log($"  Final transcript: {finalTranscript}\n");

            ct.ThrowIfCancellationRequested();

            // Transition to Pasting
            DebugLogger.Log("  Transitioning to Pasting\n");
            SetState(PipelineState.Pasting);

            // Paste the transcript
            DebugLogger.Log("  Pasting text...\n");
            await _clipboardManager.PasteTextAsync(
                finalTranscript,
                preserveClipboard: settings.PreserveClipboard,
                cancellationToken: ct);

            // If Enter requested, simulate Enter key press
            if (shouldPressEnter)
            {
                DebugLogger.Log("  Simulating Enter key press after paste\n");
                await Task.Delay(50, ct);
                try
                {
                    new InputSimulator().Keyboard.KeyPress(VirtualKeyCode.RETURN);
                }
                catch
                {
                }
            }

            // Store for "Paste Again" functionality
            _lastTranscript = finalTranscript;

            // Success!
            DebugLogger.Log("  Pipeline completed successfully!\n");
            HandleCompletion(finalTranscript, wasCancelled: false);
        }
        catch (OperationCanceledException)
        {
            DebugLogger.Log("  Pipeline was cancelled\n");
            HandleCancellation();
        }
        catch (Exception ex)
        {
            DebugLogger.Log($"  Exception: {ex.Message}\n");
            DebugLogger.Log($"  Stack trace: {ex.StackTrace}\n");
            HandleError(CurrentState, ex.Message, ex);
        }
        finally
        {
            CleanupAudioFile();
        }
    }

    /// <inheritdoc />
    public void Cancel()
    {
        var currentState = CurrentState;
        if (currentState == PipelineState.Idle)
        {
            return;
        }

        // Signal cancellation
        _pipelineCts?.Cancel();

        // If we're recording, cancel the recording
        if (currentState == PipelineState.Recording || currentState == PipelineState.Initializing)
        {
            _audioRecorder.CancelRecording();
        }

        HandleCancellation();
    }

    /// <summary>
    /// Sets the pipeline state and fires the StateChanged event.
    /// </summary>
    private void SetState(PipelineState newState)
    {
        PipelineState previousState;
        lock (_stateLock)
        {
            previousState = _currentState;
            _currentState = newState;
        }

        if (previousState != newState)
        {
            StateChanged?.Invoke(this, new PipelineStateChangedEventArgs(previousState, newState));
        }
    }

    /// <summary>
    /// Handles audio output muting during recording.
    /// </summary>
    private void HandleAudioMutingStart(AppSettings settings)
    {
        if (!settings.MuteAudioDuringDictation) return;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _mutedDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_mutedDevice != null && !_mutedDevice.AudioEndpointVolume.Mute)
            {
                _mutedDevice.AudioEndpointVolume.Mute = true;
                _didMuteAudio = true;
                DebugLogger.Log("  Muted system audio playback for dictation\n");
            }
        }
        catch
        {
            // CoreAudio muting is best-effort
        }
    }

    /// <summary>
    /// Restores audio output volume after recording completes or cancels.
    /// </summary>
    private void HandleAudioMutingEnd()
    {
        if (_didMuteAudio && _mutedDevice != null)
        {
            try
            {
                _mutedDevice.AudioEndpointVolume.Mute = false;
                DebugLogger.Log("  Unmuted system audio playback\n");
            }
            catch
            {
            }
            finally
            {
                _didMuteAudio = false;
                _mutedDevice = null;
            }
        }
    }

    /// <summary>
    /// Handles successful pipeline completion.
    /// </summary>
    private void HandleCompletion(string transcript, bool wasCancelled)
    {
        CleanupAudioFile();
        HandleAudioMutingEnd();

        lock (_stateLock)
        {
            _activeRecordingMode = null;
            _currentState = PipelineState.Idle;
        }

        // Fire state changed to Idle
        StateChanged?.Invoke(this, new PipelineStateChangedEventArgs(_currentState, PipelineState.Idle));

        // Fire completion event
        Completed?.Invoke(this, new PipelineCompletedEventArgs(transcript, wasCancelled, _rawTranscript));

        // Dispose the CTS
        _pipelineCts?.Dispose();
        _pipelineCts = null;
    }

    /// <summary>
    /// Handles pipeline cancellation.
    /// </summary>
    private void HandleCancellation()
    {
        CleanupAudioFile();
        HandleAudioMutingEnd();

        var previousState = CurrentState;
        lock (_stateLock)
        {
            _activeRecordingMode = null;
            _currentState = PipelineState.Idle;
        }

        // Fire state changed to Idle
        StateChanged?.Invoke(this, new PipelineStateChangedEventArgs(previousState, PipelineState.Idle));

        // Fire completion event with cancelled flag
        Completed?.Invoke(this, new PipelineCompletedEventArgs(string.Empty, wasCancelled: true, _rawTranscript));

        // Dispose the CTS
        _pipelineCts?.Dispose();
        _pipelineCts = null;
    }

    /// <summary>
    /// Handles pipeline errors.
    /// </summary>
    private void HandleError(PipelineState stateAtError, string message, Exception? exception = null)
    {
        CleanupAudioFile();
        HandleAudioMutingEnd();

        var previousState = CurrentState;
        lock (_stateLock)
        {
            _activeRecordingMode = null;
            _currentState = PipelineState.Error;
        }

        // Fire state changed to Error
        StateChanged?.Invoke(this, new PipelineStateChangedEventArgs(previousState, PipelineState.Error));

        // Fire error event
        Error?.Invoke(this, new PipelineErrorEventArgs(stateAtError, message, exception));

        // Transition back to Idle after error
        lock (_stateLock)
        {
            _currentState = PipelineState.Idle;
        }
        StateChanged?.Invoke(this, new PipelineStateChangedEventArgs(PipelineState.Error, PipelineState.Idle));

        // Dispose the CTS
        _pipelineCts?.Dispose();
        _pipelineCts = null;
    }

    /// <summary>
    /// Cleans up the temporary audio file.
    /// </summary>
    private void CleanupAudioFile()
    {
        if (!string.IsNullOrEmpty(_currentAudioFilePath))
        {
            try
            {
                if (File.Exists(_currentAudioFilePath))
                {
                    File.Delete(_currentAudioFilePath);
                }
            }
            catch
            {
                // Best effort cleanup - don't throw
            }
            finally
            {
                _currentAudioFilePath = null;
            }
        }
    }

    /// <summary>
    /// Parses the custom vocabulary string into individual lines.
    /// </summary>
    private static IReadOnlyList<string> ParseVocabulary(string vocabulary)
    {
        if (string.IsNullOrWhiteSpace(vocabulary))
        {
            return Array.Empty<string>();
        }

        return vocabulary
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToList();
    }

    /// <summary>
    /// Forwards audio level events.
    /// </summary>
    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        // Only forward if we're in Recording state
        if (CurrentState == PipelineState.Recording)
        {
            AudioLevelChanged?.Invoke(this, e);
        }
    }

    /// <summary>
    /// Disposes resources and unsubscribes from events.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed and unmanaged resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Unsubscribe from audio recorder events to prevent memory leaks
            _audioRecorder.AudioLevelChanged -= OnAudioLevelChanged;

            // Cancel any active pipeline operation
            _pipelineCts?.Cancel();
            _pipelineCts?.Dispose();
            _pipelineCts = null;

            // Restore any muted audio
            HandleAudioMutingEnd();

            // Clean up any remaining audio file
            CleanupAudioFile();
        }

        _disposed = true;
    }
}
