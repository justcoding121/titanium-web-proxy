# WebSocket large messages

> Design only. The relay behavior below is what the code does today. Do not implement the chunk callback until this spec is approved.

## What the relay does today

The frame decoder does not keep RSV1–RSV3. A `permessage-deflate` opener is delivered as an ordinary data frame, and message tracking reports it uncompressed. Tests lock that in: an RSV1 text frame still yields the original payload and `isCompressed == false`.

When a subscriber filters frames, the decoder waits for the whole frame and enforces `maxWebSocketFramePayloadBytes` (close 1009). The raw relay, with no subscriber, copies bytes and does not decode frames. Hooks that filter content keep that whole-frame behavior.

`DefaultMaxWebSocketMessageBytes` is not a second enforced cap. The live cap is the per-frame budget (`webSocketFrameBudget` / `server.limits.maxWebSocketFramePayloadBytes`).

## Proposed shape (not implemented)

1. Add an optional chunk callback beside the existing whole-frame event. Subscribers that do not set it keep today's whole-frame delivery.
2. Message-level limits, separate from the frame cap: maximum reassembled message bytes, and a fragmentation bound (frames per message). Both need `Enforce` / `Observe` / `Disabled` under `webSocketFrameBudget`.
3. Decide whether `DefaultMaxWebSocketMessageBytes` becomes that message cap or is removed so one name remains.
4. `permessage-deflate` inflates per chunk only when the extension was negotiated and RSV1 is preserved. Until then, RSV stays dropped and the extension offer is still stripped when interception is on, and preserved on the raw relay.
5. Cancellation and backpressure must return pooled buffers. A chunk subscriber must not force the raw relay to buffer.

Gate before any implementation: `compare-ws-h1tls` and `compare-ws-h2`. The raw relay stays untouched.
