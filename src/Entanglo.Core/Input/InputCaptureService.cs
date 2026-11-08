using System.Runtime.InteropServices;
using Entanglo.Core.Protocol;
using Entanglo.Core.Protocol.Payloads;
using Entanglo.PInvoke;

namespace Entanglo.Core.Input;

/// <summary>
/// Low-level mouse + keyboard hook. Runs on a dedicated thread with its own
/// Win32 message pump so Windows doesn't time out and disable the hook (~5 s).
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
    uint    _hookWin32ThreadId; // Win32 thread ID captured inside HookThreadProc
    volatile bool _active;
    volatile bool _capturing;

    // Raised on the hook thread — dispatch to UI/network on caller side
    public event Action<InputEventMessage>? EventCaptured;

    // Fired when the user presses Escape or Scroll Lock while capturing, so the
    // caller (EdgeSwitcherService) can exit capture mode cleanly.  The key is
    // already consumed before this fires — it will NOT reach Windows or the peer.
    public event Action? ReleaseRequested;

    // When true, events are consumed (not passed to local system) and forwarded to peer
    public bool IsCapturing
    {
        get => _capturing;
        set => _capturing = value;
    }

    // Install hooks on a dedicated message-pump thread
    public void Start()
    {
        _active = true;
        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "EntangloHook",
            Priority = ThreadPriority.AboveNormal, // keep input responsive even when the app is backgrounded
        };
        _hookThread.Start();
    }

    void HookThreadProc()
    {
        // Store Win32 thread ID so Dispose() can post WM_QUIT to THIS thread
        // (PostQuitMessage targets the calling thread, not this one).
        _hookWin32ThreadId = Kernel32.GetCurrentThreadId();

        var hMod = Kernel32.GetModuleHandle(null);
        _mouseHook    = User32.SetWindowsHookExW(User32.WH_MOUSE_LL,    MouseProc,    hMod, 0);
        _keyboardHook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, KeyboardProc, hMod, 0);

        // Message pump — required or Windows kills the hook after ~5 s
        while (_active && User32.GetMessage(out var msg, 0, 0, 0))
        {
            User32.TranslateMessage(msg);
            User32.DispatchMessageW(msg);
        }

        if (_mouseHook    != 0) User32.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != 0) User32.UnhookWindowsHookEx(_keyboardHook);
    }

    nint MouseProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<User32.MSLLHOOKSTRUCT>(lParam);

            // Skip events we injected ourselves
            if (data.dwExtraInfo == User32.INJECTED_EXTRA_INFO)
                return User32.CallNextHookEx(0, nCode, wParam, lParam);

            var evt = BuildMouseEvent((int)wParam, data);
            if (evt != null)
            {
                EventCaptured?.Invoke(evt);
                if (_capturing) return 1; // consume — don't pass to local system
            }
        }
        return User32.CallNextHookEx(0, nCode, wParam, lParam);
    }

    nint KeyboardProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);

            // While in capture mode, Escape and Scroll Lock exit controller mode.
            // Consume the key first so it never reaches Windows or the remote peer.
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
            int? macKey = Protocol.KeyMap.WinVkToMac((int)data.vkCode);
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
        _active = false;
        // PostQuitMessage(0) would target the CALLING thread (UI thread), not
        // the hook thread — causing the app to hang on quit.  Instead post
        // WM_QUIT directly to the hook thread's Win32 message queue.
        if (_hookWin32ThreadId != 0)
            User32.PostThreadMessageW(_hookWin32ThreadId, WM_QUIT, 0, 0);
        _hookThread?.Join(500);
    }
}
