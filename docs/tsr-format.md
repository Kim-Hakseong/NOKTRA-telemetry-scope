# `.tsr` — Telemetry Scope recording

A capture file holds the frames exactly as they arrived, the moment each one arrived, and the
channel definition they were captured under. Nothing is decoded on the way in, so a recording can
be re-read under a corrected definition later without losing a bit.

All integers are little-endian.

## Header

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Magic `TSR1` |
| 4 | 8 | `startUnixMicros` — wall clock when the capture began |
| 12 | 4 | `definitionLength` — bytes of UTF-8 definition text |
| 16 | *n* | The channel definition file, verbatim |

The definition is embedded rather than referenced. A path would rot; a copy replays correctly ten
years later even if the file it came from has since been edited.

## Records

Repeated to the end of the file:

| Size | Field |
|---|---|
| 8 | `timestampMicros` — microseconds since `startUnixMicros` |
| 4 | `frameLength` |
| *frameLength* | The frame bytes as received |

Timestamps are relative to the start of the capture, so a wall-clock adjustment during a long run
cannot distort the intervals that replay depends on.

## Torn files

There is no index and no trailer, because both would be the parts missing from a file that ended
badly — and a capture matters most when it stopped badly. The reader consumes records until one is
incomplete, then stops and reports `Truncated`. Every complete record before the tear is returned.

A record header whose timestamp is negative or whose length is past 64 MiB is treated as torn
bytes rather than a record, so a corrupt file cannot make the reader allocate on a garbage length.

## Limits

| | |
|---|---|
| Embedded definition | 16 MiB |
| Single frame | 64 MiB |
