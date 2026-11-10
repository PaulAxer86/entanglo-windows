using Entanglo.Core.Logging;
using Entanglo.Core.Net;
using Entanglo.Core.Protocol.Payloads;
using Entanglo.PInvoke;

namespace Entanglo.Core.Input;

/// <summary>
/// Watches mouse-edge crossings and routes input events to the configured peer
/// while the cursor is in capture mode.  Escape or Scroll Lock exits capture.
///
/// When the peer's screen dimensions are known (from Hello), tracks a virtual
/// cursor in the peer's pixel space and sends clamped absolute x/y.  When they
/// are not known, sends raw relative deltaX/deltaY.
///
/// Edge return: when the virtual cursor crosses back over the entry edge with
/// no buttons held, local capture is released and the cursor is restored to the
/// matching position just inside our own entry edge.
/// </summary>
public sealed class EdgeSwitcherService : IDisposable
{
    const int EdgePx     = 3;   // pixels from edge to trigger capture
    const int CooldownMs = 600; // min ms between exit and re-enter

    enum EntryEdge { None, Left, Right, Top, Bottom }

    readonly InputCaptureService   _capture;
    readonly ConnectionCoordinator _coordinator;
    readonly AppSettings           _settings;

    volatile string? _activePeerId;
    long _exitedAtTicks;

    int _screenW;
    int _screenH;

    // Virtual cursor state in the peer's pixel space
    EntryEdge _entryEdge;
    int       _peerW;    // peer screen dimensions from Hello (0 = unknown)
    int       _peerH;
    double    _virtualX; // current virtual cursor position on peer screen
    double    _virtualY;

    public string? ActivePeerId => _activePeerId;
    public bool    IsActive     => _activePeerId != null;

    public event Action? StateChanged;

    public EdgeSwitcherService(
        InputCaptureService   capture,
        ConnectionCoordinator coordinator,
        AppSettings           settings)
    {
        _capture     = capture;
        _coordinator = coordinator;
        _settings    = settings;

        RefreshScreenMetrics();

        _capture.EventCaptured    += OnEvent;
        _capture.RawMouseCaptured += OnRawMouse;
        _capture.ReleaseRequested += OnReleaseRequested;
    }

    public void RefreshScreenMetrics()
    {
        _screenW = Math.Max(1, User32.GetSystemMetrics(User32.SM_CXSCREEN));
        _screenH = Math.Max(1, User32.GetSystemMetrics(User32.SM_CYSCREEN));
    }

    // Handles LL-hook events: edge detection when idle; button/scroll/key forwarding
    // when capturing.  WM_MOUSEMOVE during capture is handled by OnRawMouse instead.
    void OnEvent(InputEventMessage evt)
    {
        if (!_settings.EdgeConfig.Enabled) return;

        var peerId = _activePeerId;

        if (peerId == null)
        {
            if (evt.Kind == "mouseMove" && evt.X.HasValue && evt.Y.HasValue)
            {
                var elapsed = (double)(Environment.TickCount64 - Interlocked.Read(ref _exitedAtTicks));
                if (elapsed >= CooldownMs)
                    TryEnterEdge((int)evt.X.Value, (int)evt.Y.Value);
            }
        }
        else
        {
            // mouseMove during capture is handled by OnRawMouse (from Raw Input) so we
            // skip it here to avoid sending the clamped/stale cursor position from the hook.
            if (evt.Kind == "mouseMove") return;

            // Strip position from button events — the receiver clicks at whatever position
            // the last absolute move put the remote cursor, not at our clipped clip rect.
            var toSend = evt;
            if (evt.Kind is "mouseDown" or "mouseUp")
                toSend = evt with { X = null, Y = null };

            _ = _coordinator.SendInputAsync(peerId, toSend);
        }
    }

    // Handles Raw Input (WM_INPUT) mouse deltas while capturing.
    void OnRawMouse(InputEventMessage evt)
    {
        var peerId = _activePeerId;
        if (peerId == null || evt.Kind != "mouseMove") return;

        var dx = evt.DeltaX ?? 0;
        var dy = evt.DeltaY ?? 0;

        // Apply pointer speed (NaN or out-of-range → 1.0)
        var raw = _settings.PointerSpeed;
        double speed = double.IsNaN(raw) ? 1.0 : Math.Clamp(raw, 0.25, 4.0);
        dx *= speed;
        dy *= speed;

        if (_peerW > 0 && _peerH > 0)
        {
            // Virtual cursor: track absolute position on peer screen
            _virtualX += dx;
            _virtualY += dy;

            // Check whether the virtual cursor crossed back over the entry edge
            bool shouldReturn = _entryEdge switch
            {
                EntryEdge.Right  => _virtualX < 0,
                EntryEdge.Left   => _virtualX >= _peerW,
                EntryEdge.Bottom => _virtualY < 0,
                EntryEdge.Top    => _virtualY >= _peerH,
                _                => false,
            };

            if (shouldReturn)
            {
                ReturnToLocalScreen();
                return;
            }

            _virtualX = Math.Clamp(_virtualX, 0, _peerW - 1);
            _virtualY = Math.Clamp(_virtualY, 0, _peerH - 1);

            var move = new InputEventMessage(
                "mouseMove", _virtualX, _virtualY, null, null,
                null, null, null, 0, null, null);
            _ = _coordinator.SendInputAsync(peerId, move);
        }
        else
        {
            // No peer screen size known: send relative deltas
            var move = new InputEventMessage(
                "mouseMove", null, null, dx, dy, null, null, null, 0, null, null);
            _ = _coordinator.SendInputAsync(peerId, move);
        }
    }

    void OnReleaseRequested() => ExitCapture();

    void TryEnterEdge(int x, int y)
    {
        var cfg = _settings.EdgeConfig;
        string?    target = null;
        EntryEdge  edge   = EntryEdge.None;

        if      (x <= EdgePx                && cfg.LeftPeer   != null) { target = cfg.LeftPeer;   edge = EntryEdge.Left;   }
        else if (x >= _screenW - EdgePx - 1 && cfg.RightPeer  != null) { target = cfg.RightPeer;  edge = EntryEdge.Right;  }
        else if (y <= EdgePx                && cfg.TopPeer    != null) { target = cfg.TopPeer;    edge = EntryEdge.Top;    }
        else if (y >= _screenH - EdgePx - 1 && cfg.BottomPeer != null) { target = cfg.BottomPeer; edge = EntryEdge.Bottom; }

        if (target == null) return;

        if (!_coordinator.IsConnectedTo(target))
        {
            AppLog.Info($"[Edge] Edge peer {target} not connected — skipping switch");
            return;
        }

        // Look up peer screen dimensions from Hello
        var peer = _coordinator.ActivePeers.FirstOrDefault(p => p.DeviceId == target);
        var peerW = (peer?.ScreenWidth  ?? 0);
        var peerH = (peer?.ScreenHeight ?? 0);

        // Calculate entry position on the peer's screen at the matching edge.
        double vx, vy;
        switch (edge)
        {
            case EntryEdge.Right:   // crossing our right edge → peer's left edge
                vx = 1;
                vy = peerH > 0 ? (double)y / _screenH * peerH : y;
                break;
            case EntryEdge.Left:    // crossing our left edge → peer's right edge
                vx = peerW > 0 ? peerW - 2 : _screenW - 2;
                vy = peerH > 0 ? (double)y / _screenH * peerH : y;
                break;
            case EntryEdge.Bottom:  // crossing our bottom edge → peer's top edge
                vx = peerW > 0 ? (double)x / _screenW * peerW : x;
                vy = 1;
                break;
            default:                // EntryEdge.Top: crossing our top edge → peer's bottom edge
                vx = peerW > 0 ? (double)x / _screenW * peerW : x;
                vy = peerH > 0 ? peerH - 2 : _screenH - 2;
                break;
        }

        _peerW     = peerW;
        _peerH     = peerH;
        _virtualX  = vx;
        _virtualY  = vy;
        _entryEdge = edge;

        AppLog.Info($"[Edge] Entering capture → peer {target} (edge {edge}, peer {peerW}x{peerH}, entry {vx:F0},{vy:F0})");
        _activePeerId = target;
        _capture.EnterCapture();
        StateChanged?.Invoke();

        // Send initial absolute position immediately so the remote cursor warps to the entry point
        if (peerW > 0 && peerH > 0)
        {
            var init = new InputEventMessage(
                "mouseMove", vx, vy, null, null, null, null, null, 0, null, null);
            _ = _coordinator.SendInputAsync(target, init);
        }
    }

    void ExitCapture()
    {
        var prev = _activePeerId;
        _activePeerId = null;
        _entryEdge    = EntryEdge.None;
        _capture.ExitCapture();
        Interlocked.Exchange(ref _exitedAtTicks, Environment.TickCount64);
        if (prev != null)
        {
            AppLog.Info($"[Edge] Capture released (was controlling {prev})");
            StateChanged?.Invoke();
        }
    }

    // Called when the virtual cursor exits back through the entry edge.
    void ReturnToLocalScreen()
    {
        var prevEdge = _entryEdge;
        var prevPeer = _activePeerId;
        var vx = _virtualX;
        var vy = _virtualY;
        var pw = Math.Max(1, _peerW);
        var ph = Math.Max(1, _peerH);

        _activePeerId = null;
        _entryEdge    = EntryEdge.None;
        _capture.ExitCapture();
        Interlocked.Exchange(ref _exitedAtTicks, Environment.TickCount64);

        // Place local cursor just inside the edge we came from, at the normalized position.
        int lx, ly;
        switch (prevEdge)
        {
            case EntryEdge.Right:    // entered from our right → restore just inside right
                lx = _screenW - EdgePx - 2;
                ly = (int)(vy / ph * _screenH);
                break;
            case EntryEdge.Left:     // entered from our left → restore just inside left
                lx = EdgePx + 2;
                ly = (int)(vy / ph * _screenH);
                break;
            case EntryEdge.Bottom:   // entered from our bottom → restore just inside bottom
                ly = _screenH - EdgePx - 2;
                lx = (int)(vx / pw * _screenW);
                break;
            default:                 // EntryEdge.Top → restore just inside top
                ly = EdgePx + 2;
                lx = (int)(vx / pw * _screenW);
                break;
        }
        lx = Math.Clamp(lx, 0, _screenW - 1);
        ly = Math.Clamp(ly, 0, _screenH - 1);
        User32.SetCursorPos(lx, ly);
        AppLog.Info($"[Edge] Return from {prevPeer} → local cursor at {lx},{ly}");
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _capture.EventCaptured    -= OnEvent;
        _capture.RawMouseCaptured -= OnRawMouse;
        _capture.ReleaseRequested -= OnReleaseRequested;
        ExitCapture();
    }
}
