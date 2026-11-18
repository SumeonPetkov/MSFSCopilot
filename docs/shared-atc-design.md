# One shared ATC: finding and proposed design

Status: **research only, nothing built.** Needs the user's go-ahead.

Live facts found later (MSFS 2024, in flight): MSFS's own AI traffic uses AI-only models titled
"Asobo PassiveAircraft A320 CEO", "… E135", "Microsoft PassiveAircraft S340B Passenger" and so on,
mostly with an empty livery.

## Finding: feasible, through MSFS 2024's own ATC panel interface

MSFS has no official API for ATC messages. But the ATC window in MSFS 2024 is plain HTML/JS, and
it ships unarchived:

`E:\SteamLibrary\steamapps\common\MSFS2024\Packages\fs-base-ingamepanels-atc\html_ui\InGamePanels\ATC\ATC.js`

The panel gets everything from the sim through one Coherent "view listener", and answers through
the same one:

| What | How (in `ATC.js`) |
|---|---|
| Connect | `RegisterViewListener("JS_LISTENER_ATC", ...)` |
| Conversation lines | event `UpdateATCDialogsData` → `Dialogs[]` with `Emitter`, `Message`, `Type` (1 = ATC, 2 = aircraft, 5 = ATIS) |
| Current prompt and reply options | event `UpdateATCPromptAndOptionsData` → `Prompt`, `Options[]` (`"1 - text"`), `OptionsActive[]`, `OptionsNoMsg[]`, `MessageBeingPlayed` |
| Everything at once (on connect) | event `UpdateATCAllData` |
| ATIS, radios, copilot | `UpdateATCATISData`, `UpdateATCDRadioData`, `UpdateCopilotState` |
| Pick a reply | `listener.trigger("ATC_SELECT_OPTION", index)` (0-based index into `Options`) |

So (a) reading the Captain's ATC messages and replies and (b) sending a chosen reply into the
Captain's ATC are both possible from JavaScript running inside the Captain's sim.

Other things checked:

- **Getting data out of the sim needs no WASM.** MSFS 2024's SimConnect (SDK 1.8.2) has CommBus:
  `SimConnect_SubscribeToCommBusEvent` / `SimConnect_CallCommBusEvent`, with JS as a broadcast
  target. A JS panel can talk to the SkyLink app directly with JSON strings.
- **Clicking a reply most likely does NOT fire `K:ATC_MENU_n`.** The button calls
  `ATC_SELECT_OPTION` on the view listener directly, not a key event. Only replies chosen with the
  keyboard or controller bindings ("ATC menu 1..0") would go through the key-event system that
  SkyLink's current ATC mirroring captures. **Not verified live** (the sim was not in a flight):
  run `skylink watch` in a flight, then click a reply, then press a number key.
- **`K:ATC_MENU_OPEN` and `K:ATC_MENU_CLOSE` don't exist in MSFS 2024.** Live, MSFS rejects both
  names (`NAME_UNRECOGNIZED`) whenever SkyLink loads the common definition.
  `ATC_MENU_1`..`ATC_MENU_0` are accepted. So today's "open/close the ATC menu" mirroring does
  nothing, and every connect logs four PROBLEM lines. Fixed: `common.yaml` now mirrors MSFS 2024's
  ATC window toggle `K:ATC` instead (accepted live).
- **How FS Copilot does it, read only for understanding.** Its package replaces Asobo's whole
  `Pages/VCockpit/Core/VCockpit.js` with a modified copy that loads its scripts. It hooks DOM
  mouse and key events of cockpit instruments and relays them over CommBus to its WASM module.
  That covers glass-cockpit gauges, not the ATC window. Replacing a core sim file is the fragile
  part: every sim update that changes `VCockpit.js` is either broken by it or breaks it.

## How fragile across sim updates

- **Medium risk.** `JS_LISTENER_ATC`, the event names, the field names and `ATC_SELECT_OPTION` are
  internal and undocumented. Asobo can rename them in any update, and the feature would then stop
  working until SkyLink is adapted (usually a few lines of JS).
- **The sim itself is never at risk.** The design below overrides no Asobo file. It adds its own
  panel, so a sim update cannot be broken by SkyLink's package. At worst the feature goes quiet.
- **Self-check.** If the hook gets no ATC data within ~10 s of the radio being on, SkyLink says
  "shared ATC is not working with this MSFS version" and falls back to today's behaviour: each
  sim's own ATC, with `ATC_MENU_n` key replies mirrored.

## Proposed design

```
 Captain's MSFS                                  First Officer's MSFS
 ┌───────────────────────────────┐               ┌─────────────────────────────────┐
 │ MSFS ATC engine               │               │ MSFS ATC engine (ignored; ATC   │
 │   ▲ ATC_SELECT_OPTION(i)      │               │ voice volume 0, panel unused)   │
 │   │ UpdateATC*Data            │               │                                 │
 │ SkyLink ATC panel (JS)        │               │ SkyLink ATC panel (JS): shows   │
 │   ▲ CommBus JSON              │               │ the Captain's lines and options │
 └───┼───────────────────────────┘               └───▲──────────────┬──────────────┘
     │ SimConnect CommBus                            │ CommBus      │ "pick 2 of prompt #17"
 SkyLink app (Captain) ◄──────── UDP, reliable ──────┴── SkyLink app (FO)
```

1. **A Community package `skylink-atc`** (installed with `skylink install-bridge`) that adds its
   own toolbar panel "SkyLink ATC". It contains only SkyLink's own files, never a copy of
   `ATC.js`. Its JS registers `JS_LISTENER_ATC`, turns `UpdateATC*Data` into small JSON messages
   (`{seq, dialogs:[{who,text,type}], prompt, options:[{n,text,active}], playing}`) and calls
   CommBus `SKYLINK_ATC_STATE`. It listens on CommBus `SKYLINK_ATC_SELECT` to call
   `ATC_SELECT_OPTION`.
2. **Captain's app:** subscribes to `SKYLINK_ATC_STATE` and sends each change to the FO as a new
   reliable message (`AtcState`). Like the traffic stream, the Captain is the only source.
3. **FO's app and panel:** show the Captain's conversation and reply buttons, in the FO's own
   SkyLink ATC panel (in-sim, VR friendly) and in the desktop app's in-flight card. Clicking sends
   `AtcSelect{promptSeq, option}` to the Captain. The Captain's app checks that the prompt is
   still current (`promptSeq`), so a stale or double click, or both pilots answering at once, is
   ignored. It then calls CommBus `SKYLINK_ATC_SELECT`. The Captain's own clicks need no relay.
4. **One conversation:** the FO never uses their own MSFS ATC. SkyLink asks the FO to turn the ATC
   voice volume down. Optionally the FO's app reads the Captain's ATC lines aloud with Windows
   speech, since the Captain's ATC voice only plays on the Captain's PC.
5. **Fallback and status:** the self-check above. The app shows "Shared ATC: on (Captain's
   ATC)", or why it is off.

### To verify live before building (read-only, or with your OK)

- Whether a custom toolbar panel's JS keeps running (and keeps receiving `JS_LISTENER_ATC`
  events) while the panel is closed. If not, the Captain keeps it open (it can be tiny), or the
  hook moves to an always-loaded page.
- Whether a second `JS_LISTENER_ATC` registration gets the same events as Asobo's panel. Other
  listeners are registered from dozens of views in the sim, so it is likely.
- Whether `ATC_SELECT_OPTION` from a non-Asobo panel is accepted.
- `skylink watch` while clicking an ATC reply, to settle the `K:ATC_MENU_n` question.

Building a custom toolbar panel needs the MSFS 2024 SDK package tool to compile its `.spb`. The
SDK is installed.
