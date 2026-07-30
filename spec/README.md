# spec/ — external standard gate

A protocol detail or standard constant may only be implemented once it is recorded here with a
citation. Anything not recorded is a bug report, not a guess: the affected code path returns an
error instead of inventing a value.

## Status

Telemetry Scope defines every wire format it owns:

| Artefact | Origin | Gate |
|---|---|---|
| Channel definition (`.yaml` subset) | This project | Self-defined — see `docs/channel-definition.md` |
| Frame recording (`.tsr`) | This project | Self-defined — see `docs/tsr-format.md` |
| Framing modes (fixed / length-field / delimiter) | This project | Self-defined, parameterised by the definition file |

The only external standard consumed is **IEEE 754 binary32 / binary64** for `f32` / `f64` field
decoding. It is not re-implemented: `BinaryPrimitives.ReadSingleBigEndian` and friends from the
.NET base class library provide it, so no constant is transcribed here.

No wire protocol constant (CRC polynomial, MIL-STD / IRIG / CCSDS header layout, sync pattern) is
hard-coded anywhere in this repository. When such support is added, the source document must be
cited in this folder first.
