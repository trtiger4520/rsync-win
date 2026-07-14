All source reading is done. Here is the spec.

---

# rsync transfer-phase wire spec — protocol 30/31, uncompressed, no `--inplace`, client PULL

Scope: we are receiver+generator, server is `--sender`; compat set per `docs/flist-spec.md` (510, no
inc-recurse). Everything below rides the multiplexed MSG_DATA logical stream (`docs/flist-spec.md` §8);
mux messages may interleave at any byte position; no codec state resets on frame boundaries.
Cross-refs: iflags/message shapes `codec-spec.md` §5, sum head §7, checksum contexts §10, ndx codec §4.

PROVENANCE (whole doc): canonical rsync master (GPLv3) read for behavior only — `sender.c:send_files/receive_sums/write_ndx_and_attrs`, `match.c:match_sums/matched/hash_search`, `token.c:simple_send_token/simple_recv_token`, `receiver.c:recv_files/receive_data`, `generator.c:generate_files/check_for_finished_files/sum_sizes_sqroot`, `rsync.c:read_ndx_and_attrs`, `io.c:read_sum_head/write_sum_head/write_ndx/read_shortint`, `main.c:handle_stats/read_final_goodbye/do_recv/write_del_stats/read_del_stats`, `checksum.c:sum_init/sum_end`. Scalars are wire facts. Proto-27 base cross-checked against openrsync (ISC: `sender.c`, `downloader.c:protocol_token_raw`, `blocks.c:blk_recv_ack`) and gokrazy/rsync (BSD-3: `internal/receiver/token.go`, `receiver.go`, `transfer.go`). MEASURED facts cite `wire-notes.md`.

## 1. Sender's per-file reply (data channel, s2c)

PROVENANCE: canonical-behavior `sender.c:write_ndx_and_attrs` + `send_files`; echo-verbatim cross-checked openrsync `blk_recv_ack` (byte-identical 16-byte echo at proto 27).

Per replied file, contiguous in the logical stream:

1. **ndx** — encoded on the **sender's own outbound ndx state** (independent `prev_positive=-1`, `prev_negative=1` per direction; `NDX_DONE` = single `0x00`, stateless). Value = the ndx we requested; replies arrive **in request order** (single-threaded sender loop, non-inc-recurse).
2. **iflags** — LE16, **verbatim echo** of the word we sent.
3. **fnamecmp_type** — 1 byte, iff bit 11 (`0x0800`); verbatim echo.
4. **xname vstring** — iff bit 12 (`0x1000`); verbatim echo.
5. iff `ITEM_TRANSFER` (`0x8000`):
   - **sum-head echo**: 4 LE int32 (`count, blength, s2length, remainder`) — a **verbatim echo of our request's four values**, never recomputed (sender stores the read fields and re-serializes them; block sums are consumed, never echoed).
   - **token stream** (§2), then **whole-file checksum** (§3).

Attribute-only replies (`ITEM_TRANSFER` clear) end after step 4 — **confirmed nothing follows** (out of scope exception: `-X` xattr request sub-exchange). An `ITEM_TRANSFER` reply/request in phase 2 is a protocol error on both ends (exit 2).

**A request may produce NO reply**: on open-failure/vanished file the sender emits mux `MSG_NO_SEND` (4B LE ndx) instead and continues (proto ≥ 30). Never assume 1:1 request/reply; correlate by ndx.

## 2. Token stream framing (no `-z`)

PROVENANCE: canonical-behavior `token.c:simple_send_token/simple_recv_token`, `match.c:matched`; cross-checked openrsync `downloader.c:protocol_token_raw` and gokrazy `token.go` (identical at proto 27–31).

Each token is a **plain 4-byte LE signed int32** (`write_int`) — **no varint forms at any protocol ≤ 31**:

- **t > 0**: t literal bytes follow raw. Stock sender emits ≤ **CHUNK_SIZE = 32768** per token (longer literal runs become consecutive tokens), but the canonical reader accepts arbitrarily large t (drains in 32 KiB pulls; comment cites acrosync's 64 KiB blocks) — our reader must too (sanity-bound, don't hard-code 32768).
- **t < 0**: block reference `i = -(t+1)`; must satisfy `0 ≤ i < count` (else protocol error, exit 2). Matched length = `blength`, except block `count-1` when `remainder != 0` → `remainder`. Data comes from OUR basis at offset `i * blength`.
- **t == 0**: end of this file's delta stream; the checksum trailer follows immediately.

The compressed byte-flag format (`END_FLAG`/`TOKEN_REL`/`DEFLATED_DATA`…) exists only under `-z*` — out of scope.

## 3. Whole-file checksum trailer

PROVENANCE: canonical-behavior `match.c:match_sums` (sender), `receiver.c:receive_data` (read + compare), `checksum.c:sum_init/sum_end`; seed semantics = `codec-spec.md` §10 context B (confirmed against `sum_init`); read-error sentinel cross-checked openrsync `sender.c` (same trick, bumps first byte vs canonical's last — both just guarantee mismatch).

- Length = **full digest length of the negotiated xfer checksum** (`xfer_sum_len`): md4 16, md5 16, xxh64 8, xxh3 8, xxh128 16 (emitted `low64` LE then `high64` LE). NOT the `file_sum_nni` algorithm — the trailer uses **`xfer_sum_nni`**, same algorithm as block sums; `file_sum_nni` is only the pre-transfer `--checksum` flist sum.
- **Always present** — even for zero-length files and count=0 full transfers, and even on attribute-identical redo results. It is computed over the **entire new file content** (literals + matched blocks in output order), with §10-B seed rules: md5 / md4-negotiated-at-30+ **unseeded**; proto-29 `MD4_OLD` prepends the LE seed unconditionally (even 0); xxh64/xxh3 `reset(0)` — session seed ignored.
- **Sender read-error sentinel**: if the sender hit local read errors it deliberately sends a wrong sum — all zero bytes, or last byte `0x01` if the true digest was all-zeros — to force a verification failure. No special decoding needed; it just mismatches.
- **Receiver on mismatch** (`recv_ok = 0`): the finished temp file is discarded (kept only under `--partial`); first failure → queue the ndx for redo (canonical sends internal `MSG_REDO` receiver→generator over the fork pipe — for our single-process client this is just "append to redo list"), log WARNING "will try again", **no exit-code impact**. The redo transfer happens in phase 1 (§5).

## 4. Zero-length files & attribute-only traffic

PROVENANCE: canonical-behavior `match.c:match_sums` else-branch + `sender.c` (`st.st_size==0 → mbuf=NULL`); gokrazy `receiver.go:receiveData` handles identically.

- Zero-length source, full transfer: reply = ndx + iflags(+payloads) + 16-byte sum-head echo + **one EOF token `00 00 00 00`** + full trailer (md5-of-empty `d41d8cd98f00b204e9800998ecf8427e`, unseeded). Nothing is skipped.
- Request side (us): a new file gets the all-zero null head; an existing zero-length basis produces `(0, 700, 2, 0)` — both have `count=0`, both legal, echoed verbatim.
- Directories, symlinks, and all attribute-only messages: **confirmed** no sum head, no tokens, no trailer (§1). `ITEM_TRANSFER` on a non-regular file → both ends abort (exit 2, `rsync.c:read_ndx_and_attrs` / receiver check).

### 4a. Phase-0 skip rule (mtime+size fast path, P5)

PROVENANCE: captures `ssh31-pull-uptodate` (dest = exact copy, size+mtime match) and `ssh31-pull-partial` (one file stale content+mtime, one identical bytes but older mtime, rest up to date).

- A regular-file entry whose local size **and** mtime already match the flist entry is **not requested at all**: no ndx, no iflags, no sum head — zero generator bytes for that entry. Confirmed by `ssh31-pull-uptodate`: the entire c2s logical stream there is exactly the empty filter-list `int32 0` plus the five `NDX_DONE` bytes, nothing else, for all seven files **and** both directories.
- This extends to directories: one that already exists with the entry's exact mtime also gets no itemize echo. A directory that is freshly created is `ITEM_IS_NEW|ITEM_LOCAL_CHANGE`; one that exists but whose mtime differs is `ITEM_REPORT_TIME` only (this is what the transfer root itself gets whenever the destination directory was just created by us — it always "exists" by request time, so it never takes the `IS_NEW` path).
- A regular file that exists locally but differs is still requested, with the all-zero sum head (no delta support before P6) and iflags built from which field(s) differ: `ITEM_TRANSFER` always, plus `ITEM_REPORT_SIZE` iff the size differs, plus `ITEM_REPORT_TIME` iff the mtime differs. Never `ITEM_IS_NEW` — that stays reserved for files missing locally. Observed in `ssh31-pull-partial`: b001 (size and mtime both differ) → `0x800C`; b002 (same size, older mtime) → `0x8008`.
- A file missing locally keeps the existing shape from §3/§4: `ITEM_TRANSFER|ITEM_IS_NEW` + all-zero sum head.

## 5. Phase/DONE choreography with transfers (pins codec-spec §6)

PROVENANCE: canonical-behavior `generator.c:generate_files` (incl. `EARLY_DELAY_DONE_MSG() ≡ !delay_updates`, `EARLY_DELETE_DONE_MSG() ≡ !(delete_during==2 || delete_after)`), `sender.c:send_files` (`max_phase=2`), `main.c` (`do_recv` parent lines: final goodbye; `do_server_sender`: stats; `read_final_goodbye`), `receiver.c:recv_files`; counts MEASURED for list-only in `wire-notes.md` P3 (c2s 5 DONEs @31 / 4 @30; s2c 3 DONEs + stats + @31 goodbye).

Default pull (no `--delay-updates`, no `--delete-after/--delete-delay`), c2s = us, s2c = sender:

| step | proto 31 | proto 30 |
|---|---|---|
| c2s phase-0 requests, then **DONE#1** | same | same |
| c2s phase-1: redo requests only (nothing if redo list empty), strictly between DONE#1 and the next burst | same | same |
| c2s burst after redo phase: **DONE#2, DONE#3** (early delay-done), [**NDX_DEL_STATS + 5 varints** iff deleting], **DONE#4** (early delete-done) | #2,#3,(del-stats),#4 | #2,#3 only (no 31 delete phase) |
| s2c: echo **DONE** after reading #1; echo **DONE** after #2; reading #3 ends the loop (`phase 3 > max_phase 2`, not echoed) | 2 echoes | 2 echoes |
| s2c end: [`MSG_IO_ERROR` mux msg iff sender's io_error changed] → **final DONE** (3rd) → **stats block** | same | same |
| s2c goodbye: sender reads our #4 → **echoes one DONE** (4th s2c); with `--delete` it first reads our del-stats and **echoes NDX_DEL_STATS + 5 varints** | yes | absent |
| c2s **final goodbye DONE** (#5 @31, #4 @30) after we've consumed stats (+goodbye); server then exits 0 → clean EOF | #5 | #4; after stats expect **EOF, no goodbye DONE** |

- **Stats block** (s2c, between sender's final DONE and the goodbye leg): 5 × `varlong` with `min_bytes=3`. Wire order — CORRECTED against all three captures (values unambiguous by magnitude): **`total_read`-by-sender (= bytes WE sent) FIRST, then `total_written`-by-sender (= bytes we received)**, then `total_size` (sum of regular-file sizes only, dirs excluded), `flist_buildtime`, `flist_xfertime` (last two proto ≥ 29). Matches the measured list-only tail and canonical `handle_stats` (the server-sender writes total_read first).
- **NDX_DEL_STATS (-3)** shape: `write_ndx(-3)` (negative path: first-ever negative write encodes `FF 02`) followed by **5 varints**: deleted regular files, dirs, symlinks, devices, specials. Sent by the generator (us) at proto ≥ 31 only, only when `delete_mode || force_delete`; position: before the delete-phase DONE (#4 early, or late after `--delete-after` deletions). A server sender that reads it **echoes it verbatim s2c** (`rsync.c:read_ndx_and_attrs`: `am_sender && am_server`). Reader rule stands: consume 5 varints, not a phase marker; 3.4.3 bounds each at `MAX_WIRE_DEL_STAT = 1<<28` (reject → exit 2).
- **Empty redo list**: phase 1 carries zero bytes c2s — DONE#1 then (after the receiver-side has consumed all phase-0 replies) the #2/#3/#4 burst. Nothing else is ever sent in an empty phase.
- Single-process trap: canonical's receiver→generator `write_int(pipe, NDX_DONE)` and `MSG_REDO`/`MSG_DONE` bookkeeping are **fork-pipe internals** — never emit a 4-byte `FF FF FF FF` DONE on the wire; wire DONEs are always the 1-byte ndx-codec form at ≥ 30.

## 6. Redo mechanics (client = receiver+generator in one process)

PROVENANCE: canonical-behavior `generator.c:check_for_finished_files` (redo branch), `receiver.c:recv_files` (FLAG_FILE_SENT/`redoing`), `generator.c:sum_sizes_sqroot` (`max_s2length = MIN(SUM_LENGTH, xfer_sum_len)`), `io.c:read_sum_head` (s2length ≤ xfer_sum_len validation, the CVE-2024-12084 fix).

- Trigger: trailer mismatch in phase 0 → ndx goes on the redo list; the update was discarded, so the basis on disk is unchanged.
- Phase-1 re-request = an ordinary `ITEM_TRANSFER` request, **indistinguishable in shape**; differences are semantic: `s2length = MIN(16, xfer_sum_len)` (full digest — **8, not 16, if xxh64/xxh3 is the xfer sum**; sending 16 there is rejected by 3.4.0+ servers, exit 2), quick-check bypassed (`ignore_times`), block sums computed over the same basis. Readers must honor the s2length in the head, never assume the phase-0 truncation.
- The sender does **not** mark redos in iflags or anywhere on the wire; its `FLAG_FILE_SENT` flip (short→full csum_length) is local state. The reply's echoed sum head carrying the full-length s2length is the only visible difference.
- Phase-2 failure (redo's own trailer also mismatches): **no third attempt**. Receiver logs `ERROR: <name> failed verification -- update discarded.` as an `FERROR_XFER`-class message → sets the xfer-error flag → final client exit **23** (`RERR_PARTIAL`). (Vanished-file-only io_error instead yields exit 24.)
- Sender aborts (exit 2) if any `ITEM_TRANSFER` request arrives after it has read the 2nd DONE — so never emit redo requests after DONE#2.
- **ndx codec state persists across both phases** (capture-pinned, `ssh31-pull-redo`): the whole
  session shares ONE `write_ndx` encoder per direction — a redo request for ndx 0 re-encodes as
  `FE 00 00` (the persistent-state escape form: `diff = 0 - prevPositive(0) = 0`), never a fresh
  `01`. No wire message announces a redo at all; the server (and, symmetrically, our own reader)
  infers it purely from seeing the same ndx requested again after DONE#1.

## 7. Interleaved mux traffic during the transfer phase (pull)

PROVENANCE: canonical-behavior `io.c:read_a_msg`, `sender.c:send_files` tail; tags/payloads already spec'd in `flist-spec.md` §8.

- `MSG_NO_SEND` (4B LE ndx): substitute for a reply (§1) — mark the ndx unfulfilled and move on. (flist-spec §8 lists it push-side; a pull client **does** receive it — canonical forwards it receiver→generator.)
- `MSG_IO_ERROR` (4B LE flags): sender emits once per `send_files` pass iff its io_error changed, just before its final DONE; OR into local io_error (drives 23 vs 24 and delete-safety).
- `MSG_NOOP` (len 0) and zero-length `MSG_DATA` keep-alives; `MSG_ERROR_XFER`/`MSG_INFO`/`MSG_WARNING` text at any point. `MSG_ERROR_EXIT` goodbye per flist-spec §8.

## 8. Read-side validation (what we must enforce / expect)

PROVENANCE: canonical `io.c:read_sum_head`, `receiver.c:receive_data`, `errcode.h` scalars.

- Sum head: `count ≥ 0` (and count·xfer_sum_len must not overflow), `0 ≤ blength ≤ 131072` (@≥30), `0 ≤ s2length ≤ xfer_sum_len`, `0 ≤ remainder ≤ blength` → violation is `RERR_PROTOCOL` = **exit 2** in canonical (note: codec-spec §7 currently says exit 12 — reconcile; 12 = `RERR_STREAMIO` is for stream/mux-level failures).
- Token stream: block index out of `[0, count)` → exit 2; a block-match token when we have no basis mapped **and are writing output** → exit 2 (on the canonical discard path it's absorbed instead — normal protocol, the sender doesn't know we're discarding).
- Trailer mismatch is NOT a protocol error — it's the redo path (§6).

## 9. 3.4.3 vs 3.2.x in these paths (NEWS check)

PROVENANCE: NEWS.md 3.4.0–3.4.3 + code; flist-spec §9 already covers flist/message channel.

- **No wire-format change** to the uncompressed transfer phase, phases, DONEs, stats, or trailer since 3.2.x. Protocol 32 (3.4.0) is a marker only.
- 3.4.0 (CVE-2024-12084): `s2length` is now validated against the **negotiated** digest length — the practical constraint on our redo requests (§6) and the origin of `max_s2length = MIN(16, xfer_sum_len)`.
- 3.4.2: compressed-token decoder rejects negative token values; 3.4.3 (CVE-2026-43618): compressed-token counter bounds — both `-z`-only, out of scope.
- 3.4.3 defence-in-depth we should mirror: bounded `read_sum_head` count arithmetic, `MAX_WIRE_DEL_STAT = 1<<28` per del-stats varint, receiver block-match-with-no-basis check (§8).
- CVE-2026-43620 (recv_files parent_ndx) is inc-recurse-only — moot under `--no-inc-recursive`, which also remains the published workaround.

## 10. Pin by capture (P4 additions)

1. The c2s DONE#2/#3/#4 burst bytes at 31 with a real transfer (extends the measured list-only tail).
2. A `--delete` run at 31: c2s `FF 02` + 5 varints and its s2c echo position.
3. `MSG_NO_SEND` from a vanished file + end-of-pass `MSG_IO_ERROR` (expect exit 24).
4. Token stream + trailer for: zero-length file, count=0 full transfer, and a delta with matches (verify remainder-block length handling on the last block).

Resolved by capture (P6): an induced-mismatch redo — `ssh31-pull-redo` pins the full-length
(s2length=16) sum head on the wire and the persistent ndx-codec re-encode (§6).

---

## Validation status (P4)

Byte-exact decode of all three captures, both directions, every byte accounted (ssh31-pull-rt,
ssh30-pull-rt, ssh31-pull-delta). Highlights:

- Every whole-file trailer recomputed and matched as **XXH128 with seed 0**, emitted low64 LE then
  high64 LE — the "whole-file sum ignores the session seed" rule is now measured for xxh128.
- Every ndx shadow-re-encoded byte-identical; DONE maps pinned: c2s @31 = requests+#1, #2#3#4
  burst, goodbye #5 (@30: 4 DONEs, no goodbye leg on either side); s2c @31 = echo#1, echo#2+final#3,
  stats+goodbye#4 (@30: 3 DONEs + stats, clean EOF).
- Delta capture: request carried sum head (429, 700, 2, 400) + 429×6B block sums; reply tokens
  `ref0, lit700, ref[2..213], lit700, ref[215..428], END` reconstruct exactly 300000 bytes with the
  remainder rule on block 428; reconstructed SHA-256 equals the full-transfer content.
- Sections still uncaptured (pin in the phase that needs them): `--delete` del-stats echo,
  MSG_NO_SEND/MSG_IO_ERROR traffic.

Implementation gates in the test suite: `PullSessionReplayTests` replays the whole capture through
`PullSession` (7 files reconstructed, SHA-256-pinned, xxh128 trailers genuinely verified) and
asserts our generator's demuxed byte stream equals the captured client's exactly;
`TokenStreamCaptureTests` pins the delta token semantics; the live gate pulls a tree over ssh.exe
byte-identical with remote exit 0.

## P6 additions: delta signatures + redo (capture-pinned)

`ssh31-pull-delta` (single-file `--no-whole-file` pull) pins the generator's real signature request
— sum head `(429, 700, 2, 400)` + 429 × 6-byte block entries — byte-exact against the captured
client, and the reconstructed 300000-byte file SHA-256-matches the full-transfer content
(`PullSessionReplayTests.DeltaReplay_SignsExistingBasisAndReconstructsByteExact`). `ssh31-pull-redo`
(an induced whole-file-trailer mismatch) pins the redo request's full-length signature
(s2length=16, count/blength/remainder unchanged from phase 0) and the persistent ndx-codec
re-encode of the same ndx as `FE 00 00` between DONE#1 and the DONE#2/#3/#4 burst — exercised
hermetically (no capture-fidelity concern; the shape is fully spec'd) in
`PullSessionRedoTests.InducedMismatch_RedoesWithPersistentNdxState_AndFullLengthSignature`.
