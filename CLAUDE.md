# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A Windows rsync **client** in C# targeting .NET 10. It implements the rsync wire protocol so Windows
machines can sync with real rsync servers without WSL or Cygwin.

Scope: **pull and push**; transport over **ssh.exe** and the **rsync daemon** (`rsync://`); CLI only.

**Start here every session:** `docs/roadmap.md` is the living plan — phase status with commit
evidence, what to do next, and the mandatory per-phase working method (spec → capture → validate →
implement → hermetic tests → live interop → adversarial review → commit). `AGENTS.md` is the
model-agnostic entry point mirroring the reading order for non-Claude agents.

## Documentation upkeep (mandatory)

The docs are how a context-limited session avoids re-deriving byte-level facts. Update them **in
the same commit** as the change they describe:

- Phase progress, re-scoping, new/finished tasks → status table + checkboxes in `docs/roadmap.md`
- New or corrected protocol facts, resolved/deferred open questions → `docs/wire-notes.md`
- Byte-layout changes → the relevant `docs/*-spec.md`
- New standing rules or traps → this file

## Build & Run

```powershell
dotnet build

dotnet test                                   # everything
dotnet test --filter "Category!=Interop"      # fast, hermetic (no rsync binary needed)
dotnet test --filter "Category=Interop"       # live interop against a real rsync

dotnet test --filter "FullyQualifiedName~TestClassName.MethodName"

dotnet run --project src/RsyncWin.Cli
```

## Architecture

A **pure protocol core** with all I/O pushed to the edges. This is the load-bearing decision: the
byte-exact formulas can be golden-vector tested offline, without a network or an rsync binary.

| Project | Role |
|---|---|
| `src/RsyncWin.Protocol` | THE pure core: `Wire`, `Mux`, `Session`, `Checksums`, `FileList`, `Delta`. **No `Process`, no `Socket`, no filesystem.** |
| `src/RsyncWin.Transport` | `IRsyncTransport` seam → `OpenSshProcessTransport`, `DaemonTcpTransport` |
| `src/RsyncWin.Fs` | All Windows/NTFS reality: `\\?\` paths, name sanitization, attribute policy |
| `src/RsyncWin.Engine` | Orchestrates generator/receiver (pull) and sender (push) over Channels |
| `src/RsyncWin.Cli` | Thin shell. Returns rsync's numeric exit codes verbatim. |

Never add an I/O dependency to `RsyncWin.Protocol`. Role engines must never branch on a raw protocol
version int — they read the immutable `SessionContext` behind the `IProtocolDialect` seam.

See `docs/wire-notes.md` for provenance, verification status, and the full trap list. Read it before
touching any codec.

## Licensing / provenance rule

Canonical rsync ([RsyncProject/rsync](https://github.com/RsyncProject/rsync), formerly `WayneD/rsync`)
is **GPLv3** — read it for **behavior only**.

- Scalar protocol constants are *facts* and may be recorded.
- Anything larger than a scalar is *expression*. The `int_byte_extra[64]` varint table and the
  checksum tables must be taken from a permissive reference — [openrsync](https://github.com/kristapsdz/openrsync)
  (ISC/BSD) or [gokrazy/rsync](https://github.com/gokrazy/rsync) (BSD-3) — or regenerated from the
  documented algorithm. Never copied from `rsync.h`.

## Key Technical Constraints

- **.NET 10 / C# 14** — modern language features encouraged (primary constructors, collection expressions).
- **Protocol 31** is what we implement, negotiating down to **30/29**. The session uses
  `min(local, remote)`. A peer is routinely *newer* than us — stock rsync 3.4.3 advertises
  **protocol 32** — so the ceiling on a peer's claim (`MaxProtocolVersion`) is a sanity bound (40),
  not the version we implement. Do not conflate the two.
- **No WSL dependency** — everything runs natively on Windows.
- **SSH transport** shells out to the in-box `C:\Windows\System32\OpenSSH\ssh.exe`. Use only
  `Process.StandardInput.BaseStream` / `StandardOutput.BaseStream`; the `StreamReader`/`StreamWriter`
  text wrappers corrupt the binary stream. Pump stdin/stdout/stderr on **three concurrent loops** or a
  large stderr burst deadlocks both sides.
- **Dependency-light**: only `System.IO.Hashing` and `System.CommandLine`. MD4 is hand-ported (not in
  the BCL); MD5 is in-box. No SSH.NET, no BouncyCastle.

## rsync Protocol Notes

These are the facts that bite. A wrong value here does not fail loudly — it produces a **silent full
resend or an exit-12 desync**.

- **Handshake order** (verified against `compat.c`): version `min()` → **`compat_flags` varint** →
  **`negotiate_the_strings()`** → **`checksum_seed` LAST**. The seed comes *after* negotiation.
  Reading it earlier consumes the compat_flags varint and desyncs everything downstream.
- **Roles**: for a *pull*, the **server is the sender**; we are the receiver/generator. The generator
  must actively request each file (`write_ndx` + a sum header, `count=0` for a full transfer, + the
  `iflags` word) before any data arrives.
- **Block size**: `blength` from `sum_sizes_sqroot` is rounded to a **multiple of 8** — *not* a power
  of two — floored at 700 and capped at `MAX_BLOCK_SIZE` (131072).
- **Checksums**: the weak checksum is a rolling Adler-32 variant. The strong checksum is MD4 (legacy),
  MD5, or a negotiated xxHash. Per-block strong sums are **truncated to `s2length`**; the whole-file
  sum uses the **full** length and may use a **different negotiated algorithm** (`file_sum_nni` ≠
  `xfer_sum_nni`).
- **Checksum seed**: `seed == 0` short-circuits all seed mixing. MD5 prepends vs appends the seed
  depending on `CF_CHKSUM_SEED_FIX`. xxHash uses `withSeed(seed)` for blocks but `reset(0)` for the
  whole-file sum. This asymmetry is easy to get wrong.
- **`write_ndx`/`read_ndx` is its own encoding** (delta-from-previous, `0xFE` escape, `0x80` high bit,
  separate positive/negative state). `NDX_DONE` is **not** `write_int(-1)` on protocol ≥ 30.
- **rsync's varint is not LEB128** — the length indicator is in the first byte.
- **Multiplexing is asymmetric**: `io_start_multiplex_in` and `_out` are independent, per-version
  decisions, and it is off during the handshake. A zero-length `MSG_DATA` frame is a **keep-alive, not
  EOF**.
- **A whole-file checksum mismatch triggers a redo** in a later phase — that is *why* there are
  multiple phases and multiple `NDX_DONE` markers.
- **File-list order is ordinal byte order.** Any culture-aware or case-insensitive compare desyncs the
  positional index against the server. (`InvariantGlobalization` is on.)
- **`System.IO.Hashing` writes xxHash bytes big-endian**; rsync is little-endian. Go through
  `HashToUInt64` + `BinaryPrimitives`. Never `memcpy` the hash span.
- **Exit codes**: map Windows filesystem failures to **11** (file I/O), not 12 (protocol stream).
  `ssh.exe` exiting 255 means SSH itself failed → report 5 and surface its stderr.
- `--checksum` skips the mtime+size fast path and always does a full block comparison.

## Testing

Two tiers. Correctness is gated by the first, which needs no network:

1. **Hermetic golden vectors** (`RsyncWin.Protocol.Tests`) — bytes captured from a real rsync
   (`--debug=deltasum2`, `--debug=deltasum4`, `--debug=proto`) checked into `test-fixtures/` and
   replayed against the pure core.
2. **Live interop** (`RsyncWin.Interop.Tests`, `[Trait("Category","Interop")]`) — against a real rsync
   over ssh-to-localhost, or pinned Testcontainers images.

Phase gates are byte/hash equality or "re-run transfers nothing" — never "looks right". Every interop
test needs a **hang-detection timeout**; phase-boundary bugs manifest as hangs, not failures.
