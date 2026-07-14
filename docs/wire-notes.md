# Wire notes

Living record of the load-bearing byte-level facts, their provenance, and their verification status.
A wrong value here does not fail loudly — it produces a silent full-resend or an exit-12 desync. Treat
every entry as unverified until a captured-byte test pins it.

## Licensing / provenance rule

Canonical rsync ([RsyncProject/rsync](https://github.com/RsyncProject/rsync), formerly `WayneD/rsync`)
is **GPLv3**. We read it for **behavior only**.

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
| Protocol version negotiation (min of both sides) | **measured** | captured: we say 31, peer says 32, session runs 31 |
| Handshake **order** | **measured** | captured bytes, see below |
| `MessageTag.Data` = 0 (header byte `07`) | **measured** | frame headers throughout every capture |
| Compat flag (`CF_*`) bit positions | **consistent with capture** | `81 fe` = 510 = all-except-INC_RECURSE fits the documented bits |
| varint / varlong / ndx / vstring codecs | **spec'd + pinned** | `docs/codec-spec.md`; compat varint, flist mtime bytes, generator request all decode from captures |
| `ITEM_*` iflags | **spec'd, partially pinned** | 0x0008 and 0xA000 observed in captured requests |
| `XMIT_*` file-list flags | **capture-pinned** | `docs/flist-spec.md` §1; sightings 0x19/0x18/0x3a/0xba/0x2019 across four captures |
| flist entry layout (proto 30/31, varint xflags) | **capture-pinned** | every byte of four captured flists decoded (`flist-spec.md`); 30 and 31 byte-identical for our compat set |
| flist end: `varint(0)` + `varint(io_error)` + id0-name tail | **capture-pinned** | `00 00` in all captures; `-a` adds `00 04 root` ×2 (CF_ID0_NAMES) |
| `f_name_cmp` sort (band rule + virtual trailing slash) | **live-pinned** | two discriminating `--list-only` experiments + 23-entry line-for-line match |
| list-only choreography (proto 31 DONE/stats/goodbye) | **live-verified** | `ListOnlySession` runs a real rsync to **exit 0** (P3 interop gate) |
| Block sizing (`sum_sizes_sqroot`) | **measured** | 19-point `--debug=deltasum2` sweep; all reproduced |
| Rolling weak checksum | **measured** | every `--debug=deltasum4` chunk sum reproduced; signed-char pinned |
| MD4 | **verified** | RFC 1320 suite + openssl legacy provider |
| Seeded strong-sum variants (MD5/xxh64 block vs whole-file) | **implemented vs spec vectors** | final live pin lands with the P4 delta transfer |
| Sum head codec + validation | **implemented** | measured 300000 head + spec vectors; null head observed in captures |
| NDX_DONE phase choreography | **capture-pinned** | `docs/transfer-spec.md` §5: c2s @31 = #1, #2#3#4 burst, goodbye #5; s2c = echo#1, echo#2+final#3, stats+goodbye#4 (@30: 4/3, no goodbye) |
| Token stream (plain LE int32; literal/ref/end) | **capture-pinned** | every token of three captures decoded; delta capture pins the remainder rule on the last block |
| Whole-file trailer (xfer_sum_len, always present) | **capture-pinned** | every trailer recomputed as XXH128 seed-0 (low64 LE ∥ high64 LE); zero-length files included |
| Sender reply shape (ndx + iflags echo + verbatim sum-head echo) | **capture-pinned** | `transfer-spec.md` §1; generator golden test writes byte-identical requests |
| Stats block field order (sender-read FIRST) | **measured** | magnitudes unambiguous across three captures; spec's written-first claim corrected |
| Handshake runner (client side) | **live-verified** | replays all three captured prologues byte-exact AND negotiates 31/29 against a live rsync 3.4.3 over ssh.exe (P2 interop tests) |
| Server argv (`server_options()`) | **golden-pinned** | `ServerArgvBuilder` reproduces all 7 captured `argv.txt` files word-for-word |
| compat_flags letter→bit mapping | **source-verified** | compat.c behavior read (see "client_info" below); consistent with captured 510 |

**P1 hard gate: CLOSED.** Every pure codec (varint/varlong/ndx/vstring/mux header/rolling/MD4/
seeded strong sums/block sizing/sum head) is implemented and gated by spec vectors and/or captured
bytes — 146 hermetic tests. What remains before P4 can transfer a file is assembly, not codecs.

**P2 CLOSED.** `HandshakeRunner` + `SessionContext` + `ChecksumNegotiator` + `ServerArgvBuilder`
(pure core) and `OpenSshProcessTransport` (three concurrent pump paths, `BaseStream` only) are
implemented. Hermetic tests replay the captured 29/30/31 prologues byte-exact and assert the reader
stops on the first mux byte; live interop tests negotiate against a real rsync 3.4.3 in an
sshd container and pin the ssh-255-on-auth-failure path.

**P3 CLOSED.** `MultiplexReader`/`MultiplexWriter` (frame boundaries carry no semantics — pinned by
reading the captured stream in 7-byte pulls), `FileListReader` + `FileNameComparer` (spec'd by
workflow into `docs/flist-spec.md`, validated byte-exact against four captures), and
`ListOnlySession` (measured proto-31 choreography: filter terminator → flist → DONE, DONE×3 →
3×DONE + 5×varlong(3) stats + goodbye → final goodbye). Live gate: a full `--list-only` session
against rsync 3.4.3 ends with the remote side exiting **0**. End-of-run tails, for P4: c2s carries
5 NDX_DONEs at 31 (4 at 30); s2c: 3 DONEs + stats + (31 only) goodbye DONE.

**P4 CLOSED — THE interop milestone.** Transfer phase spec'd and byte-validated into
`docs/transfer-spec.md` (three captures, both directions, every byte accounted; all trailers
recomputed as xxh128 seed-0). `PullSession` (generator requests + receiver with temp-file writes,
whole-file verification, one-retry redo list, measured DONE choreography), `FileReceiver`,
`Token`, `ItemFlags`, async ndx reads, xxh128 whole-file sums. Hermetic gates: the full captured
pull replays through `PullSession` (7 files, SHA-256-pinned, trailers genuinely verified) and our
generator's demuxed request stream is byte-identical to the captured client's. Live gate: pulling
a whole tree (UTF-8 + space names) over ssh.exe is SHA-256-identical to the container and the
remote rsync exits **0**. Still uncaptured: `--delete` del-stats, induced-mismatch redo,
MSG_NO_SEND/MSG_IO_ERROR traffic (transfer-spec §10).

Post-review hardening (adversarial review, three confirmed findings): `LocalPath` rejects
Windows-only escapes the Unix-centric flist validation cannot see (`\` separators, drive/ADS
colons, plus a resolved-path containment check) and every name is mapped before the first
filesystem touch; read-only destination files are replaced (attribute cleared before the move,
matching rsync's contract) with the temp file cleaned on every path and finalize failures demoted
to the per-file retry-then-exit-23 lane; wire mtimes clamp to the settable Win32 FileTime range
(1601-01-01T00:00:01Z … 9999-12-31T23:59:59Z) instead of throwing out of the session.

## Handshake gating rules (source-verified against compat.c, behavior only)

- The server derives compat_flags from the **capability letters** we send after `-e.` in the server
  argv (`client_info`): `i`→INC_RECURSE, `f`→SAFE_FLIST, `x`→AVOID_XATTR_OPTIM, `C`→CHKSUM_SEED_FIX,
  `I`→INPLACE_PARTIAL_DIR, `v`→VARINT_FLIST_FLAGS, `u`→ID0_NAMES. Exception: bits 1–2
  (SYMLINK_TIMES, SYMLINK_ICONV) reflect the **server build's** capabilities, not our `L`/`s`
  letters — so never pin the full compat value against a live server, only the bits we depend on.
- **`CF_VARINT_FLIST_FLAGS` does double duty**: it is also the "peer can negotiate strings" signal
  (`do_negotiated_strings`). The client must read compat_flags **before** sending its checksum
  vstring; when the bit is absent nothing is written or read, and the fallback is **md5 at 30/31,
  md4 at 29**.
- **The client's list order picks the winner** ("first name in the client's list that the server
  also lists" — both sides compute it identically). With our single-name offer (`md5`) the rule is
  unobservable; pin with `--debug=nstr` before offering more than one name (P4, xxh64).
- A failed negotiation is **exit 4** (`RERR_UNSUPPORTED`), not 2 and not 12.
- The `-e.<letters>` block is emitted only when the client's **pre-negotiation** protocol is ≥ 30 —
  that is why a `--protocol=29` run shows plain `-tr` in the captured argv.
- The compat varint is wire-compatible with the old `write_byte` form for values < 0x80, so a
  pre-3.2 server's single compat byte decodes fine through the varint reader.
- **Daemon nuance for P8**: on `rsync://` sockets there are NO binary version ints — the version was
  already negotiated via the textual `@RSYNCD: <ver>.<sub>` greeting, and `setup_protocol` skips the
  binary exchange (`remote_protocol` already set). The rest of the prologue is unchanged.

## Checksum negotiation (measured, rsync 3.4.3)

- Client offer: `xxh128 xxh3 xxh64 md5 md4`; server reply: same + ` none`. **No sha1/sha256** in a
  stock build's transfer-checksum negotiation (SHA appears only in openssl-enabled builds'
  `--checksum-choice` and daemon auth). First common entry wins.
- **Never advertise `md4` at protocol 30/31.** Stock rsync's OpenSSL-EVP path *prepends* the block
  seed while its builtin path *appends* it — two stock builds can disagree with each other. MD4 is
  strictly the proto-29 fallback (builtin append path, `CSUM_MD4_OLD` semantics: whole-file sum
  prepends the seed even when it is 0).
- Our v1 offer: `md5` (BCL) now, `xxh64` (System.IO.Hashing, LE emission via `HashToUInt64` +
  `BinaryPrimitives`) once golden-vectored in P4.

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
  desyncs immediately. *Resolved: measured directionality lives in `SessionContext`
  (in: 29/30/31 framed; out: 30+ framed, 29 raw).*
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

- ~~Exact `write_varint`/`write_varlong` byte math~~ — resolved (P1, `codec-spec.md` §2–§3)
- ~~Multiplex on/off, per direction and per version~~ — resolved (measured; `SessionContext`)
- ~~Exact `server_options()` bundled short-flag set~~ — resolved (7 argv goldens + compat.c read)
- ~~Double-`NDX_DONE` at a protocol-31 phase boundary~~ — resolved (P4 capture; full DONE map in
  `transfer-spec.md`)
- `--secluded-args` posture for remote paths with spaces/metacharacters — P5 (ssh passes the argv
  through the remote shell, which splits on whitespace)
- ~~Phase-0 writes every request before reading any reply~~ — resolved (P5): the request writer and
  reply reader now run as two concurrent loops per phase, handed off through a bounded
  `System.Threading.Channels.Channel<int>` of requested ndx values and joined with `Task.WhenAll`,
  removing the old fill-both-pipe-buffers deadlock risk on a very large flist. A fault in either
  loop cancels a linked token so the other side cannot hang forever waiting on the channel.
- ~~Fast path (mtime+size) for already up-to-date files~~ — resolved (P5, captures
  `ssh31-pull-uptodate` / `ssh31-pull-partial`): a regular file whose local size and mtime already
  match the flist entry produces **zero** generator bytes — no ndx, no iflags, no sum head. This
  extends to directories too: a directory that already exists with the entry's exact mtime also
  gets no itemize echo (the up-to-date capture's whole c2s stream, dirs included, is just the
  empty filter-list int32 plus the five `NDX_DONE` bytes). A file that exists but differs gets
  `ITEM_TRANSFER` plus `ITEM_REPORT_SIZE` iff the size differs and/or `ITEM_REPORT_TIME` iff the
  mtime differs — never `ITEM_IS_NEW`, which stays reserved for files missing locally. Observed
  values: b001 (size **and** mtime differ) → `0x800C`; b002 (same size, older mtime) → `0x8008`.
- ~~Temp names were the deterministic `<final>.rsyncwin-tmp`~~ — resolved (P5): randomized per
  receive as `.<final>.<8-hex>.rsyncwin-tmp`, alongside `WindowsPathMapper` name sanitization
- Checksum-negotiation winner rule observed live with a multi-name offer (`--debug=nstr`) — P4,
  before advertising xxh64
- Daemon `@RSYNCD` auth digest specifics for a modern `rsyncd` — P8 (note: no binary version ints
  on daemon sockets, see the gating-rules section)
