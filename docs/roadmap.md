# Roadmap — RsyncWin build plan and AI working method

> **LIVING DOCUMENT — update it or it rots.** Whoever (human or AI agent) completes a phase,
> closes a finding, or re-scopes work MUST update this file in the same commit: flip the status
> table, check off tasks, move resolved open questions to their pin. A future session starts by
> reading this file; stale status here costs a full re-discovery pass.
>
> Entry points for agents: [`AGENTS.md`](../AGENTS.md) (any model) → [`CLAUDE.md`](../CLAUDE.md)
> (rules & constraints) → this file (what to do next) → [`wire-notes.md`](wire-notes.md)
> (verified protocol facts) → the per-layer specs (`codec-spec.md`, `flist-spec.md`,
> `transfer-spec.md`).

## Status

| Phase | Scope | Status | Evidence |
|---|---|---|---|
| P0 | Scaffold, constants, interop substrate | **DONE** | solution builds; container substrate in `SshRsyncContainer` |
| P1 | Pure codecs + checksums + block sizing (hard gate) | **DONE** | `c330c22`, `0f38380`, `d4af5d0` — golden vectors from `--debug=deltasum2/4` |
| P2 | SSH transport + handshake | **DONE** | `8604830` — live proto-31 negotiation, captured 29/30/31 prologues replay byte-exact |
| P3 | Multiplex + filter send + flist receive (list-only) | **DONE** | `cd61fc4` — list-only against real rsync exits 0 |
| P4 | Pull transfer (THE interop milestone) | **DONE** | `4683481` — whole tree over ssh.exe SHA-256-identical, remote exit 0; adversarial review findings fixed |
| P5 | Recursive pull polish (fast path, sanitization, Channels) | **DONE** | `P5 complete` commit — fast path pinned byte-exact by `ssh31-pull-uptodate`/`-partial` vectors; live gates 7/7 incl. re-run-transfers-nothing + hostile-name sanitization; CLI wired; 4 adversarial findings fixed |
| P6 | Delta efficiency (basis matching) | **DONE** | `P6 complete` commit — xxh128 block sums capture-pinned (429/429 + 2048/2048 full-length via `ssh31-pull-redo`); delta replay + redo request bytes golden; live gate: 8-byte edit of 4 MiB re-pulls literal 2048 / matched 4,192,256, EXACTLY equal to real rsync `--stats`; zero-fill degrade on changed basis |
| P7 | Push (sender role) | **DONE** | `P7 complete` commit — four full-stream c2s replay gates byte-identical (`ssh31-push-rt`/`-uptodate`/`-delta`/`-redo`); MatchSearcher token streams capture-exact incl. redo; live gates 4/4: tree SHA-identical server-side, re-push transfers nothing with real NTFS nsec mtimes, delta literal/matched EXACTLY equal to real rsync `--stats`, vanished source → exit 23 not hang; 3 adversarial findings fixed |
| P8 | Daemon transport (`rsync://`) | **DONE** | `P8 complete` commit — preamble byte-identical to captured client (greeting/auth/argv) across 10 daemon vectors; auth digest recipe computationally reproduced (md5, proto 31 and 29); post-OK stream `cmp`-identical to ssh minus version ints; 13 hermetic daemon replays + live gates 8/8 (anon+auth pull, module listing, push byte-identical, re-push transfers nothing, @ERROR/readonly exit codes); 4 adversarial findings fixed |
| P9 | Polish (`--checksum`, `--delete` pull, flag surface, exit codes) | **DONE** | `P9 complete` commit — `--checksum` flist F_SUM capture-pinned (`ssh31-pull-checksum`: 16B xxh128, unseeded, regular-files-only, last field) + generator decision 0x0008/0x8002/0xA002 (hermetic byte-exact c2s replay + decision unit tests + live attribute-only/content-change gates); `--delete` pull is **local-only** (`ssh31-pull-delete`: zero wire del-stats either direction) via `LocalTreePruner` (reparse-point-safe, io_error-suppressed incl. mid-session `MSG_IO_ERROR`); drive-letter/UNC disambiguation, `-c`/`--delete`/long-form/unknown-flag handling, exit-code completeness 0/2/3/4/5/10/11/12/23/24/30 + ssh-255-before-12 ordering fix (`ExitCodeMapper`, one test per code); 394 hermetic + 23 live interop green; adversarial review found no code bugs, 2 test-strength gaps closed |
| P10 | Compression (`-z`) + `--secluded-args` + push `--checksum`/`--delete` | **NEXT** | — |

Estimated effort remaining (active agent working hours, from P1–P9 measured pace):
P10 ≈ 4–8 h (`-z` is a new compressed-token codec + compression-string negotiation — the bulk).

## The working method (follow this loop every phase)

This is how P1–P4 were built and verified; it is the reason the client interoperates. Do not
shortcut it — the failure mode of this protocol is **silent** (full resend or exit-12 desync),
so "looks right" is never a gate.

1. **Spec before code.** Derive the byte layout from *behavior* sources (canonical rsync is
   GPLv3 — read only; port expressions from BSD openrsync / gokrazy-rsync). Write the spec into
   `docs/*-spec.md` with every field, gate, and trap enumerated.
2. **Capture golden vectors.** Run a real rsync (3.4.3, alpine:3.21) in a **throwaway Docker
   container — never install rsync on the Windows host** — and capture both wire directions
   (`test-fixtures/vectors/`). Ambiguities are settled by *decisive experiments* (construct two
   inputs whose captured order/bytes discriminate the hypotheses), not by reading harder.
3. **Validate the spec byte-exact against captures** before implementing. Correct the spec, not
   the capture.
4. **Implement pure-core first.** New wire logic goes in `RsyncWin.Protocol` (zero I/O),
   orchestration in `RsyncWin.Engine`, Windows reality in `RsyncWin.Fs`.
5. **Hermetic tests are the gate.** Replay the captures through the implementation; pin both
   directions (parse the server bytes AND assert our written bytes are identical to the captured
   client's, demuxed). `dotnet test --filter "Category!=Interop"` must stay green and fast.
6. **Live interop gate.** One end-to-end test against a real rsync over ssh (container), with a
   hang-detection timeout (phase-boundary bugs hang, they don't fail). Gate = byte/hash equality
   or "re-run transfers nothing", plus remote exit 0.
7. **Adversarial review.** Independent review lenses (protocol correctness, Windows filesystem
   hostility, tests) with findings adversarially verified before fixing. Fix confirmed findings,
   assess unverified ones inline, defer with a written note in `wire-notes.md` open questions.
8. **Commit at the phase breakpoint and update the docs** (this file's status table,
   `wire-notes.md` facts/open questions, the spec that changed). Commit style:
   `P<n> complete: <one-line capability statement>` — no trailing `。`, no Co-Authored-By.

## P5 — Recursive pull polish

Goal: the pull is a daily-usable tool — safe names, no re-transfer on re-run, scales past
capture-tree sizes.

Tasks:
- [x] **`WindowsPathMapper` in `RsyncWin.Fs`** — replace P4's hard-reject of `\` / `:` names
      (see `PullSession.LocalPath`) with sanitization/mapping; handle reserved device names
      (CON, NUL, COM1…), trailing dots/spaces, `\\?\` long paths. Keep the containment check as
      the last line of defense.
- [x] **mtime+size fast path** — generator compares the flist entry against the local file and
      skips up-to-date files (no transfer request). `--checksum` later forces the full compare
      (P9). Gate: **re-run transfers nothing** (assert 0 requested files and stats agree).
- [x] **Channels-based generator/receiver concurrency** — P4 writes every phase-0 request before
      reading any reply; a large flist can fill both pipe buffers and mutually block (documented
      in `wire-notes.md` open questions). Move to concurrent loops over
      `System.Threading.Channels` per the architecture. The `GeneratorBytes` golden test must
      still pass (demuxed logical stream equality is frame-agnostic, so pipelining is safe).
- [x] **Randomized temp names** — replace the deterministic `<final>.rsyncwin-tmp` suffix
      (clobber risk against a pre-existing user file; documented in open questions).
- [x] **Warn-and-skip unmappable types** (symlinks without privilege, devices) — the flist
      decode is still fully consumed; only the fs-apply is skipped.
- [x] **Wire the CLI**: `rsyncwin -r user@host:path dest` runs `PullSession`; map failures to
      rsync numeric exit codes (fs errors → 11, protocol → 12, ssh 255 → 5).

Verify: `diff -r`-equivalent tree equality vs container; re-run transfers nothing; hostile-name
tree (created inside the container) lands sanitized without escaping the destination.

## P6 — Delta efficiency

Goal: a near-identical basis file transfers only changed blocks. Hardest remaining phase — a
single byte wrong in the rolling checksum is a silent full resend.

Tasks:
- [x] `SignatureGenerator` — block the basis file with `sum_sizes_sqroot` sizing and emit real
      sum heads (count/blength/s2length/remainder) instead of P4's all-zero head. s2length =
      `MIN(16, xfer_sum_len)` rules per `transfer-spec.md`.
- [x] `RollingChecksum` integration on the receive path; `FileReceiver` block-reference tokens
      reconstruct from the basis (currently `NotSupportedException` outside full transfers).
- [x] **Pin xxh128 block-sum seed rules by capture** — `StrongChecksum.ComputeBlockSum` throws
      for xxh128 until then; whole-file xxh128 is already pinned (seed 0, low64 LE then high64).
- [x] Capture an **induced-mismatch redo** (corrupt a basis mid-transfer) to pin the redo-phase
      bytes (`transfer-spec.md` §10 lists this as uncaptured).
- [x] Basis-file random reads via `RsyncWin.Fs.BasisFileStore`.

Verify: modify one block of a large file, re-pull; our `--stats`-equivalent shows literal ≪ file
size and matched ≈ file size, cross-checked against real rsync's `--stats` for the same pair.

## P7 — Push (sender role)

Goal: push a tree to a real rsync server. Reuses the pure core; the new work is the sender loop.

Tasks:
- [x] `FileEnumerator` (source walk) + flist **encode** (the reader's layout in reverse,
      `flist-spec.md`) + ordinal sort.
- [x] Read the server-generator's requests (ndx + iflags + sum head), run `MatchSearcher`
      (rolling-search equivalent of `match.c`), emit token stream + whole-file trailer.
- [x] Handle the server's redo phase (re-send on its mismatch report).
- [x] Capture c2s-sender fixtures (new direction) for hermetic replay
      (`ssh31-push-uptodate`/`-delta`/`-redo`/`-nsec1`+`-nsec2`, recipes merged into capture.sh).

Verify: push a tree; inside the container `diff -r` is empty and re-push transfers nothing;
delta stats sane on a modified file.

## P8 — Daemon transport (`rsync://`)

Tasks:
- [x] `DaemonTcpTransport` — TCP 873. **No binary version ints on daemon sockets**: the
      greeting is textual `@RSYNCD: <ver>` both ways (see `wire-notes.md` gating rules), then
      module name, then `@RSYNCD: OK` / `AUTHREQD <challenge>`.
- [x] MD5 challenge-response auth; module listing.
- [x] After `OK` the session is the identical multiplex + protocol core
      (`HandshakeOptions.PreNegotiatedProtocolVersion` skips the version ints; daemon sessions
      floor negotiation at protocol 30 — see `daemon-spec.md`).

Verify: pull and push against a real `rsyncd` module in a container, anonymous and
authenticated. Done — see the status table evidence; full byte layout in `docs/daemon-spec.md`.

## P9 — Polish

Scoped (with the user) to **everything except `-z`**, which moved to P10 (a compressed-token codec is
a phase of its own). `--checksum` and `--delete` landed **pull-only** — the push variants need a
FileListWriter F_SUM emission / a server-side del-stats read plus their own captures (P10).

- [x] **`--checksum` (`-c`), pull** — capture `ssh31-pull-checksum` pinned the flist `F_SUM`
      (16-byte xxh128, unseeded, regular-files-only, entry's last field; `flist-spec.md` §14) and the
      generator's decision iflags (`transfer-spec.md` §4b). `FileListReader` decodes F_SUM under
      `FileListOptions.Checksum`; `PullSession.ComputeChecksumDecisionAsync` replaces the mtime+size
      fast path with a whole-file-checksum compare (0x0008 attribute-only / 0x8002 transfer / 0xA002
      new / skip). Gates: hermetic byte-exact c2s replay + decision unit tests + live gates.
- [x] **`--delete`, pull** — capture `ssh31-pull-delete` proved it is **local-only**: no `--delete`
      in the server argv, empty filter list, NO `NDX_DEL_STATS` on the wire either direction
      (`transfer-spec.md` §5a — a correction to the earlier spec-derived assumption). Implemented as a
      local `RsyncWin.Fs.LocalTreePruner` (reparse-point-safe, containment-checked, read-only-clearing),
      run after the transfer tail and suppressed on `io_error` (flist OR mid-session `MSG_IO_ERROR`).
- [x] **Exit-code completeness** (0/2/3/4/5/10/11/12/23/24/30) with a test per code — `ExitCodeMapper`
      centralizes the mapping and fixes the ssh-255-before-12 ordering (unreachable host → 5, not 12).
- [x] **Broader flag surface** — drive-letter/UNC disambiguation (`D:\backup` is local, not a remote
      host), `-c`/`--delete`/`--recursive`/`--archive`/`--times` long forms, unknown-flag rejection
      (exit 1), extracted `CommandLineParser` (unit-tested). `--secluded-args`/`-s`/`--protect-args`
      is **recognized and rejected** (exit 1) — its wire format is observed (`wire-notes.md` open
      questions) but deferred to P10.

## P10 — Compression + secluded-args + push polish

- [ ] **`-z` compression** — capture the compression-string negotiation (stock rsync offers `zstd`
      first; the BCL only has deflate, so force/scope to `zlib`) and the compressed-token format
      (`END_FLAG`/`TOKEN_REL`/`DEFLATED_DATA`, cross-file zlib flush state, RLE runs — `transfer-spec.md`
      §2 marks it out of scope today). A new codec, hence its own phase.
- [ ] **`--secluded-args` (`-s`)** — implement the pre-handshake NUL-terminated arg list (format
      observed in P9: server argv drops `. <paths>`, the file args are sent before the version int32);
      pin the leading argv[0] token + the daemon case, then send it. Fixes remote paths with spaces.
- [ ] **Push `--checksum` / `--delete`** — FileListWriter F_SUM emission (push-`-c`), and the
      server-side del-stats read (push-`--delete`, s2c) — each needs its own capture.

## Standing constraints (do not relax)

- `RsyncWin.Protocol` stays pure: no `Process`, no `Socket`, no filesystem.
- rsync binaries only in throwaway Docker containers, never on the Windows host.
- GPLv3 provenance rule: canonical rsync for behavior only; tables/expressions from
  openrsync/gokrazy or regenerated.
- Every interop test has a hang-detection timeout.
- No multi-step work is "done" without an independent verification pass (tests actually run and
  observed) — see `CLAUDE.md` orchestration rules.
