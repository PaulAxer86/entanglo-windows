using Entanglo.Core.Logging;
using Entanglo.Core.Net;
using Entanglo.Core.Protocol.Payloads;
using Entanglo.PInvoke;

namespace Entanglo.Core.Input;

/// <summary>
/// Watches mouse-edge crossings and routes input events to the configured peer
/// while the cursor is in capture mode.  Escape or Scroll Lock exits capture.
///
/// Flow:
///   cursor hits screen edge → IsCapturing = true → all events forwarded to peer
///   user presses Escape/ScrollLock → IsCapturing = false → events go to local system
/// </summary>
public sealed class EdgeSwitcherService : IDisposable
{
    const int EdgePx      = 3;   // pixels from edge to trigger
    const int CooldownMs  = 600; // min ms between exit and re-enter

    readonly InputCaptureService  _capture;
    readonly ConnectionCoordinator _coordinator;
    readonly AppSettings           _settings;

    volatile string? _activePeerId;
    long _exitedAtTicks = 0;

    int _screenW;
    int _screenH;

    public string? ActivePeerId  => _activePeerId;
    public bool    IsActive      => _activePeerId != null;

    /// <summary>Fired on the hook thread when capture starts or stops.</summary>
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

        _capture.EventCaptured  += OnEvent;
        _capture.ReleaseRequested += OnReleaseRequested;
    }

    public void RefreshScreenMetrics()
    {
        _screenW = Math.Max(1, User32.GetSystemMetrics(User32.SM_CXSCREEN));
        _screenH = Math.Max(1, User32.GetSystemMetrics(User32.SM_CYSCREEN));
    }

    void OnEvent(InputEventMessage evt)
    {
        if (!_settings.EdgeConfig.Enabled) return;

        var peerId = _activePeerId;

        if (peerId == null)
        {
            // Not capturing — look for an edge crossing on every mouse-move
            if (evt.Kind == "mouseMove" && evt.X.HasValue && evt.Y.HasValue)
            {
                var elapsedMs = (double)(Environment.TickCount64 - Interlocked.Read(ref _exitedAtTicks));
                if (elapsedMs >= CooldownMs)
                    TryEnterEdge((int)evt.X.Value, (int)evt.Y.Value);
            }
        }
        else
        {
            // Capturing — forward every event to the controlled peer.
            // Fire-and-forget from the hook thread; SendInputAsync is thread-safe.
            _ = _coordinator.SendInputAsync(peerId, evt);
        }
    }

    void OnReleaseRequested() => ExitCapture();

    void TryEnterEdge(int x, int y)
    {
        var cfg = _settings.EdgeConfig;
        string? target = null;

        if      (x <= EdgePx                  && cfg.LeftPeer   != null) target = cfg.LeftPeer;
        else if (x >= _screenW - EdgePx - 1   && cfg.RightPeer  != null) target = cfg.RightPeer;
        else if (y <= EdgePx                  && cfg.TopPeer    != null) target = cfg.TopPeer;
        else if (y >= _screenH - EdgePx - 1   && cfg.BottomPeer != null) target = cfg.BottomPeer;

        if (target == null) return;

        if (!_coordinator.IsConnectedTo(target))
        {
            AppLog.Info($"[Edge] Edge peer {target} not connected — skipping switch");
            return;
        }

        AppLog.Info($"[Edge] Entering capture → peer {target} (screen edge at {x},{y})");
        _activePeerId = target;
        _capture.IsCapturing = true;
        StateChanged?.Invoke();
    }

    void ExitCapture()
    {
        var prev = _activePeerId;
        _activePeerId = null;
        _capture.IsCapturing = false;
        Interlocked.Exchange(ref _exitedAtTicks, Environment.TickCount64);
        if (prev != null)
        {
            AppLog.Info($"[Edge] Capture released (was controlling {prev})");
            StateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _capture.EventCaptured   -= OnEvent;
        _capture.ReleaseRequested -= OnReleaseRequested;
        ExitCapture();
    }
}
