// SkyLink bridge protocol v1 — must stay byte-identical with src/SkyLink.Sim/BridgeProtocol.cs.
// See wasm/PROTOCOL.md.
#pragma once

#include <stdint.h>
#include <string.h>

namespace skylink {

constexpr uint32_t kMagic = 0x424C4B53u;  // "SKLB"
constexpr uint16_t kVersion = 1;
constexpr int kFrameSize = 8192;
constexpr int kHeaderSize = 16;
constexpr int kRecordHeaderSize = 4;

enum RecordType : uint16_t {
  kHello = 0x01,
  kRpn = 0x02,
  kListLVars = 0x03,
  kHelloAck = 0x81,
  kRpnResult = 0x82,
  kLVarList = 0x83,
  kError = 0x84,
};

// WASM is little-endian, so plain memcpy matches the wire format.
inline uint16_t ReadU16(const uint8_t* p) { uint16_t v; memcpy(&v, p, 2); return v; }
inline uint32_t ReadU32(const uint8_t* p) { uint32_t v; memcpy(&v, p, 4); return v; }
inline void WriteU16(uint8_t* p, uint16_t v) { memcpy(p, &v, 2); }
inline void WriteU32(uint8_t* p, uint32_t v) { memcpy(p, &v, 4); }
inline void WriteF64(uint8_t* p, double v) { memcpy(p, &v, 8); }

// Builds one response frame in place.
class FrameWriter {
 public:
  explicit FrameWriter(uint8_t* frame) : frame_(frame) { memset(frame_, 0, kFrameSize); }

  int Remaining() const { return kFrameSize - off_ - kRecordHeaderSize; }

  // Reserves a record and returns its payload pointer, or nullptr if it does not fit.
  uint8_t* Begin(RecordType type, int length) {
    if (length < 0 || length > Remaining()) return nullptr;
    WriteU16(frame_ + off_, type);
    WriteU16(frame_ + off_ + 2, static_cast<uint16_t>(length));
    uint8_t* payload = frame_ + off_ + kRecordHeaderSize;
    off_ += kRecordHeaderSize + length;
    ++count_;
    return payload;
  }

  // Gives back the unused tail of the last record reserved with Begin().
  void Shrink(int bytes) { off_ -= bytes; }

  void Finish(uint32_t seq) {
    WriteU32(frame_, kMagic);
    WriteU16(frame_ + 4, kVersion);
    WriteU16(frame_ + 6, count_);
    WriteU32(frame_ + 8, seq);
    WriteU32(frame_ + 12, static_cast<uint32_t>(off_ - kHeaderSize));
  }

 private:
  uint8_t* frame_;
  int off_ = kHeaderSize;
  uint16_t count_ = 0;
};

}  // namespace skylink
