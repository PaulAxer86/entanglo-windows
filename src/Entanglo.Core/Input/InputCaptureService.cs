using System.Runtime.InteropServices;
using Entanglo.Core.Logging;
using Entanglo.Core.Protocol;
using Entanglo.Core.Protocol.Payloads;
using Entanglo.PInvoke;

namespace Entanglo.Core.Input;

/// <summary>
/// Low-level mouse + keyboard hook. Runs on a dedicated thread with its own
/// Win32 message pump so Windows doesn't time out and disable the hook (~5 s).
/// When capture is active, Raw Input provides raw relative deltas (cursor is
/// confined with ClipCursor so the local pointer never moves), and
/// RawMouseCaptured fires for each mouse move event.
/// PROTOCOL.md §5.5.
/// </summary>
public sealed class InputCaptureService : IDisposable
{
    const uint VK_ESCAPE = 0x1B;
    const uint VK_SCROLL = 0x91; // Scroll Lock — alternative release key
    const uint WM_QUIT   = 0x0012;

    nint _mouseHook;
    nint _keyboardHook;
    Thread? _hookThread;
    uint    _hookWin32ThreadId;
    volatile bool _active;
    volatile bool _capturing;
    nint _rawHwnd;      // message-only window for Raw Input registration
    bool _cursorHidden; // tracks ShowCursor balance — only modified on hook thread

    // Raised on the hook thread for all captured events (button, scroll, keyboard,
    // and WM_MOUSEMOVE when NOT in capture mode — edge detection uses this).
    public event Action<InputEventMessage>? EventCaptured;

    // Raised on the hook thread when capturing, with raw relative deltaX/deltaY from
    // WM_INPUT. The LL mouse hook's WM_MOUSEMOVE is suppressed during capture so this
    // is the only source of mouse motion while the cursor is confined.
    public event Action<InputEventMessage>? RawMouseCaptured;

    // Fired when Escape or Scroll Lock is pressed while capturing.
    public event Action? ReleaseRequested;

    public bool IsCapturing => _capturing;

    public void Start()
    {
        _active = true;
        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name         = "EntangloHook",
            Priority     = ThreadPriority.AboveNormal,
        };
        _hookThread.Start();
    }

    // Called by EdgeSwitcherService when cursor crosses a screen edge.
    public void EnterCapture()
    {
        if (_capturing) return;
        _capturing = true;
        var sw = User32.GetSystemMetrics(User32.SM_CXSCREEN);
        var sh = User32.GetSystemMetrics(User32.SM_CYSCREEN);
        // Confine the cursor to a 1×1 rect at the center so the local pointer never moves.
        var cx = sw / 2;
        var cy = sh / 2;
        unsafe
        {
            var rect = new User32.RECT { left = cx, top = cy, right = cx + 1, bottom = cy + 1 };
            User32.ClipCursor(&rect);
        }
        if (!_cursorHidden)
        {
            User32.ShowCursor(false);
            _cursorHidden = true;
        }
    }

    // Called by EdgeSwitcherService on release (Escape, edge return, disconnect).
    // Releases cursor confinement and makes the cursor visible again.
    public void ExitCapture()
    {
        _capturing = false;
        unsafe { User32.ClipCursor(null); }
        if (_cursorHidden)
        {
            User32.ShowCursor(true);
            _cursorHidden = false;
        }
    }

    void HookThreadProc()
    {
        _hookWin32ThreadId = Kernel32.GetCurrentThreadId();
        var hMod = Kernel32.GetModuleHandle(null);

        // Create a message-only hidden window so Raw Input has a target HWND.
        _rawHwnd = User32.CreateWindowExW(
            0, "STATIC", null, 0, 0, 0, 0, 0,
            User32.HWND_MESSAGE, nint.Zero, nint.Zero, nint.Zero);

        if (_rawHwnd != nint.Zero)
        {
            var rid = new User32.RAWINPUTDEVICE
            {
                usUsagePage = 0x01, // HID_USAGE_PAGE_GENERIC
                usUsage     = 0x02, // HID_USAGE_GENERIC_MOUSE
                dwFlags     = User32.RIDEV_INPUTSINK, // receive even when not foreground
                hwndTarget  = _rawHwnd,
            };
            User32.RegisterRawInputDevices([rid], 1,
                (uint)Marshal.SizeOf<User32.RAWINPUTDEVICE>());
        }
        else
        {
            AppLog.Warning("[Capture] Raw Input window creation failed — relative motion unavailable");
        }

        _mouseHook    = User32.SetWindowsHookExW(User32.WH_MOUSE_LL,    MouseProc,    hMod, 0);
        _keyboardHook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, KeyboardProc, hMod, 0);

        while (_active && User32.GetMessage(out var msg, 0, 0, 0))
        {
            if (msg.message == User32.WM_INPUT && _capturing)
                HandleRawInput(msg.lParam);
            User32.TranslateMessage(msg);
            User32.DispatchMessageW(msg);
        }

        if (_mouseHook    != 0) User32.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != 0) User32.UnhookWindowsHookEx(_keyboardHook);
        if (_rawHwnd != nint.Zero) User32.DestroyWindow(_rawHwnd);
    }

    unsafe void HandleRawInput(nint hRawInput)
    {
        uint size = 0;
        User32.GetRawInputData(hRawInput, User32.RID_INPUT, nint.Zero, ref size,
            (uint)sizeof(User32.RAWINPUTHEADER));
        if (size == 0 || size > 256) return;

        nint buf = Marshal.AllocHGlobal((int)size);
        try
        {
            uint written = User32.GetRawInputData(hRawInput, User32.RID_INPUT, buf, ref size,
                (uint)sizeof(User32.RAWINPUTHEADER));
            if (written == 0) return;

            var raw = Marshal.PtrToStructure<User32.RAWINPUT>(buf);
            if (raw.header.dwType != 0) return; // not mouse
            var dx = raw.mouse.lLastX;
            var dy = raw.mouse.lLastY;
            if (dx == 0 && dy == 0) return;

            var evt = new InputEventMessage(
                "mouseMove", null, null, dx, dy, null, null, null, 0, null, null);
            RawMouseCaptured?.Invoke(evt);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);

            if (data.dwExtraInfo == User32.INJECTED_EXTRA_INFO)
                return User32.CallNextHookEx(0, nCode, wParam, lParam);

            if (_capturing && wParam == User32.WM_MOUSEMOVE)
                return 1; // Raw Input handles moves during capture; consume to prevent passthrough

            var evt = BuildMouseEvent((int)wParam, data);
            if (evt != null)
            {
                EventCaptured?.Invoke(evt);
                if (_capturing) return 1;
            }
        }
        return User32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);

            // Escape / Scroll Lock exits capture mode — consume before anything else.
            if (_capturing
                && (data.vkCode == VK_ESCAPE || data.vkCode == VK_SCROLL)
                && (wParam == User32.WM_KEYDOWN || wParam == User32.WM_SYSKEYDOWN))
            {
                ReleaseRequested?.Invoke();
                return 1;
            }

            if (data.dwExtraInfo == (uint)User32.INJECTED_EXTRA_INFO)
                return User32.CallNextHookEx(0, nCode, wParam, lParam);

            bool isDown = wParam == User32.WM_KEYDOWN || wParam == User32.WM_SYSKEYDOWN;

            // Media / volume keys: forward as mediaKey events (no Mac keycode equivalent)
            string? mediaKey = (int)data.vkCode switch
            {
                User32.VK_VOLUME_MUTE      => "mute",
                User32.VK_VOLUME_DOWN      => "volumeDown",
                User32.VK_VOLUME_UP        => "volumeUp",
                User32.VK_MEDIA_NEXT_TRACK => "next",
                User32.VK_MEDIA_PREV_TRACK => "previous",
                User32.VK_MEDIA_STOP       => "stop",
                User32.VK_MEDIA_PLAY_PAUSE => "playPause",
                _                          => null,
            };
            if (mediaKey != null)
            {
                var evt = new InputEventMessage(
                    "mediaKey", null, null, null, null, null, null, mediaKey,
                    GetCurrentModifiers(), isDown, null);
                EventCaptured?.Invoke(evt);
                if (_capturing) return 1;
            }

            // Regular keys: map Mac keycode and forward
            int? macKey = KeyMap.WinVkToMac((int)data.vkCode);
            if (macKey.HasValue)
            {
                var evt = new InputEventMessage(
                    Kind: isDown ? "keyDown" : "keyUp",
                    X: null, Y: null, DeltaX: null, DeltaY: null,
                    Button: null,
                    KeyCode: macKey.Value,
                    MediaKey: null,
                    ModifierFlags: GetCurrentModifiers(),
                    Pressed: isDown,
                    ClickState: null);
                EventCaptured?.Invoke(evt);
                if (_capturing) return 1;
            }
        }
        return User32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    static InputEventMessage? BuildMouseEvent(int msg, User32.MSLLHOOKSTRUCT data)
    {
        ulong mods = GetCurrentModifiers();
        return msg switch
        {
            User32.WM_MOUSEMOVE => new InputEventMessage(
                "mouseMove", data.pt.X, data.pt.Y, null, null, null, null, null, mods, null, null),
            User32.WM_LBUTTONDOWN => new InputEventMessage(
                "mouseDown", data.pt.X, data.pt.Y, null, null, 0, null, null, mods, true, 1),
            User32.WM_LBUTTONUP => new InputEventMessage(
                "mouseUp", data.pt.X, data.pt.Y, null, null, 0, null, null, mods, false, null),
            User32.WM_RBUTTONDOWN => new InputEventMessage(
                "mouseDown", data.pt.X, data.pt.Y, null, null, 1, null, null, mods, true, 1),
            User32.WM_RBUTTONUP => new InputEventMessage(
                "mouseUp", data.pt.X, data.pt.Y, null, null, 1, null, null, mods, false, null),
            User32.WM_MBUTTONDOWN => new InputEventMessage(
                "mouseDown", data.pt.X, data.pt.Y, null, null, 2, null, null, mods, true, 1),
            User32.WM_MBUTTONUP => new InputEventMessage(
                "mouseUp", data.pt.X, data.pt.Y, null, null, 2, null, null, mods, false, null),
            User32.WM_MOUSEWHEEL => new InputEventMessage(
                "scroll", null, null, 0, (short)(data.mouseData >> 16) / 1.0, null, null, null, mods, null, null),
            User32.WM_MOUSEHWHEEL => new InputEventMessage(
                "scroll", null, null, (short)(data.mouseData >> 16) / 1.0, 0, null, null, null, mods, null, null),
            _ => null,
        };
    }

    static ulong GetCurrentModifiers()
    {
        ulong flags = 0;
        if ((User32.GetKeyState(0x10) & 0x8000) != 0) flags |= ModifierFlags.Shift;
        if ((User32.GetKeyState(0x11) & 0x8000) != 0) flags |= ModifierFlags.Control;
        if ((User32.GetKeyState(0x12) & 0x8000) != 0) flags |= ModifierFlags.Option;
        if ((User32.GetKeyState(0x14) & 0x0001) != 0) flags |= ModifierFlags.CapsLock;
        if ((User32.GetKeyState(0x5B) & 0x8000) != 0) flags |= ModifierFlags.Command; // LWIN
        return flags;
    }

    public void Dispose()
    {
        ExitCapture(); // release ClipCursor / ShowCursor on any exit path
        _active = false;
        if (_hookWin32ThreadId != 0)
            User32.PostThreadMessageW(_hookWin32ThreadId, WM_QUIT, 0, 0);
        _hookThread?.Join(500);
    }
}
