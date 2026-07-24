using System;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
///     Direction of a WebSocket frame relative to the proxy.
/// </summary>
public enum WebSocketFrameDirection
{
    /// <summary>Client → server (must be masked on the wire).</summary>
    ClientToServer,

    /// <summary>Server → client (must not be masked on the wire).</summary>
    ServerToClient
}

/// <summary>
///     Action taken by a <see cref="SessionEventArgs.BeforeWebSocketFrame" /> handler.
/// </summary>
public enum WebSocketFrameInterceptAction
{
    /// <summary>Forward the frame (default).</summary>
    Forward,

    /// <summary>Drop the frame; do not write it to the peer.</summary>
    Drop,

    /// <summary>Replace the payload (and optionally opcode) before forwarding.</summary>
    Replace
}

/// <summary>
///     Frame-level interception context for an active WebSocket session.
/// </summary>
public class WebSocketFrameInterceptEventArgs : ProxyEventArgsBase
{
    internal WebSocketFrameInterceptEventArgs(ProxyServer server, TcpClientConnection clientConnection,
        SessionEventArgs session, WebSocketFrameDirection direction, WebSocketFrame frame)
        : base(server, clientConnection)
    {
        Session = session;
        Direction = direction;
        OpCode = frame.OpCode;
        IsFinal = frame.IsFinal;
        // Copy payload so user code can retain it safely past decoder buffer reuse.
        Data = frame.Data.ToArray();
    }

    /// <summary>
    ///     Owning HTTP session (WebSocket upgrade).
    /// </summary>
    public SessionEventArgs Session { get; }

    /// <summary>
    ///     Frame direction.
    /// </summary>
    public WebSocketFrameDirection Direction { get; }

    /// <summary>
    ///     Frame opcode.
    /// </summary>
    public WebsocketOpCode OpCode { get; set; }

    /// <summary>
    ///     FIN bit.
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    ///     Unmasked payload bytes (copied; safe to retain).
    /// </summary>
    public byte[] Data { get; set; }

    /// <summary>
    ///     Interception action. Defaults to <see cref="WebSocketFrameInterceptAction.Forward" />.
    /// </summary>
    public WebSocketFrameInterceptAction Action { get; set; } = WebSocketFrameInterceptAction.Forward;

    /// <summary>
    ///     Optional delay applied before the frame is written (Forward/Replace only).
    /// </summary>
    public TimeSpan Delay { get; set; }

    /// <summary>
    ///     Convenience: drop this frame.
    /// </summary>
    public void Drop() => Action = WebSocketFrameInterceptAction.Drop;

    /// <summary>
    ///     Convenience: replace the payload (and optionally opcode) then forward.
    /// </summary>
    public void Replace(byte[] newData, WebsocketOpCode? opCode = null)
    {
        Data = newData ?? throw new ArgumentNullException(nameof(newData));
        if (opCode.HasValue) OpCode = opCode.Value;
        Action = WebSocketFrameInterceptAction.Replace;
    }
}
