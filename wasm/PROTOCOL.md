# SkyLink bridge protocol (v1)

`skylink.exe` talks to the SkyLinkBridge WASM module inside MSFS through two SimConnect client
data areas, each exactly **8192 bytes**:

| Area name | Written by | Read by |
|---|---|---|
| `SkyLink.Bridge.Command` | app | module (`SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET`) |
| `SkyLink.Bridge.Response` | module | app (`SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET`) |

Both sides call `CreateClientData` and ignore an "already created" exception, so either may start
first.

## Frame (both directions, little-endian)

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `0x424C4B53` ("SKLB") |
| 4 | 2 | protocol version (1) |
| 6 | 2 | record count |
| 8 | 4 | sequence number (command: new number; response: the command number it answers) |
| 12 | 4 | payload length L (bytes of records) |
| 16 | L | records |

Record: `u16 type`, `u16 length`, `length` bytes of payload. The rest of the 8192 bytes is zero.

## Reliability

- **Stop-and-wait.** The app has at most one command frame in flight and resends it every 400 ms
  until a response with the same sequence number arrives.
- **Dedup.** The module remembers the last sequence it processed and its response. A repeat of
  that sequence gets the cached response re-sent; it is **not executed again**, so a retransmitted
  RPN never runs twice.
- The app starts its sequence numbers at a random non-zero value, so a relaunched app never
  collides with the module's cached sequence.
- **One response record per command record, in the same order.** A command that fails gets an
  `ERROR` record in its slot.

## Records

| Type | Direction | Payload |
|---|---|---|
| `0x01 HELLO` | app → module | `u16 version` |
| `0x02 RPN` | app → module | `u32 id`, UTF-8 calculator code (no NUL) |
| `0x03 LIST_LVARS` | app → module | `u32 start index` |
| `0x81 HELLO_ACK` | module → app | `u16 version`, UTF-8 build string |
| `0x82 RPN_RESULT` | module → app | `u32 id`, `u8 ok`, `f64 value` |
| `0x83 LVAR_LIST` | module → app | `u32 next index`, `u8 done`, NUL-terminated UTF-8 names (as many as fit) |
| `0x84 ERROR` | module → app | `u16 command type`, UTF-8 message |

`LIST_LVARS` walks `get_name_of_named_variable(i)` from the start index until it returns null
(then `done = 1`) or the response would exceed the frame.
