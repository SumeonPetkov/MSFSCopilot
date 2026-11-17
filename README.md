# SkyLink

Shared cockpit for Microsoft Flight Simulator 2024, built so that the two cockpits **cannot stay
out of sync**. Clean-room design; see `C:\Users\Simeon\.claude\plans\i-need-you-to-distributed-scott.md`
for the full plan.

## Status

| Phase | State |
|---|---|
| 0. Toolchain | .NET 8 SDK installed. Still needed: Visual Studio 2022 Build Tools (C++), MSFS 2024 SDK |
| 1. Core sync engine + chaos harness | **Done**: 10,000 randomized hostile-network sessions pass |
| 2. SimConnect adapter + UDP transport + C172 definition | **Done.** Verified live against MSFS 2024: reads, plus remote writes (K-events) applied and read back in about 70 ms |
| 3. In-sim bridge module (RPN/H-events, L-var name checks) | App side, protocol and installer **done and tested**, channel verified live in MSFS with a stand-in module. The C++ module is written but **not yet compiled**: that needs the MSFS 2024 SDK |
| 4. Flight model slaving + control handover in the sim | |
| 5. Var Recorder, A320neo/A321 definitions | |
| 6. WPF UI, diagnostics, release | |

## How sync works (short version)

- **Replicated store, no skip counters.** Every variable is `{value, hybrid-logical-clock stamp}`.
  An applied remote value is recognised when it echoes back *by value*, so nothing can get stuck
  the way FS Copilot's skip counters do.
- **Sync classes.** `state` (switches, last-writer-wins), `analog` (knobs: streamed, then a reliable
  final value), `event` (button presses: exactly once, in order, survive disconnects),
  `control` (flight controls from the pilot flying), `derived` (only hashed), `local` (never sent).
- **Anti-entropy.** Every second each seat sends per-group hashes. A group that differs twice in a
  row is repaired with a snapshot, so any loss, bug or reconnect heals within about 3 s.
- **Verified writes.** Every remote write is read back and retried a bounded number of times. A
  switch the sim refuses is marked *Rejected* in Sync Health. It is never silently dropped and
  never starts a ping-pong.
- **Host is the truth on join.** The joiner's cold-and-dark cockpit is a "baseline" that can never
  override the host. Initial sync applies groups in order (power before avionics).
- **Control handover** is request → grant → commit, plus a priority takeover. Authority is
  `(epoch, seat)`, carried in every digest, so both seats always agree on exactly one pilot flying.

## Flying with it (CLI, until the app in phase 6)

Both pilots load the **same aircraft**. The host makes a code and shares an address:

```bash
skylink code
```

```bash
skylink host --code K7PM2X
```

The other pilot joins. With Tailscale or ZeroTier use the host's 100.x / VPN address; without a
VPN the host must forward UDP 47800:

```bash
skylink join 100.64.12.34 --code K7PM2X
```

Keys while running: `r` request controls, `t` take controls, `g` give controls, `q` quit.

Other commands:

```bash
skylink probe
```

```bash
skylink lint definitions
```

`skylink probe` shows which definition matched the loaded aircraft, the current value of every
variable, every name MSFS rejected, and whether the bridge module is running. `skylink lint`
validates the definition files.

### The in-sim bridge module

Some cockpit controls can only be driven from inside the sim: calculator code (RPN), H: events
(G1000 bezels, many airliner buttons), multi-parameter K: events. The module also lists the
L-vars that actually exist. External SimConnect can't do that, and it silently creates any
L-var it's asked for, so a misspelled name in a definition would never sync and never complain.
With the module installed, SkyLink holds back every L-var until the module confirms it exists,
then reports the ones that never appear.

1. Install the MSFS 2024 SDK (in MSFS: Options > General > Developers, turn on Developer Mode,
   then Help > SDK Installer).
2. Build the module:

   ```bash
   powershell -ExecutionPolicy Bypass -File wasm/build.ps1
   ```

3. Install it into MSFS's Community folder, then restart MSFS:

   ```bash
   skylink install-bridge
   ```

`install-bridge` finds the Community folder through MSFS's `UserCfg.opt` (Steam or Microsoft
Store). It refuses to install an unbuilt package and never touches a `skylink-bridge` folder it
didn't create. Use `--dry-run` to see what it would do. The protocol is in `wasm/PROTOCOL.md`.
Without the module everything else still works, and SkyLink says what it can't check.

SimConnect.dll is looked up in `SKYLINK_SIMCONNECT_DLL`, next to `skylink.exe`, and in the MSFS
2024 SDK (`MSFS2024_SDK`).

## Aircraft definitions

YAML files in `definitions/`. `_base/common.yaml` holds the standard switches, radios and autopilot.
`_base/generic.yaml` is the fallback for unknown aircraft, and it always comes with a warning.
Aircraft files `extends: common` and add, override or `remove: true` entries:

```yaml
- id: battery
  class: state                                   # state | analog | event | derived | control | local
  read: "A:ELECTRICAL MASTER BATTERY:1, Bool"     # A:name, unit | L:name | B:input_event
  write: { kevent: TOGGLE_MASTER_BATTERY, mode: toggle }
- id: ap_master_press
  class: event
  capture: { kevent: AP_MASTER }                  # replayed exactly once on the other side
```

Setters: `simvar`, `K:EVENT`, `{ kevent, param }`, `{ bevent }`, `{ inc, dec, step }`,
`H:EVENT` / `{ hevent }` and `{ rpn: "{value} (>L:SOMETHING)" }`. The last two need the bridge
module; `{value}` is replaced by the parameter. A list gives an alternate setter for retries. Parameter
expressions use `value`, `+ - * /`, `round floor ceil abs bcd16 clamp16k`. Unknown keys, bad names,
duplicates and missing setters are errors, and a definition with any error never loads.

## Layout

```
src/SkyLink.Core/          engine: Hlc, Schema, Messages, MessageCodec, SyncEngine (pure, no I/O)
src/SkyLink.Definitions/   YAML loader, inheritance, linter, aircraft matching, param expressions
src/SkyLink.Net/           LiteNetLib UDP link: session-code auth, HMAC, reconnect
src/SkyLink.Sim/           SimConnect bridge (direct P/Invoke), fake cockpit, shared setter logic,
                           bridge-module protocol client, Community-folder installer
wasm/                      SkyLinkBridge C++ module, build script, PROTOCOL.md
package/skylink-bridge/    MSFS package skeleton (manifest; the build adds the .wasm and layout.json)
src/SkyLink.Session/       the single-threaded session loop
src/SkyLink.Cli/           skylink.exe
definitions/               aircraft definitions
tests/SkyLink.Core.Tests/        unit tests for the FS Copilot failure modes, codec, clock
tests/SkyLink.Chaos.Tests/       FakeSim + ChaosLink + randomized two-pilot scenarios
tests/SkyLink.Integration.Tests/ definitions, two full sessions over lossy UDP, SDK header check, live sim (opt-in)
```

## Build and test

```bash
dotnet test
```

The default is a quick run of 300 chaos sessions. The full gate is 10,000:

```bash
SKYLINK_CHAOS_RUNS=10000 dotnet test
```

To trace one failing seed with its event log:

```bash
SKYLINK_DEBUG_SEED=1234 dotnet test tests/SkyLink.Chaos.Tests --filter DebugSeed --logger "console;verbosity=detailed"
```

`SKYLINK_HEAL_MS` changes how long the network gets to heal before convergence is checked (default
3000 ms, the plan's gate).

With MSFS running, this checks that a fake joiner exactly mirrors the live aircraft. It is
read-only: nothing is written into the sim.

```bash
SKYLINK_LIVE=1 dotnet test tests/SkyLink.Integration.Tests --filter LiveSim
```

This one **writes into the sim**. It flips the taxi light and moves the heading bug through the
full sync path, verifies both, then puts them back. If the sync path can't restore them, a
direct write does.

```bash
SKYLINK_LIVE_WRITE=1 dotnet test tests/SkyLink.Integration.Tests --filter LiveWrite
```
