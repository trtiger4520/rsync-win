---
name: wire-decode
description: Decode a captured rsync wire stream (c2s.bin/s2c.bin) by hand — prologue layout, mux frames, ndx/iflags/varint cheatsheet. Use when analyzing a new capture or diagnosing a desync/hang.
---

# Decode a captured rsync byte stream

Authoritative layouts live in `docs/codec-spec.md`, `docs/flist-spec.md`, `docs/transfer-spec.md`;
verified facts and traps in `docs/wire-notes.md`. This is the working cheatsheet.

## Prologue (before multiplexing, proto 31 over ssh)

- c2s: `int32 LE version` (e.g. `1f 00 00 00` = 31) → vstring checksum offer (1 length byte + ASCII, e.g. `19` + `xxh128 xxh3 xxh64 md5 md4`) → everything after is muxed.
- s2c: `int32 LE version` → compat_flags **varint** → negotiation strings → `int32 checksum_seed` **last** → muxed. (Reading the seed early consumes compat_flags and desyncs everything.)

## Mux frames

Header = 4 bytes LE: low 24 bits payload length, high byte = tag; tag 7 = `MSG_DATA`
(MPLEX_BASE). `07` data, `08` error-xfer, `09` info… A **zero-length MSG_DATA is a
keep-alive, not EOF**. Demux = concatenate MSG_DATA payloads → the logical stream.

## Logical stream cheat facts (proto 31 pull, generator→sender)

- filter list: `int32 0` = empty
- per requested file: `write_ndx` (delta-from-previous encoding, first file often a single
  byte; `0xFE` escape, separate positive/negative state) + `int16 LE iflags` + sum head
  (4× int32: count/blength/s2length/remainder; all-zero = whole-file request)
- iflags: `0x8000 ITEM_TRANSFER`, `+0x0004 REPORT_SIZE` (size differs), `+0x0008 REPORT_TIME`
  (mtime differs). Up-to-date files: **zero bytes on the wire** (verified `ssh31-pull-uptodate`)
- `NDX_DONE` = single byte `0x00` (NOT `write_int(-1)` on proto ≥ 30). Full pull choreography:
  client DONE#1 (end phase 0) … #2#3#4 burst … goodbye #5
- rsync varint is **not LEB128** — length indicator in the first byte

## Workflow

```bash
xxd test-fixtures/vectors/<name>/c2s.bin | head -40
```

Walk frames by hand for short streams. For long ones, replay through the pure core
(`ScriptedTransport` + the relevant reader) instead of eyeballing. Cross-check any decoded
claim against `client-cmd.txt` / `argv.txt` / `server-stderr.txt` in the same vector dir.
For a semantic cross-check, run the same scenario with `-i --dry-run` in the container.
