# Changelog

All notable changes to the Murmur Windows project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.1.4] - 2026-10-22

### Added
- **Indicator Stop Button Commits Dictation**: In Toggle mode, clicking the floating recording indicator's Stop button now commits transcription and pastes the result directly into your active window via `orchestrator.StopAsync()` (rather than cancelling or discarding).
- **Safe Clipboard Collision Guard**: `ClipboardManager` now verifies whether the clipboard still contains Murmur's transcript before restoring pre-existing clipboard text, preventing accidental overwrites if you copied something else while transcription was processing.
- **Shortcut Preset Selection Highlighting**: Improved visual active state highlighting and checkmarks on preset shortcut buttons (`Ctrl+Win`, `Win+Space`, `Alt+Space`, `F2`) in the Settings window.

### Fixed
- **WPF Alt-Key Recording**: Fixed key capture logic so combinations using the `Alt` key (or `SystemKey`) can be assigned without triggering Windows system menu focus.
- **Unified Hotkey Registration**: Fixed edge-case where re-registering hotkeys with modifier-only bindings could fail silently during settings save.

### Build & Release
- Automated GitHub Actions release pipeline for self-contained, single-file ReadyToRun Windows x64 binaries.

---

## [1.1.3] - 2026-10-22

### Performance (<10ms Activation)
- **Sub-10ms Hotkey Response**: Replaced UI dispatcher queuing with high-priority `DispatcherPriority.Send`, delivering immediate audio capture initiation when pressing the hotkey.
- **Standby Audio Device Pre-Warming**: Pre-warmed `WaveInEvent` capture buffers and pre-allocated static `WaveFormat` to eliminate device initialization latency on first press.
- **O(1) Audio Device Resolution**: Replaced synchronous audio device enumeration loops with an O(1) device count check and 5-second TTL cache.
- **Parallel Context Capture**: Offloaded active window context detection and CoreAudio endpoint ducking to parallel background worker threads to keep the UI thread completely unblocked.
- **Zero-Latency In-Memory Settings**: Cached application settings in memory with filesystem timestamp validation, eliminating repetitive disk reads on each hotkey press.
- **Pre-Rendered Chimes & Pre-Warmed HUD**: Pre-rendered audio chime PCM waveforms into native `SoundPlayer` instances and pre-warmed the HUD window handle (`HWND`) with cached DPI scale matrices.

---

## [1.1.2] - 2026-10-21

### Added
- **Creator Attribution**: Added high-resolution creator photo and "Powered by Vijit Pugalia" attribution across Settings, History Window, and the System Tray tooltip.

### Changed
- **Visual Polish**: Refined margins, font sizes, card padding, and aligned button placement in the Settings and History dialogs.

---

## [1.1.1] - 2026-10-21

### Fixed
- **Accidental Tap Protection**: Filtered out ultra-short accidental hotkey taps (<150ms) to prevent empty or corrupted audio payloads from being sent to transcription providers.
- **Provider Error Parsing**: Enhanced HTTP 400 error diagnostics to inspect and parse underlying JSON error payloads from Groq and OpenAI instead of showing generic HTTP error codes.
- **Pipeline Fault Tolerance**: Improved graceful recovery when transcription requests fail, avoiding stuck recording states.

---

## [1.1.0] - 2026-10-21

### Added
- **Unified Hotkey Engine**: Introduced single configurable shortcut (default: `Ctrl+Win`) supporting both Hold-to-Talk (>400ms) and double-tap hands-free latch (<350ms).
- **Interactive Shortcut Capture**: Added interactive Click-to-Record keyboard capturing in Settings with automatic modifier detection.
- **Private Win32 Clipboard Tags**: Dictations are tagged with Win32 flags (`CanIncludeInClipboardHistory = 0`, `CanUploadToCloudClipboard = 0`, `ExcludeClipboardContentFromMonitorProcessing`) preventing sensitive transcripts from persisting in Windows Clipboard History (`Win+V`) or syncing to cloud.
- **Dual-Format Rich Clipboard (HTML + Plain Text)**: Added `CF_HTML` envelope generation alongside `CF_UNICODETEXT` for accurate rich formatting when pasting into apps like Microsoft Word, Slack, and Google Docs.
- **Non-Activating Overlay HUD**: Handled `WM_MOUSEACTIVATE` returning `MA_NOACTIVATE`, ensuring interactions with the floating HUD never steal focus from the user's active editor or input field.
- **Windows 11 EcoQoS & Sleep Prevention**: Automatically opts out of CPU power throttling (`ProcessPowerThrottling`) during recording and blocks system sleep via `SetThreadExecutionState`.
- **Streamlined 5-Tab Settings UI**: Replaced 20+ scrolling cards with a clean sidebar navigation (Shortcuts & General, Audio & Recording, AI & Models, Macros & Vocab, Advanced & Logs) with sticky Save/Close footer.

---

## [1.0.0] - 2026-10-17

### Added
- **Initial Release of Murmur for Windows**: Native, ultra-responsive Windows voice dictation tool powered by speech-to-text models and AI refinement.
- **System Tray Core & Lifecycle**: Lightweight background tray client with live animated waveform icon, status transitions (Idle, Recording, Transcribing), and instant Windows startup support.
- **Global Hotkey Support**: Hold-to-Talk (press to record, release to transcribe) and Toggle Mode (press to start, press again to stop) supporting Win, Ctrl, Alt, Shift, and F1–F12 keys.
- **Floating HUD Recording Indicator**: Non-activating overlay with live audio level meters and responsive state animations.
- **Dual Pipeline (Speech-to-Text & AI Refinement)**: High-speed transcription (Groq Whisper / OpenAI Whisper) combined with LLM post-processing (formatting, punctuation, grammar, code-block awareness).
- **Direct App Pasting**: Injects dictated text directly into the focused active window via clipboard emulation with automatic clipboard backup/restoration.
- **Per-App Profiles & Voice Snippets**: Custom LLM prompt rules based on active foreground application and expandable snippet expansions.
- **Settings & History Window**: Complete configuration UI for audio input devices, API keys, hotkeys, models, plus a searchable history viewer with sound chime feedback.

[1.1.4]: https://github.com/VijitPugalia/Murmur/compare/v1.1.3...v1.1.4
[1.1.3]: https://github.com/VijitPugalia/Murmur/compare/v1.1.2...v1.1.3
[1.1.2]: https://github.com/VijitPugalia/Murmur/compare/v1.1.1...v1.1.2
[1.1.1]: https://github.com/VijitPugalia/Murmur/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/VijitPugalia/Murmur/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/VijitPugalia/Murmur/commits/v1.0.0
