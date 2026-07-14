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

Docker is the substrate; rsync is never installed on the Windows host. Verified working:

```powershell
docker run --rm alpine:3.21 sh -c "apk add --no-cache rsync >/dev/null && rsync --version"
# rsync  version 3.4.3  protocol version 32
```

WSL/Ubuntu is a fallback. There is no `sshd` on the Windows host and nothing listening on port 22, so
**ssh-to-localhost is not an option** — an `rsync + sshd` container is required. A container running
`sshd` + `rsync` with ed25519 key auth was stood up and driven successfully (see below).

Note the version triple: a peer is routinely *newer* than us. We advertise N, rsync 3.4.3 advertises
32, and the session negotiates `min(...) = N`. That is the normal case, not an error.

## MEASURED: rsync 3.4.3 accepts old-protocol clients

Driven against a real rsync 3.4.3 in a container. Every cell byte-verified with `cmp`:

| transport | proto 27 | 29 | 30 | 31 |
|---|---|---|---|---|
| daemon pull (`rsync://`) | OK | OK | OK | OK |
| ssh pull | OK | OK | OK | OK |
| ssh push | OK | — | — | OK |

This matters enormously for scope. **Protocol 27 predates varint, `write_ndx` delta encoding,
`compat_flags`, and `negotiate_the_strings`** — all of which are protocol 30+. It is also exactly the
version that openrsync (ISC) and gokrazy/rsync (BSD-3) implement, so at 27 there *are* permissively
licensed reference implementations to port from; at 30/31 there are none, and rsync itself is GPLv3.

## Capturing golden vectors

`--debug=DELTASUM` (levels 1-4) prints the block-sizing decision and per-chunk weak checksums:

```sh
rsync --protocol=27 -a --no-whole-file --debug=deltasum4 src/blob.bin basis/blob.bin
# count=429 rem=400 blength=700 s2length=2 flength=300000
# chunk[0] offset=0 len=700 sum1=3c71f701
# chunk[1] offset=700 len=700 sum1=d80b0ca2
```

Measured for `flength=300000`: `blength=700`, `count=429`, `rem=400`, `s2length=2`.
Cross-check: `ceil(300000/700) = 429`, and `428*700 + 400 = 300000`. `sqrt(300000) ≈ 548 < 700`, so
blength floors at `BLOCK_SIZE`.

**Identical at protocol 27 and 31** — block sizing does not vary across the versions we care about, so
this work carries over regardless of the version we target.

Do not reconstruct `s2length` from memory. A hand-derivation of the `sum_sizes_sqroot` bit math gave
1 where the real rsync prints 2. Take the formula from openrsync and validate it against captured
vectors across a spread of file sizes.

## Handshake order (MEASURED — confirmed against captured bytes)

Confirmed byte-for-byte in `vectors/ssh31-pull-rt/{c2s,s2c}.bin` (rsync 3.4.3, `--protocol=31`):

| direction | bytes | meaning |
|---|---|---|
| c2s | `1f 00 00 00` | client version **31** (capped by `--protocol=31`) |
| s2c | `20 00 00 00` | server version **32** (always advertises its max) |
| s2c | `81 fe` | **compat_flags varint — server→client only** |
| c2s | `19` + `xxh128 xxh3 xxh64 md5 md4` | client checksum-choice vstring (1 len byte + ASCII) |
| s2c | `1e` + `xxh128 xxh3 xxh64 md5 md4 none` | server checksum-choice vstring |
| s2c | `3f 93 4d 6a` | **checksum_seed — LAST**, 4-byte LE int |
| both | `xx xx xx 07` … | multiplexing on; first frames follow immediately |

1. version exchange → session uses `min(local, remote)` — client with `--protocol=31` advertises 31,
   *not* its native max.
2. `compat_flags` varint — **written by the server only**, read by the client (protocol ≥ 30).
3. `negotiate_the_strings()` — checksum vstrings both ways. No compression string appears when `-z`
   is not requested. Vstring = 1 length byte (values < 0x80) + ASCII.
4. `checksum_seed` — 4-byte LE int, server→client, **after** negotiation.

Reading the seed before negotiation consumes the compat_flags varint instead, and every subsequent
read is desynced → exit 12. Multiplexing is **not** active during this prologue.

`81 fe` decodes to **510** under the `[0x80 | hi][lo]` two-byte varint form = all compat bits except
`CF_INC_RECURSE` (bit 0) — the server honored `--no-inc-recursive`, and bit 8 (`CF_ID0_NAMES` = 256,
rsync ≥ 3.2.7) exists beyond the classic 8 flags. Assert `CF_INC_RECURSE` clear post-handshake.

## Multiplex directionality (MEASURED — answers the P0-2 open question)

- **Protocol 31**: BOTH directions multiplexed. The client's very first post-vstring write is already
  a frame: `04 00 00 07` (len 4, MSG_DATA) wrapping `00 00 00 00` — the empty exclude-list terminator.
  This also confirms the **exclude-list send phase happens right after the handshake, before flist**.
- **Protocol 29**: client→server is **raw** (exclude-list int with no framing); server→client is
  multiplexed from right after the seed (`ce 00 00 07` immediately follows). At proto 29 the order is
  simply version → seed, no compat flags, no vstrings.
- The final c2s frame of a proto-31 session is MSG_DATA wrapping a single `00` byte — consistent with
  `write_ndx(NDX_DONE)` encoding NDX_DONE as one zero byte.

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
