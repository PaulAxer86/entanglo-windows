namespace Entanglo.Core.Protocol;

public enum MessageType
{
    Hello,
    PairRequest,
    PairResponse,
    Heartbeat,
    InputEvent,
    ReleaseControl,
    ClipboardText,
    FileOffer,
    FileChunk,
    FileAck,
    UrlPush,
    ScreenshotRequest,
    ScreenshotResult,
    Error,
}
