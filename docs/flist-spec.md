All facts are pinned — source behavior read, permissive cross-checks done (openrsync proto-27 flist, gokrazy receiver), and both local captures decoded byte-exact against the derived layout. The complete spec follows.

---

# rsync file-list (flist) wire spec — protocol 30/31, compat_flags = 510

Scope: our client feature set — pull and push, `-t -r` or `-logDtpr`, `--no-inc-recursive`, never `-H`, no `--numeric-ids`, no iconv, no ACL/xattr, no `--atimes`/`--crtimes`. Compat bits assumed: `CF_INC_RECURSE` **clear**, `CF_SAFE_FLIST`(8) / `CF_CHKSUM_SEED_FIX`(0x20) / `CF_VARINT_FLIST_FLAGS`(0x80) / `CF_ID0_NAMES`(0x100) **set** (= the captured `81 fe` → 510).

Verification: every claim marked **MEASURED** was decoded byte-for-byte from `test-fixtures/vectors/ssh31-pull-rt/s2c.bin` (`-tre.LsfxCIvu`, 9 entries) and `test-fixtures/vectors/ssh31-pull-a/s2c.bin` (`-logDtpre.LsfxCIvu`), demuxed and parsed with an independent decoder built only from this spec. Everything else is canonical-rsync behavior (read for behavior only, GPLv3, expressed in own words), cross-checked against openrsync `flist.c` (ISC) and gokrazy/rsync `internal/receiver/flist.go` (BSD-3), which pin the proto-27 base layout.

---

## 1. XMIT_* flag bits

PROVENANCE: values = wire facts from `rsync.h`; applicability = canonical-behavior (`flist.c send_file_entry`); "seen" column = MEASURED from our captures.

| bit | name | protocols | can appear for us? |
|---|---|---|---|
| 1<<0 | `XMIT_TOP_DIR` | all | yes — transfer-root dirs, incl. `.` (seen: `0x19`, `0x01`) |
| 1<<1 | `XMIT_SAME_MODE` | all | yes (seen: `0x3a`) |
| 1<<2 | `XMIT_SAME_RDEV_pre28` | 20–27 | never (proto floor 29) |
| 1<<2 | `XMIT_EXTENDED_FLAGS` | 28+ | only as the **zero-substitute** (see §2 step 0) |
| 1<<3 | `XMIT_SAME_UID` | all | yes — **always set when `-o` absent** (seen everywhere in `-tr`) |
| 1<<4 | `XMIT_SAME_GID` | all | yes — always set when `-g` absent |
| 1<<5 | `XMIT_SAME_NAME` | all | yes (seen: `0x3a`, `0xba`) |
| 1<<6 | `XMIT_LONG_NAME` | all | yes, names > 255 bytes |
| 1<<7 | `XMIT_SAME_TIME` | all | yes (seen: `0xba` arriving as varint `80 ba`) |
| 1<<8 | `XMIT_SAME_RDEV_MAJOR` | 28+, **devices only** | with `-D` on char/block devices; also forced on FIFOs/sockets at proto **30** (not 31) |
| 1<<8 | `XMIT_NO_CONTENT_DIR` | 30+, **dirs only** | possible on dirs sent without content (`-d`, implied dirs); with plain `-r` every dir has content → not seen |
| 1<<9 | `XMIT_HLINKED` | 28+, non-dirs | never (`-H` never sent) — but see §2 step 3 for the parse rule |
| 1<<10 | `XMIT_SAME_DEV_pre30` | 28–29 | never at 30/31 |
| 1<<10 | `XMIT_USER_NAME_FOLLOWS` | 30+ | **never**: sender sets it only when `inc_recurse && !numeric_ids`; we force inc-recurse off. Names travel in the trailing id-list instead (§5) |
| 1<<11 | `XMIT_RDEV_MINOR_8_pre30` | 28–29 | never at 30/31 |
| 1<<11 | `XMIT_GROUP_NAME_FOLLOWS` | 30+ | never (same reason as bit 10) |
| 1<<12 | `XMIT_HLINK_FIRST` | 30+, HLINKED only | never |
| 1<<12 | `XMIT_IO_ERROR_ENDLIST` | 31 (also 30 w/ `f` compat), only with `XMIT_EXTENDED_FLAGS` | **never in varint-flags mode** — the end-of-list io_error varint replaces it (§4) |
| 1<<13 | `XMIT_MOD_NSEC` | 31+ | **yes** — set whenever the sender's stat has nonzero in-range `st_mtim.tv_nsec`; NOT option-gated. Must be handled on every proto-31 pull (our fixture tree has whole-second mtimes, so not in captures) |
| 1<<14 | `XMIT_SAME_ATIME` | `--atimes` only | never |
| 1<<15 | `XMIT_UNUSED_15` | unused | never |
| 1<<16 | `XMIT_RESERVED_16` | reserved (fileflags) | never |
| 1<<17 | `XMIT_CRTIME_EQ_MTIME` | `--crtimes` only | never |

Same-bit reuse (2, 8, 10, 11, 12) is disambiguated by protocol version and by entry type (dir vs device vs non-dir) — a decoder must branch on the decoded `mode` type for bit 8.

## 2. Field-by-field decode of one entry (proto 30/31, `CF_VARINT_FLIST_FLAGS` on)

PROVENANCE: canonical-behavior (`send_file_entry`/`recv_file_entry`); base field order cross-checked against openrsync `flist_recv` and gokrazy receiver (proto 27 — identical order, older encodings); steps 0–2, 4–7, 9 and the `-o -g` uid/gid path MEASURED.

Two distinct kinds of conditionality — get this wrong and you desync:

- **Option-skipped**: field bytes absent from the wire entirely, whatever the flags say — uid (`-o`), gid (`-g`), rdev (`-D`; specials only < 31), symlink target (`-l`), atime (`--atimes`), crtime (`--crtimes`), hardlink data (`-H`), trailing flist checksum (`--checksum`). Both ends derive the same option set from the `--server` argv we send, so the parser must be built per-session from those options.
- **Flag-elided**: field position exists but bytes are omitted when a `SAME_*` bit is set — name prefix, mtime, mode, uid/gid values, rdev major.

Decode order:

0. **xflags** = `read_varint`. `0` = end of list (§4). A sender writes `XMIT_EXTENDED_FLAGS` (4) instead of a genuinely all-zero flag word; bit 2 carries no meaning at 30/31 in varint mode — ignore it. (Non-varint fallback, for completeness: one byte; if `XMIT_EXTENDED_FLAGS` set, a second high byte follows → 16-bit LE shortint.)
1. **l1** = 1 byte, iff `XMIT_SAME_NAME`: count of leading bytes shared with the previous entry's name (sender caps at 255; `SAME_NAME` iff l1 > 0).
2. **l2**: varint iff `XMIT_LONG_NAME` (set iff l2 > 255), else 1 byte. Then **l2 raw name bytes** (no NUL). Full name = first l1 bytes of previous name + these. Canonical recv aborts if `l1 + l2 >= MAXPATHLEN` (4096), rejects `..` components and (non-relative) leading `/` with exit 4 (`RERR_UNSUPPORTED`).
3. **hardlink shortcut** (never for us; parse rule is flags-driven, not option-driven): iff proto ≥ 30 and `HLINKED` set and `HLINK_FIRST` clear → varint `first_hlink_ndx`; if it references the current list, the **entry ends here** (no further fields); a cross-list reference (inc-recurse only) continues with full fields. `HLINK_FIRST` entries carry no varint and continue normally.
4. **size** = `read_varlong(min_bytes=3)`. MEASURED: 4096 → `00 00 10`, 300000 → `04 e0 93`. Negative → abort (exit 4). Dirs carry their stat size.
5. **mtime** = `read_varlong(min_bytes=4)` iff `!XMIT_SAME_TIME`. Signed; 9-byte form legal (pre-1970). MEASURED: 1577934245 → `5e a5 5d 0d`.
6. **mtime nsec** = varint iff `XMIT_MOD_NSEC` (proto 31). Range 0..999999999; a 3.4.3 peer enforces the bound (exit 2).
7. **mode** = 4-byte **LE int32** iff `!XMIT_SAME_MODE`. MEASURED: `ed 41 00 00` = 0o40755. Wire type bits are classic Unix `S_IFMT` = 0170000: **reg 0100000, dir 0040000, symlink 0120000**, chr 0020000, blk 0060000, fifo 0010000, sock 0140000. (Wire-format remap exists only for platforms whose native `S_IFLNK` differs.) 3.4.x peers reject any other type value (exit 2), with mode 0 allowed only under `--delete-missing-args`.
8. *(atime varlong(4), crtime varlong(4) — option-skipped for us; order when present: nsec → crtime → mode → atime)*
9. **uid**, iff `preserve_uid` (`-o`) and `!XMIT_SAME_UID`: **varint**; iff `XMIT_USER_NAME_FOLLOWS`: 1 length byte + name bytes (inc-recurse only — never for us). MEASURED (`-a`): entry #0 uid varint `00`.
10. **gid**: same shape, gated on `-g` / `XMIT_SAME_GID` / `GROUP_NAME_FOLLOWS`. MEASURED: `00`.
11. **rdev**, iff (`preserve_devices` and mode is chr/blk) or (`preserve_specials` and fifo/sock and proto < 31): major = varint iff `!XMIT_SAME_RDEV_MAJOR`; minor = **varint always** (at 30/31). At proto 31 FIFOs/sockets carry **no rdev bytes at all**; at proto 30 they carry a minor varint (sender forces `SAME_RDEV_MAJOR`, minor 0). Receiver forces file length to 0 for devices (after reading the size field).
12. **symlink target**, iff `preserve_links` (`-l`) and mode is symlink: §6.
13. *(proto < 30 hardlink dev/ino longints — never at 30/31)*
14. **flist checksum**, iff `--checksum`: `xfer_sum_len` raw bytes — the full digest of the
    negotiated transfer checksum, **UNSEEDED** (byte-identical to the file's whole-file transfer
    trailer, §3 of `transfer-spec.md`), **regular files only** (absent on dirs/symlinks/devices),
    and the entry's **LAST** field. **VERIFIED (P9, `ssh31-pull-checksum`)**: 16-byte xxh128
    emitted low64-LE ∥ high64-LE at the negotiated length; our own client offers md5 (also 16).
    `FileListReader` reads it when `FileListOptions.Checksum` is set, using
    `ChecksumLength = StrongChecksum.DigestLength(negotiated)`; the generator compares it against a
    freshly computed whole-file sum of the local basis to decide the transfer (see
    `transfer-spec.md` §4b).

Annotated capture bytes (ssh31-pull-rt, first + third entries):
```
19 01 2e 00 00 10 5e a5 5d 0d ed 41 00 00
= xflags 0x19 (TOP_DIR|SAME_UID|SAME_GID), l2=1, ".", size 4096,
  mtime 1577934245, mode 0o40755

3a 03 15 "5 name with space.txt" 00 0b 00 63 ff cc b0
= xflags 0x3a (SAME_MODE|SAME_UID|SAME_GID|SAME_NAME), l1=3 ("b00"+…),
  l2=0x15, size 11, mtime 1672531199 (0x63B0CCFF)
```

Varlong byte layout, stated explicitly because the example above is exactly the misparse it
prevents: the wire form is a leading control byte holding the HIGH-order value bits (masked by
`& ((1 << (8 - extra)) - 1)` when extra > 0), followed by `min_bytes - 1 + extra` LOW-order bytes
little-endian (LSB first). `63 ff cc b0` is NOT the LE int 0xb0ccff63 — the header `63` carries
the top bits: value 0x63B0CCFF = 1672531199. Verified against 20+ magnitudes across all captures
(4096 = `00 00 10`, 300000 = `04 e0 93`, 1099511627775 = `e0 ff ff ff ff ff`).

## 3. First-entry special cases

PROVENANCE: canonical-behavior (statics in `send_file_entry`/`recv_file_entry`); MEASURED for `.`/TOP_DIR and the uid/gid guard.

- Prev-state seeding, identical on both sides: prev-name = empty, prev-mtime = 0, prev-mode = 0, prev-uid = prev-gid = 0, prev-rdev-major = 0. State is per-process and persists across the whole list (and across lists, in inc-recurse — moot for us).
- `XMIT_SAME_NAME` can't occur on entry #0 (empty prev-name ⇒ l1 = 0).
- `XMIT_SAME_UID`/`XMIT_SAME_GID` have an **extra first-entry guard**: the sender sets them only if the prev-name is non-empty. So with `-o -g` the first entry always carries explicit uid+gid (MEASURED: `-a` entry #0 = `01 … 00 00` uid 0, gid 0). Without `-o`/`-g` the bits are set unconditionally, first entry included (MEASURED: `0x19`).
- `XMIT_SAME_TIME`/`XMIT_SAME_MODE` have **no** such guard: a first entry whose mtime is exactly 0 legitimately arrives with `SAME_TIME` and decodes against the zero seed.
- `.`: the transfer root is sent as literal name `.` with `XMIT_TOP_DIR`. Every source-arg dir gets TOP_DIR; under `-r` all sent dirs have content, so `XMIT_NO_CONTENT_DIR` does not appear; the combination TOP_DIR|NO_CONTENT_DIR marks an *implied* dir (e.g. `--relative` ancestors).
- Wire arrival order is the sender's readdir order, **not** sorted (MEASURED: `b002, b005, b001, b000, b004, b003`), depth-first with a directory's files before its subdirs' contents. Never assume sorted input; order is irrelevant because both sides sort afterwards (§7).

## 4. End of list

PROVENANCE: canonical-behavior (`write_end_of_flist`, `recv_file_list`, `compat.c`); the `00 00` tail MEASURED in both captures.

With `CF_VARINT_FLIST_FLAGS` (our case), end-of-list is **always exactly two varints**:

```
varint(0)          -- the xflags-position 0 that terminates the loop
varint(io_error)   -- 0 unless the sender hit read errors
```

That is the answer to the "multiple 0x00 bytes" observation: `00 00` (MEASURED). The io_error varint's **presence** is gated purely on `CF_VARINT_FLIST_FLAGS` — not on CF_SAFE_FLIST and not on protocol. Whether it may carry a **nonzero** value is gated on safe-flist semantics, and `use_safe_inc_flist = CF_SAFE_FLIST || protocol >= 31` — at 31 it is forced on regardless of the compat bit. Receiver: OR the value into local io_error (unless `--ignore-errors`); nonzero means the sender's list may be incomplete → suppress deletions, final exit 23/24 semantics.

Non-varint fallback (only if the bit weren't negotiated): a single `00` byte; an io_error end is instead the 2-byte shortint `XMIT_EXTENDED_FLAGS|XMIT_IO_ERROR_ENDLIST` (0x1004, bytes `04 10`) + varint io_error, legal only under safe-flist. Proto < 30: io_error is a plain int32 **after** the id lists; proto 30 non-safe: via `MSG_IO_ERROR` message instead.

After the two varints, in a non-inc-recurse run, in order:
1. **id lists** (§5) — only the lists whose option (`-o`/`-g`) is active; nothing at all for `-tr` (MEASURED: transfer bytes follow immediately).
2. Nothing else. **No `NDX_FLIST_EOF`**: that marker (and `NDX_FLIST_OFFSET`-relative dir indexes) exists only in inc-recurse mode. With `--no-inc-recursive` the transfer phase begins directly — first generator request `ndx 0` (MEASURED: `01 08 00` = ndx 0, iflags 0x0008 follows the flist in s2c as the sender's echo).

## 5. uid/gid varints and the CF_ID0_NAMES id-list

PROVENANCE: canonical-behavior (`uidlist.c`); id0 tail MEASURED (`-a` capture).

- In-entry values (§2 steps 9–10): plain rsync varints of the numeric id.
- After end-of-list, the sender transmits an id→name mapping (non-inc, `numeric_ids` off): first the uid list iff `preserve_uid`, then the gid list iff `preserve_gid`. Each list is a sequence of
  ```
  varint(id != 0)  byte(len<=255)  <len name bytes>
  ```
  one per distinct id seen in the flist (id 0 excluded from the loop), terminated by `varint(0)`. With **CF_ID0_NAMES** negotiated, the terminating `varint(0)` is itself followed by `byte(len) + name` — the name for id 0 (a chrooted daemon may map root away). MEASURED: `00 04 'root' 00 04 'root'` — all files root-owned, so each list is just the id-0 terminator entry.
- Without CF_ID0_NAMES the terminator is the bare `varint(0)`.
- The receiver then remaps flist uids/gids by name (unless `--numeric-ids`). In-entry `*_NAME_FOLLOWS` never coexists with this tail in practice: names-in-entries are the inc-recurse mechanism, the trailing list is the whole-file-list mechanism.

## 6. Symlink target encoding

PROVENANCE: canonical-behavior; cross-checked openrsync/gokrazy (proto 27 uses int32 len + bytes; 30/31 upgrades len to varint). Not capture-pinned (fixture tree has no links) — pin during P3 interop.

Iff `-l` was in effect and the entry mode is `S_IFLNK`:
```
varint(len)  <len raw target bytes>     -- no NUL on the wire
```
This is **not** a vstring (no high-bit two-byte length form) — it is a plain varint length. Canonical recv aborts on `len + 1 > MAXPATHLEN` or negative; len 0 is accepted by canonical rsync (empty target), rejected by openrsync — accept, mirroring canonical. Placement: after gid/rdev, last field of the entry (absent `--checksum`).

## 7. The receiver-side sort (`f_name_cmp`) — positional ndx is derived from THIS

PROVENANCE: canonical-behavior (`f_name_cmp`, `flist_sort_and_clean`); unsorted wire order MEASURED; sorted-order consequences consistent with the captured generator requests (ndx 0 = `.`).

Both sides sort their own copy **after** the list is fully transmitted; every `ndx` on the wire afterwards is the entry's **position in the sorted array** (base `ndx_start` = 0 for non-inc 30/31; MEASURED: first request ndx 0 = `.`). Rules at proto ≥ 29:

1. **Byte-wise unsigned** comparison (`uchar`) over the joined relative path (`dirname` + `/` + `basename`). Ordinal — no locale, no case folding, no UTF awareness.
2. **`.` sorts first.** The entry whose full name is exactly `.` compares before everything (implemented as: a dir named `.` is reclassified as an empty non-directory string).
3. **Within any given parent directory: all non-dirs before all dirs.** Whenever the comparison reaches a segment boundary where one side is a non-dir basename and the other is a dir (or continues deeper into a path), the non-dir sorts first regardless of name bytes. Consequence at top level: `[".", files…, dirs+subtrees…]`.
4. **Dirs compare as if suffixed with `/` (0x2F)**, which places a directory immediately before its own contents, and — trap — **changes relative order vs naive strcmp**: among dirs, `foo-bar` (0x2D < 0x2F) and its whole subtree sort **before** `foo`, where naive strcmp says `foo` < `foo-bar`. Any byte < 0x2F in the divergence position triggers this (`!"#$%&'()*+,-.`, space, controls).
5. Non-dirs compare with end-of-string-first semantics among themselves (prefix sorts first, plain strcmp).
6. A dir and a non-dir with the same joined name are **never equal** (rules 3/4 separate them). Equal names of the same kind are duplicates: the receiver keeps the first (dir dupes: keep dir over non-dir, merge flags) and **clears the loser in place — array slots are never compacted**, so indices stay aligned with the sender, who applies the same comparator to his copy. (Duplicates only arise with overlapping source args.)
7. Recursively, each subtree repeats rules 3–5 within its directory.

Practical effect for `-r` pulls: `.` = ndx 0; then top-level files in byte order; then each top-level dir followed by its subtree. For the captured tree: `.`(0), `b000_empty`(1), `b001_small.txt`(2), `b002_64k.bin`(3), `b003_300k.bin`(4), `b004_中文檔名.txt`(5), `b005 name with space.txt`(6), `subdir`(7), `subdir/nested.txt`(8).

Implementation note: implement as a comparator over (dirname, basename, is_dir) with the virtual trailing slash and the non-dir-first band rule; golden-test against a captured `--debug=flist3` index dump before trusting it — an off-by-one here is the classic silent-full-resend. `--relative` leading-slash stripping happens *after* sorting (out of scope for v1).

## 8. Message-channel facts for the demuxer

PROVENANCE: tag numbers = wire facts (`rsync.h` enum msgcode); payloads/behavior = canonical-behavior (`io.c read_a_msg`, `cleanup.c`); MSG_DATA framing and keep-alive facts MEASURED previously (wire-notes).

Wire tag byte = 7 (`MPLEX_BASE`) + code; header = 4-byte LE, low 24 bits payload length.

| code | name | payload | client relevance |
|---|---|---|---|
| 0 | MSG_DATA | logical-stream bytes; **len 0 = keep-alive, not EOF** | always |
| 1 | MSG_ERROR_XFER | raw text | print to stderr; flags exit-23 semantics |
| 2 | MSG_INFO | raw text | print to stdout |
| 3 | MSG_ERROR | raw text | stderr; exit-23 semantics |
| 4 | MSG_WARNING | raw text | stderr |
| 5/8/6/7 | ERROR_SOCKET/UTF8/LOG/CLIENT | text | internal generator/daemon plumbing; canonical client receiver treats on-wire arrival as invalid → exit 12 |
| 9 | MSG_REDO | 4B LE ndx | internal (receiver→generator pipe); never on our wire |
| 10 | MSG_STATS | 8B raw int64 | internal; never on our wire |
| 22 | MSG_IO_ERROR | exactly 4B LE int32 flags (GENERAL=1, VANISHED=2, DEL_LIMIT=4) | OR into local io_error; drives exit 23 vs 24 and delete-safety |
| 33 | MSG_IO_TIMEOUT | exactly 4B LE int32 seconds | daemon→client; adopt if smaller than current timeout |
| 42 | MSG_NOOP | len 0 | proto-30-style keep-alive; ignore (only a *sender* answers with its own keep-alive) |
| 86 | MSG_ERROR_EXIT | **len 0 or exactly 4** (LE int32 exit code); any other length → exit 12 | see below |
| 100 | MSG_SUCCESS | 4B LE ndx (the 4+8+8 variant is local-server only) | push: sender may remove source file (`--remove-source-files`) |
| 101 | MSG_DELETED | filename text; dir names carry a trailing NUL, non-dirs don't | push with `--delete`: log "deleting X" |
| 102 | MSG_NO_SEND | 4B LE ndx | push: file was skipped |

Legitimate inbound tags for our roles — **pull client** (receiver+generator): DATA, INFO, ERROR, ERROR_XFER, WARNING, IO_ERROR, NOOP, ERROR_EXIT, plus IO_TIMEOUT on daemon transport. **Push client** (sender): those plus DELETED, SUCCESS, NO_SEND. Anything else: canonical exits 12 (`RERR_STREAMIO`, "unexpected tag"). Messages may interleave at any byte position of the logical stream, including mid-flist-entry — demux below the codec layer, never reset codec state on frame boundaries.

**MSG_ERROR_EXIT / goodbye handshake (proto ≥ 31) — yes, the client must forward it:**

- *Initiating*: a process exiting with a nonzero code — except codes 10/12 (`RERR_SOCKETIO`/`RERR_STREAMIO`), signal deaths, and 30 (`RERR_TIMEOUT`), and not while already shutting down — sends MSG_ERROR_EXIT with the 4-byte code, flushes, then keeps the connection alive with keep-alives until the peer closes. Code 0 in a 4-byte payload is legal and follows the same path (clean coordinated shutdown).
- *Receiving the 4-byte form*: a **receiver or generator** (our pull client) echoes MSG_ERROR_EXIT **with the same 4-byte code**; a **sender** (our push client) replies with a **zero-length** MSG_ERROR_EXIT. Flush, then exit with the received code verbatim (no duplicate local error line for the code itself).
- *Receiving the zero-length form*: it is the ack leg; a pure receiver echoes zero-length once; exit 0 path. Never loops because each role only echoes once and then exits.

## 9. 3.4.3-era deltas vs 3.2.x

PROVENANCE: canonical-behavior + NEWS.md; compat facts cross-checked against our measured 510.

**No wire-format changes** to the flist or message channel for a given protocol + compat set — a byte layout valid against 3.2.7 is valid against 3.4.3. What changed:

- 3.4.0 bumped the advertised protocol to **32** purely as an update marker; protocol 31 semantics are unchanged (MEASURED: 3.4.3 negotiates 31/30/29/27 fine).
- `CF_ID0_NAMES` (bit 8) arrived in 3.2.7 — the §5 id-0 name tail only exists when the bit is negotiated; a 3.2.4-era peer sends the bare `varint(0)` terminator. `CF_VARINT_FLIST_FLAGS` dates to 3.2.4; both are compat-bit-gated — never version-sniff.
- 3.4.x added **receive-side validation** we should mirror (and that a 3.4.3 peer will enforce against our push): mode type must be one of the seven `S_IFMT` values (mode 0 only under `--delete-missing-args`) → exit 2; nsec varint bounded 0..999999999 (3.4.3 `read_varint_bounded`) → exit 2; hardlink ndx bounds → exit 2; negative file length → exit 4; unsafe path (`..`, absolute) → exit 4; flist names not implied by the requested args are rejected ("unrequested file-list name", 3.2.5+ tightened through 3.4.x) → **our push flist must contain only arg-covered paths**; inc-recurse-only guards (dup dir flists, dir_ndx bounds) don't apply to us.
- `use_safe_inc_flist` is forced on at proto ≥ 31 independent of `CF_SAFE_FLIST` (present since 3.1.x — noted because it explains why the io_error endlist varint is always meaningful at 31).
- 3.4.3 keep-alive/messages unchanged; `MSG_ERROR_EXIT` goodbye is 3.1.0/proto-31 behavior, still current.

---

Cross-reference notes for `docs/wire-notes.md`: this closes the `XMIT_* — not yet recorded` row (now capture-pinned), confirms codec-spec §12 items 5 (flist entry layout at 31 — pinned by both captures) and 6 (negative length → reject, canonical exits 4), and resolves the end-of-flist encoding question. `XMIT_MOD_NSEC` was subsequently pinned by capture too: ssh31-sizes-list entry 0 carries xflags `a0 19` = 0x2019 with nsec varint `f0 f7 16 98 1b` = 462952183 between mtime and mode. **Symlink target encoding (§6) is the one remaining unpinned section** — pin during a later interop pass with a fixture tree containing a symlink.

The sort comparator (§7) rules 3 and 4 were additionally pinned by two live discriminating
experiments (rsync 3.4.3, alpine:3.21): files `a-dash`/`bbb.txt` listed before dirs `aaa`/`adir`/`ccc`
(band rule), and dir `foo-bar` + subtree listed before dir `foo` (virtual trailing slash).
