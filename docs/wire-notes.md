# Wire notes

Living record of the load-bearing byte-level facts, their provenance, and their verification status.
A wrong value here does not fail loudly — it produces a silent full-resend or an exit-12 desync. Treat
every entry as unverified until a captured-byte test pins it.

## Licensing / provenance rule

Canonical rsync (`WayneD/rsync`) is **GPLv3**. We read it for **behavior only**.

- **Scalar protocol constants are facts** (a value a peer will send us) and may be recorded here.
- **Anything larger than a scalar is expression.** The `int_byte_extra[64]` varint table and the
  checksum tables must come from a permissive reference, or be regenerated from the documented
  algorithm — never copied from `rsync.h`.

Permissive references to port from:

| Project | License | Protocol | Why |
|---|---|---|---|
| [openrsync](https://github.com/kristapsdz/openrsync) | ISC/BSD | 27 | Clean-room, small readable files that map 1:1 to sender/receiver/generator |
| [gokrazy/rsync](https://github.com/gokrazy/rsync) | BSD-3 | modern | Has real-rsync interop tests; good modern reference |

Record the source of every ported table in a comment next to it.

## Verification status

`[VERIFY]` in the source means: not yet checked against a permissive reference or a captured trace.
**P1 is a hard gate** — no codec may depend on a `[VERIFY]` value until it is pinned.

| Item | Status | How to pin |
|---|---|---|
| Peer may advertise protocol **32** | **measured** | `rsync 3.4.3` (alpine:3.21) prints `protocol version 32` |
| Protocol version negotiation (min of both sides) | unverified | capture handshake, `rsync --debug=proto` |
| Handshake **order** | **corrected, unverified** | see below — capture and diff |
| `MessageTag` numeric values | unverified | captured trace |
| Compat flag (`CF_*`) bit positions | unverified | openrsync / captured trace |
| `XMIT_*` file-list flags | **not yet recorded** | openrsync `flist.c` |
| `ITEM_*` iflags | **not yet recorded** | openrsync |
| `int_byte_extra[64]` varint table | **not yet recorded** | must come from openrsync/gokrazy (see rule above) |
| Block sizing (`sum_sizes_sqroot`) | unverified | `rsync --debug=deltasum2` across file sizes |
| Rolling + strong checksums | unverified | `rsync --debug=deltasum4` |

## Interop substrate (this machine)

Docker is the substrate. Verified working:

```powershell
docker run --rm alpine:3.21 sh -c "apk add --no-cache rsync >/dev/null && rsync --version"
# rsync  version 3.4.3  protocol version 32
```

WSL/Ubuntu is available as a fallback. There is no `sshd` on the Windows host and nothing listening on
port 22, so **ssh-to-localhost is not currently an option** — P2 needs an `rsync + sshd` container with
port 22 published. Pin the image so protocol behavior does not drift under us.

Note the version triple: we implement **31**, the peer claims **32**, and the session negotiates
`min(...) = 31`. A newer peer is the normal case, not an error.

## Handshake order (corrected)

The obvious-looking order is **wrong**. Verified against rsync's `compat.c`, `setup_protocol()` runs:

1. version exchange → session uses `min(local, remote)`
2. **`compat_flags`** (varint) — protocol ≥ 30
3. **`negotiate_the_strings()`** — vstring exchange for checksum / compression / auth
4. **`checksum_seed`** (4-byte int) — written **LAST**

Reading the seed before negotiation consumes the first bytes of the compat_flags varint instead, and
every subsequent read is desynced → exit 12 on the very first milestone.

Multiplexing is **not** active during this prologue.

## Traps worth re-reading before touching the relevant layer

- **Multiplexing is asymmetric.** `io_start_multiplex_in` and `io_start_multiplex_out` are
  independent, per-version decisions. Framing our outbound data when the server does not expect it
  desyncs immediately. *Open question — pin by live capture in P2, before any transfer code.*
- **`write_ndx`/`read_ndx` is its own encoding**: delta-from-previous byte reduction, `0xFE` escape,
  `0x80` high-bit, separate positive/negative running state. `NDX_DONE` is **not** `write_int(-1)`
  on protocol ≥ 30.
- **rsync's varint is not LEB128.** Length indicator lives in the first byte; the `int_byte_extra`
  table drives the decode.
- **Block length is rounded to a multiple of 8**, not a power of two. Per-block strong checksums are
  **truncated** to `s2length`; the whole-file checksum uses the full length and may use a *different*
  negotiated algorithm (`file_sum_nni` ≠ `xfer_sum_nni`).
- **Checksum seed:** `seed == 0` short-circuits all seed mixing. MD5 prepends vs appends the seed
  depending on `CF_CHKSUM_SEED_FIX`. xxHash uses `withSeed(seed)` for blocks but `reset(0)` for the
  whole-file sum. This asymmetry is surprising and easy to get wrong.
- **`System.IO.Hashing` writes xxHash bytes big-endian**; rsync is little-endian. Always go through
  `HashToUInt64` + `BinaryPrimitives` LE writes. Never `memcpy` the hash span.
- **A zero-length `MSG_DATA` frame is a keep-alive, not EOF.**
- **A whole-file checksum mismatch triggers a redo** in a later phase. That is *why* there are
  multiple phases and multiple `NDX_DONE` markers. Not optional.
- **File-list order is ordinal byte order.** Any culture-aware or case-insensitive compare desyncs
  the positional index against the server.
- **ssh child process:** use `BaseStream` only; pump stdin/stdout/stderr on three concurrent loops or
  a large stderr burst deadlocks both sides.

## Open questions (resolve by live capture, in the phase that needs them)

- Exact `write_varint`/`write_varlong` byte math, including `min_bytes` handling — P1
- Multiplex on/off, per direction and per version — P2, before P4
- Double-`NDX_DONE` at a protocol-31 phase boundary — P4
- Exact `server_options()` bundled short-flag set, and the `--secluded-args` posture for paths with
  spaces — P2 (capture a real remote argv with an `--rsync-path` wrapper that logs `"$@"`)
- Daemon `@RSYNCD` auth digest specifics for a modern `rsyncd` — P8
