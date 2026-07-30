# Channel definition format

A channel definition is one file that says how a byte stream is cut into frames and what numbers
live inside a frame. The same file drives live reception, recording and replay.

It is written in a small, deliberately boring subset of YAML: block mappings, block sequences,
plain and quoted scalars, `#` comments. Anchors, flow collections, multi-line scalars and implicit
typing are not accepted — those are the parts that make a definition hard to review.

## Skeleton

```yaml
name: Vehicle telemetry

source:                 # optional; a definition is usable for replay with no source at all
  type: udp             # udp | serial | none
  host: 0.0.0.0
  port: 5005

framing:
  mode: lengthField     # fixed | lengthField | delimiter
  headerLength: 2
  lengthOffset: 1
  lengthSize: 1
  adjust: 2
  maxFrameLength: 4096

channels:
  - name: Airspeed
    offset: 2
    type: s16
    endian: big
    a: 0.1              # value = a x raw + b
    b: 0
    unit: m/s
    min: 0              # valid range, in engineering units
    max: 300
```

## `framing`

| Key | Modes | Meaning |
|---|---|---|
| `mode` | all | `fixed`, `lengthField` or `delimiter` |
| `maxFrameLength` | all | Abandon a frame that grows past this. Default 65536 |
| `frameLength` | fixed | Exact bytes per frame |
| `headerLength` | lengthField | Bytes that must arrive before the length can be read |
| `lengthOffset` | lengthField | Byte offset of the length field |
| `lengthSize` | lengthField | Width of the length field, 1–4 bytes |
| `lengthEndian` | lengthField | `big` (default) or `little` |
| `adjust` | lengthField | Added to the encoded length to get the total frame size |
| `delimiter` | delimiter | Terminator, as hex bytes (`0D 0A`) or literal text |
| `keepDelimiter` | delimiter | Keep the terminator in the emitted frame. Default false |

`adjust` exists because encodings disagree about what the length counts — its own header, a
trailing checksum, both, neither. The definition states it; the code never assumes it.

## `channels`

| Key | Default | Meaning |
|---|---|---|
| `name` | required | Unique within the file, case-insensitive |
| `offset` | required | Byte offset from the start of the frame. Decimal or `0x` hex |
| `type` | required | `u8 s8 u16 s16 u32 s32 u64 s64 f32 f64` and the usual aliases |
| `endian` | `big` | `big` or `little` |
| `a` (alias `scale`) | 1 | `value = a x raw + b` |
| `b` (alias `bias`) | 0 | |
| `unit` | empty | Free text, shown next to the value |
| `min` / `max` | unset | Valid range in engineering units. A reading outside it is flagged, never clipped |

A field that reaches past the end of a frame is reported as *missing* for that frame — a normal
condition on a variable-length stream, not an error.

## Errors

Reading a definition either produces a fully validated channel set or throws with the offending
line number. There is no partial load and no silently defaulted byte order.
