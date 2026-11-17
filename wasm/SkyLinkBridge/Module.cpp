// SkyLinkBridge — standalone WASM module for MSFS 2024.
//
// Does only what external SimConnect cannot:
//   * executes calculator (RPN) code, which also covers H: events and multi-parameter K: events;
//   * lists the L-vars that actually exist, so misspelled names in definitions are caught
//     (external SimConnect silently creates any L-var it is asked for).
// Protocol: wasm/PROTOCOL.md.

#include <MSFS/MSFS.h>
#include <MSFS/MSFS_WindowsTypes.h>
#include <MSFS/Legacy/gauges.h>
#include <SimConnect.h>

#include <stdio.h>
#include <string.h>

#include "Protocol.h"

using namespace skylink;

namespace {

constexpr const char* kCommandArea = "SkyLink.Bridge.Command";
constexpr const char* kResponseArea = "SkyLink.Bridge.Response";
constexpr const char* kBuild = "SkyLinkBridge 0.3.0";

enum : SIMCONNECT_CLIENT_DATA_ID { kCommandAreaId = 0x534B0001, kResponseAreaId = 0x534B0002 };
enum : SIMCONNECT_CLIENT_DATA_DEFINITION_ID { kCommandDef = 1, kResponseDef = 2 };
enum : SIMCONNECT_DATA_REQUEST_ID { kCommandRequest = 1 };

HANDLE g_sim = 0;
uint32_t g_lastSeq = 0;
bool g_haveLast = false;
uint8_t g_response[kFrameSize];
char g_code[kFrameSize + 1];

void Send() {
  SimConnect_SetClientData(g_sim, kResponseAreaId, kResponseDef, SIMCONNECT_CLIENT_DATA_SET_FLAG_DEFAULT, 0,
                           kFrameSize, g_response);
}

void WriteError(FrameWriter& out, uint16_t forType, const char* message) {
  int len = static_cast<int>(strlen(message));
  if (len > out.Remaining() - 2) len = out.Remaining() - 2;
  uint8_t* p = out.Begin(kError, 2 + (len > 0 ? len : 0));
  if (!p) return;
  WriteU16(p, forType);
  if (len > 0) memcpy(p + 2, message, len);
}

void HandleHello(FrameWriter& out) {
  int len = static_cast<int>(strlen(kBuild));
  uint8_t* p = out.Begin(kHelloAck, 2 + len);
  if (!p) return;
  WriteU16(p, kVersion);
  memcpy(p + 2, kBuild, len);
}

void HandleRpn(FrameWriter& out, const uint8_t* payload, int length) {
  if (length < 4) {
    WriteError(out, kRpn, "short RPN command");
    return;
  }
  uint32_t id = ReadU32(payload);
  int codeLen = length - 4;
  memcpy(g_code, payload + 4, codeLen);
  g_code[codeLen] = 0;

  FLOAT64 f = 0;
  SINT32 i = 0;
  PCSTRINGZ s = nullptr;
  BOOL ok = execute_calculator_code(g_code, &f, &i, &s);

  uint8_t* p = out.Begin(kRpnResult, 13);
  if (!p) return;
  WriteU32(p, id);
  p[4] = ok ? 1 : 0;
  WriteF64(p + 5, f);
}

void HandleListLVars(FrameWriter& out, const uint8_t* payload, int length) {
  if (length < 4) {
    WriteError(out, kListLVars, "short LIST_LVARS command");
    return;
  }
  uint32_t index = ReadU32(payload);
  // Reserve the whole remaining space, fill names, then shrink by rewriting the length.
  int capacity = out.Remaining();
  if (capacity < 5) return;
  uint8_t* p = out.Begin(kLVarList, capacity);
  int used = 5;
  bool done = false;
  for (;;) {
    PCSTRINGZ name = get_name_of_named_variable(static_cast<ID>(index));
    if (name == nullptr) {
      done = true;
      break;
    }
    int n = static_cast<int>(strlen(name)) + 1;
    if (used + n > capacity) break;
    memcpy(p + used, name, n);
    used += n;
    ++index;
  }
  WriteU32(p, index);
  p[4] = done ? 1 : 0;
  // Shrink the record to what was used. FrameWriter's internal offset includes the full
  // reservation, so rebuild the record header and let Finish() use the corrected length.
  WriteU16(p - 2, static_cast<uint16_t>(used));
  out.Shrink(capacity - used);
}

void HandleCommand(const uint8_t* frame) {
  if (ReadU32(frame) != kMagic || ReadU16(frame + 4) != kVersion) return;
  uint16_t count = ReadU16(frame + 6);
  uint32_t seq = ReadU32(frame + 8);
  uint32_t length = ReadU32(frame + 12);
  if (length > kFrameSize - kHeaderSize) return;

  // A retransmission of the last command: answer again, never execute twice.
  if (g_haveLast && seq == g_lastSeq) {
    Send();
    return;
  }

  FrameWriter out(g_response);
  const uint8_t* body = frame + kHeaderSize;
  uint32_t off = 0;
  for (uint16_t r = 0; r < count; ++r) {
    if (off + kRecordHeaderSize > length) break;
    uint16_t type = ReadU16(body + off);
    uint16_t len = ReadU16(body + off + 2);
    if (off + kRecordHeaderSize + len > length) break;
    const uint8_t* payload = body + off + kRecordHeaderSize;
    switch (type) {
      case kHello: HandleHello(out); break;
      case kRpn: HandleRpn(out, payload, len); break;
      case kListLVars: HandleListLVars(out, payload, len); break;
      default: WriteError(out, type, "unknown command"); break;
    }
    off += kRecordHeaderSize + len;
  }
  out.Finish(seq);
  g_lastSeq = seq;
  g_haveLast = true;
  Send();
}

void CALLBACK Dispatch(SIMCONNECT_RECV* data, DWORD size, void*) {
  if (data->dwID != SIMCONNECT_RECV_ID_CLIENT_DATA) return;
  auto* cd = static_cast<SIMCONNECT_RECV_CLIENT_DATA*>(data);
  if (cd->dwRequestID != kCommandRequest) return;
  if (size < sizeof(SIMCONNECT_RECV_CLIENT_DATA) - sizeof(DWORD) + kFrameSize) return;
  HandleCommand(reinterpret_cast<const uint8_t*>(&cd->dwData));
}

}  // namespace

extern "C" MSFS_CALLBACK void module_init(void) {
  if (FAILED(SimConnect_Open(&g_sim, "SkyLinkBridge", nullptr, 0, 0, 0))) {
    fprintf(stderr, "[SkyLinkBridge] SimConnect_Open failed\n");
    return;
  }
  SimConnect_MapClientDataNameToID(g_sim, kCommandArea, kCommandAreaId);
  SimConnect_MapClientDataNameToID(g_sim, kResponseArea, kResponseAreaId);
  // Either side may create the areas first; "already created" is harmless.
  SimConnect_CreateClientData(g_sim, kCommandAreaId, kFrameSize, SIMCONNECT_CREATE_CLIENT_DATA_FLAG_DEFAULT);
  SimConnect_CreateClientData(g_sim, kResponseAreaId, kFrameSize, SIMCONNECT_CREATE_CLIENT_DATA_FLAG_DEFAULT);
  SimConnect_AddToClientDataDefinition(g_sim, kCommandDef, 0, kFrameSize, 0, SIMCONNECT_UNUSED);
  SimConnect_AddToClientDataDefinition(g_sim, kResponseDef, 0, kFrameSize, 0, SIMCONNECT_UNUSED);
  SimConnect_RequestClientData(g_sim, kCommandAreaId, kCommandRequest, kCommandDef,
                               SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET, SIMCONNECT_CLIENT_DATA_REQUEST_FLAG_DEFAULT, 0, 0, 0);
  SimConnect_CallDispatch(g_sim, Dispatch, nullptr);
  fprintf(stderr, "[SkyLinkBridge] %s ready\n", kBuild);
}

extern "C" MSFS_CALLBACK void module_deinit(void) {
  if (g_sim) SimConnect_Close(g_sim);
  g_sim = 0;
}
