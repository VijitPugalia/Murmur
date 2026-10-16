using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Murmur.Core.Interfaces;
using Murmur.Core.Models;
using Murmur.Core.Services;

namespace Murmur.App.Services;

/// <summary>
/// Hotkey manager using Windows API RegisterHotKey with ComponentDispatcher.
/// Uses the main UI thread's message pump to receive WM_HOTKEY messages.
/// </summary>
public class WpfHotkeyManager : IHotkeyManager, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_CONTROL = 0x11;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_SHIFT = 0x10;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const byte VK_DUMMY = 0xE8; // Used to suppress Windows Start menu on Win key release
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const int HOLD_HOTKEY_ID = 9001;
    private const int TOGGLE_HOTKEY_ID = 9002;
    private const int PASTE_AGAIN_HOTKEY_ID = 9003;

    private readonly object _lock = new();
    private HotkeyConfiguration? _configuration;
    private IntPtr _keyboardHookHandle;
    private LowLevelKeyboardProc? _keyboardProc;
    private bool _isHoldActive;
    private bool _disposed;
    private bool _isHooked;

    private static readonly HashSet<(ModifierKeys, VirtualKey)> KnownSystemHotkeys = new()
    {
        (ModifierKeys.Win, VirtualKey.D), (ModifierKeys.Win, VirtualKey.E),
        (ModifierKeys.Win, VirtualKey.L), (ModifierKeys.Win, VirtualKey.R),
        (ModifierKeys.Win, VirtualKey.S), (ModifierKeys.Win, VirtualKey.Tab),
        (ModifierKeys.Alt, VirtualKey.Tab), (ModifierKeys.Alt, VirtualKey.F4),
    };

    public event EventHandler<HotkeyEventArgs>? HoldHotkeyPressed;
    public event EventHandler<HotkeyEventArgs>? HoldHotkeyReleased;
    public event EventHandler<HotkeyEventArgs>? ToggleHotkeyPressed;
    public event EventHandler<HotkeyEventArgs>? PasteAgainHotkeyPressed;

    public bool IsActive { get; private set; }
    public HotkeyConfiguration? CurrentConfiguration => _configuration;

    private void Log(string message)
    {
        DebugLogger.Log($"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
    }

    public bool RegisterHotkeys(HotkeyConfiguration config)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));

        Log($"RegisterHotkeys called");

        lock (_lock)
        {
            UnregisterAll();
            _configuration = config;

            // Hook into the ComponentDispatcher to receive WM_HOTKEY messages on UI thread
            if (!_isHooked)
            {
                ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;
                _isHooked = true;
                Log("Hooked into ComponentDispatcher");
            }

            // Register hotkeys to the thread (IntPtr.Zero)
            // This allows receiving WM_HOTKEY via the message loop

            // Only attempt Win32 RegisterHotKey if a non-modifier key is specified.
            // Pure modifier combinations like Ctrl+Win have Key == VirtualKey.None (vk=0),
            // which RegisterHotKey rejects with ERROR_INVALID_PARAMETER (87). Those are handled
            // reliably via our low-level keyboard hook.
            if (config.HoldHotkey.Key != VirtualKey.None)
            {
                var holdMods = GetModifiers(config.HoldHotkey.Modifiers);
                var holdKey = (uint)config.HoldHotkey.Key;
                Log($"Registering Hold with RegisterHotKey: {config.HoldHotkey} (mods=0x{holdMods:X}, key=0x{holdKey:X})");
                if (!RegisterHotKey(IntPtr.Zero, HOLD_HOTKEY_ID, holdMods, holdKey))
                {
                    var err = Marshal.GetLastWin32Error();
                    Log($"RegisterHotKey for Hold returned {err}; falling back to low-level keyboard hook.");
                }
                else
                {
                    Log("Hold hotkey registered via RegisterHotKey");
                }
            }
            else
            {
                Log($"Hold hotkey {config.HoldHotkey} is modifier-only; handled via low-level hook.");
            }

            // Register Toggle hotkey
            if (config.ToggleHotkey.Key != VirtualKey.None)
            {
                var toggleMods = GetModifiers(config.ToggleHotkey.Modifiers);
                var toggleKey = (uint)config.ToggleHotkey.Key;
                Log($"Registering Toggle: {config.ToggleHotkey} (mods=0x{toggleMods:X}, key=0x{toggleKey:X})");
                
                if (!RegisterHotKey(IntPtr.Zero, TOGGLE_HOTKEY_ID, toggleMods, toggleKey))
                {
                    var err = Marshal.GetLastWin32Error();
                    Log($"RegisterHotKey for Toggle returned {err}; falling back to low-level keyboard hook.");
                }
                else
                {
                    Log("Toggle hotkey registered successfully via RegisterHotKey");
                }
            }

            // Register Paste Again hotkey if configured
            if (config.PasteAgainHotkey != null && config.PasteAgainHotkey.Key != VirtualKey.None)
            {
                var pasteAgainMods = GetModifiers(config.PasteAgainHotkey.Modifiers);
                var pasteAgainKey = (uint)config.PasteAgainHotkey.Key;
                RegisterHotKey(IntPtr.Zero, PASTE_AGAIN_HOTKEY_ID, pasteAgainMods, pasteAgainKey);
            }

            // Install keyboard hook for detecting key release and modifier-only hotkeys
            InstallKeyboardHook();

            IsActive = true;
            Log("All hotkeys registered, IsActive = true");
            return true;
        }
    }

    private void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message == WM_HOTKEY)
        {
            var hotkeyId = msg.wParam.ToInt32();
            Log($"WM_HOTKEY received via ComponentDispatcher! ID={hotkeyId}");
            
            handled = true;
            ProcessHotkey(hotkeyId);
        }
    }

    private void ProcessHotkey(int hotkeyId)
    {
        lock (_lock)
        {
            if (_configuration == null)
            {
                Log("Configuration is null, ignoring hotkey");
                return;
            }

            switch (hotkeyId)
            {
                case HOLD_HOTKEY_ID:
                    Log("Processing HOLD hotkey press");
                    _isHoldActive = true;
                    OnHoldHotkeyPressed(_configuration.HoldHotkey);
                    break;
                case TOGGLE_HOTKEY_ID:
                    Log("Processing TOGGLE hotkey press");
                    OnToggleHotkeyPressed(_configuration.ToggleHotkey);
                    break;
                case PASTE_AGAIN_HOTKEY_ID:
                    Log("Processing PASTE_AGAIN hotkey press");
                    if (_configuration.PasteAgainHotkey != null)
                        OnPasteAgainHotkeyPressed(_configuration.PasteAgainHotkey);
                    break;
            }
        }
    }

    private uint GetModifiers(ModifierKeys modifiers)
    {
        uint result = MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Ctrl)) result |= MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Win)) result |= MOD_WIN;
        return result;
    }

    public void UnregisterAll()
    {
        Log("UnregisterAll called");
        lock (_lock)
        {
            UnregisterHotKey(IntPtr.Zero, HOLD_HOTKEY_ID);
            UnregisterHotKey(IntPtr.Zero, TOGGLE_HOTKEY_ID);
            UnregisterHotKey(IntPtr.Zero, PASTE_AGAIN_HOTKEY_ID);
            
            if (_isHooked)
            {
                ComponentDispatcher.ThreadPreprocessMessage -= OnThreadMessage;
                _isHooked = false;
            }
            
            UninstallKeyboardHook();
            _configuration = null;
            _isHoldActive = false;
            IsActive = false;
        }
    }

    public HotkeyConflict? CheckConflict(HotkeyBinding binding)
    {
        if (binding == null) throw new ArgumentNullException(nameof(binding));
        var key = (binding.Modifiers, binding.Key);
        if (KnownSystemHotkeys.Contains(key))
            return new HotkeyConflict { Binding = binding, ConflictDescription = "Conflicts with Windows shortcut.", IsSystemHotkey = true, ConflictingOwner = "Windows" };
        return null;
    }

    private void InstallKeyboardHook()
    {
        Log("Installing keyboard hook for key release detection");
        _keyboardProc = LowLevelKeyboardProcCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
        _keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
        
        if (_keyboardHookHandle == IntPtr.Zero)
        {
            Log($"Failed to install keyboard hook: {Marshal.GetLastWin32Error()}");
        }
        else
        {
            Log("Keyboard hook installed successfully");
        }
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
            Log("Keyboard hook uninstalled");
        }
        _keyboardProc = null;
    }

    private static bool IsCtrlDown() =>
        (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;

    private static bool IsWinDown() =>
        (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    private static bool IsAltDown() =>
        (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;

    private static bool IsShiftDown() =>
        (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 ||
        (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;

    private static void SuppressStartMenu()
    {
        keybd_event(VK_DUMMY, 0, 0, UIntPtr.Zero);
        keybd_event(VK_DUMMY, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static bool MatchesBinding(HotkeyBinding? binding, uint vkCode, bool checkMainKey)
    {
        if (binding == null || binding.IsDisabled) return false;

        bool ctrlRequired = (binding.Modifiers & ModifierKeys.Ctrl) != 0;
        bool winRequired = (binding.Modifiers & ModifierKeys.Win) != 0;
        bool altRequired = (binding.Modifiers & ModifierKeys.Alt) != 0;
        bool shiftRequired = (binding.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrlRequired != IsCtrlDown()) return false;
        if (winRequired != IsWinDown()) return false;
        if (altRequired != IsAltDown()) return false;
        if (shiftRequired != IsShiftDown()) return false;

        if (binding.Key == VirtualKey.None)
        {
            return true;
        }

        if (checkMainKey)
        {
            return (VirtualKey)vkCode == binding.Key;
        }

        return true;
    }

    private IntPtr LowLevelKeyboardProcCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _configuration != null)
        {
            var kbStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vkCode = kbStruct.vkCode;
            var msgType = wParam.ToInt32();
            bool isKeyDown = msgType == WM_KEYDOWN || msgType == WM_SYSKEYDOWN;
            bool isKeyUp = msgType == WM_KEYUP || msgType == WM_SYSKEYUP;

            var holdHotkey = _configuration.HoldHotkey;

            // Handle HOLD hotkey key-down
            if (isKeyDown && !_isHoldActive && holdHotkey != null && !holdHotkey.IsDisabled)
            {
                bool matches = false;
                if (holdHotkey.Key == VirtualKey.None)
                {
                    matches = MatchesBinding(holdHotkey, vkCode, checkMainKey: false);
                }
                else
                {
                    matches = MatchesBinding(holdHotkey, vkCode, checkMainKey: true);
                }

                if (matches)
                {
                    Log($"Hold key combination pressed: {holdHotkey}");
                    _isHoldActive = true;
                    if ((holdHotkey.Modifiers & ModifierKeys.Win) != 0)
                    {
                        SuppressStartMenu();
                    }
                    var binding = holdHotkey;
                    Application.Current?.Dispatcher.BeginInvoke(() => OnHoldHotkeyPressed(binding));
                }
            }
            // Handle HOLD hotkey key-up
            else if (isKeyUp && _isHoldActive && holdHotkey != null)
            {
                bool shouldRelease = false;

                if (holdHotkey.Key == VirtualKey.None)
                {
                    bool ctrlReleased = (holdHotkey.Modifiers & ModifierKeys.Ctrl) != 0 && (vkCode is (uint)VK_CONTROL or (uint)VK_LCONTROL or (uint)VK_RCONTROL);
                    bool winReleased = (holdHotkey.Modifiers & ModifierKeys.Win) != 0 && (vkCode is (uint)VK_LWIN or (uint)VK_RWIN);
                    bool altReleased = (holdHotkey.Modifiers & ModifierKeys.Alt) != 0 && (vkCode is (uint)VK_MENU or (uint)VK_LMENU or (uint)VK_RMENU);
                    bool shiftReleased = (holdHotkey.Modifiers & ModifierKeys.Shift) != 0 && (vkCode is (uint)VK_SHIFT or (uint)VK_LSHIFT or (uint)VK_RSHIFT);

                    shouldRelease = ctrlReleased || winReleased || altReleased || shiftReleased;
                }
                else
                {
                    bool mainKeyReleased = vkCode == (uint)holdHotkey.Key;
                    bool ctrlReleased = (holdHotkey.Modifiers & ModifierKeys.Ctrl) != 0 && (vkCode is (uint)VK_CONTROL or (uint)VK_LCONTROL or (uint)VK_RCONTROL);
                    bool winReleased = (holdHotkey.Modifiers & ModifierKeys.Win) != 0 && (vkCode is (uint)VK_LWIN or (uint)VK_RWIN);
                    bool altReleased = (holdHotkey.Modifiers & ModifierKeys.Alt) != 0 && (vkCode is (uint)VK_MENU or (uint)VK_LMENU or (uint)VK_RMENU);
                    bool shiftReleased = (holdHotkey.Modifiers & ModifierKeys.Shift) != 0 && (vkCode is (uint)VK_SHIFT or (uint)VK_LSHIFT or (uint)VK_RSHIFT);

                    shouldRelease = mainKeyReleased || ctrlReleased || winReleased || altReleased || shiftReleased;
                }

                if (shouldRelease)
                {
                    Log($"Hold key released (vkCode={vkCode})");
                    _isHoldActive = false;
                    if ((holdHotkey.Modifiers & ModifierKeys.Win) != 0)
                    {
                        SuppressStartMenu();
                    }
                    var binding = holdHotkey;
                    Application.Current?.Dispatcher.BeginInvoke(() => OnHoldHotkeyReleased(binding));
                }
            }
        }
        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private void OnHoldHotkeyPressed(HotkeyBinding b)
    {
        Log("Firing HoldHotkeyPressed event");
        HoldHotkeyPressed?.Invoke(this, new HotkeyEventArgs { Hotkey = b, RecordingMode = RecordingMode.Hold, Timestamp = DateTime.UtcNow });
    }

    private void OnHoldHotkeyReleased(HotkeyBinding b)
    {
        Log("Firing HoldHotkeyReleased event");
        HoldHotkeyReleased?.Invoke(this, new HotkeyEventArgs { Hotkey = b, RecordingMode = RecordingMode.Hold, Timestamp = DateTime.UtcNow });
    }

    private void OnToggleHotkeyPressed(HotkeyBinding b)
    {
        Log("Firing ToggleHotkeyPressed event");
        ToggleHotkeyPressed?.Invoke(this, new HotkeyEventArgs { Hotkey = b, RecordingMode = RecordingMode.Toggle, Timestamp = DateTime.UtcNow });
    }

    private void OnPasteAgainHotkeyPressed(HotkeyBinding b)
    {
        Log("Firing PasteAgainHotkeyPressed event");
        PasteAgainHotkeyPressed?.Invoke(this, new HotkeyEventArgs { Hotkey = b, Timestamp = DateTime.UtcNow });
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                UnregisterAll();
            }
            _disposed = true;
        }
    }

    ~WpfHotkeyManager()
    {
        Dispose(false);
    }

    #region Win32

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    #endregion
}
