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
| P10 | Compression (`-z`) + `--secluded-args` + push `--checksum`/`--delete` | **DONE** | `P10 complete` commit — `--secluded-args` pre-version NUL arg-list byte-exact (`ssh31-secluded-*`); push `--checksum` F_SUM emission (write→read round-trip + live re-push-nothing) and push `--delete` via MSG_DELETED tag 0x6c (empty filter list added), both live-gated; PushSession consumes the protocol-31 `NDX_DEL_STATS` block emitted by newer peers; **`-z` zlibx** codec (forced `--new-compress`, no zstd/zlib) — decoder capture-pinned byte-exact against `ssh31-pull-z-{zlibx,delta}`, encoder↔decoder round-trip, both directions live byte-identical + re-transfers-nothing; 402 hermetic + interop green; adversarial review clean |
| P11 | Application integrity, performance, and Linux image evaluation | **BLOCKED** | Correctness gates pass (426 hermetic, 6 process-level application, existing live interop, Linux image smoke, full deterministic datasets); after two perf repair cycles the fair Linux runner still fails before client start on Windows bind-mount parsing, and Windows smoke still lacks peak RSS and literal/matched evidence |
| P12 | Local-to-local direct copy | **DONE** | `LocalSyncEngine` direct-copy engine (no wire, no transport) with real-rsync trailing-slash semantics, size+100ns-mtime skip fast path, `-c` streamed byte compare with attribute-only update, `--delete` via `LocalTreePruner`, replace hazards (read-only/locked/file↔dir) per-file not fatal, self-copy guards; 496 hermetic green incl. new `LocalSyncEngineTests`; live interop Smoke 10/10 (PullSession finalize extraction regression-checked); adversarial review: 2 confirmed findings fixed; no interop cases (zero wire bytes by design) |
| P13 | `--progress` / `--info=progress2` (client-local UX) | **DONE** | rsync-style progress rendering (bytes/%/rate/ETA/`(xfr#…, to-chk=…)`) on all three paths — pull/local per-block, push per-file. **Zero wire bytes**: never added to `ServerArgvBuilder`, so no capture/interop gate (P12-style). Pure core `FileReceiver` takes only an `Action<long>` byte-advance delegate (stays I/O-free); `ITransferProgressSink` (in `RsyncWin.Fs`) fans out to a stderr `ProgressRenderer`. Hermetic tests: FileReceiver callback sums to literal+matched, renderer format helpers + clock-driven rate/ETA via injected `TimeProvider`, parser flag coverage incl. `-P` still rejected. Format spec: [`progress-spec.md`](progress-spec.md) |

**Core feature development complete.** P0–P10 are DONE; P11 evaluates the completed surface rather
than adding rsync wire features. The client does pull and push over ssh.exe and the rsync
daemon, with delta transfers, `--checksum`, `--delete`, `--secluded-args`, and `-z` (zlibx)
compression, plus local-to-local direct copy (P12, no wire) and `--progress`/`--info=progress2`
display (P13, client-local, no wire). The live interop matrix verifies stock rsync 3.4.3 and 3.4.4 peers through Smoke,
Full, and protocol-29 Guard profiles. Scoped-out-by-design items (each with a written rationale):
protocol 27/29 flist decode, `zstd`/`lz4`/old-`zlib` compression (BCL has only deflate), `-a` extras
on push (uid/gid/links/devices), streaming for &gt;2 GiB files, and issuing
`--delete-after`/`--stats` from this client. See [`interop-matrix.md`](interop-matrix.md), the
per-phase notes, and `wire-notes.md` open questions.

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

Post-completion Windows filesystem validation adds the `Category=WindowsFs` test gate for NTFS
name mapping, long paths, read-only attributes, basis locking, and reparse-point isolation
Docker SSH validation adds long-path pull and push cases; the 2026-07-14 Windows run passed
WindowsFs 90/90, hermetic 455/455, and Interop 37/37 with no orphan `rsyncwin-interop` container

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

- [x] **Single-file push source (ssh + daemon)** — `rsync file host::mod/` / `[user@]host:file`.
      `FileEnumerator.Enumerate` now emits one basename entry with **no** `.` transfer-root for a file
      root (the flist shape canonical rsync sends for a file source arg; `flist-spec.md` §3). No new
      capture needed: the wire shape was already pinned byte-exact by `ssh31-push-delta`/`ssh31-push-redo`,
      and `PushSessionReplayTests.DeltaReplay` now sources its entry through `FileEnumerator.Enumerate`
      as an end-to-end enumerator→wire guard; the failure it fixes is pinned by
      `FileEnumeratorTests.SingleFileSource_ReturnsOneBasenameEntry_NoDotRoot`. The **directory**
      trailing-slash / always-contents convention remains the open P9 item (see the `Program.cs` push TODO).
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

- [x] **`-z` compression** — forced **zlibx** (plain `-z` negotiates zstd — no BCL codec; `--old-compress`
      zlib needs a window-insert primitive the BCL lacks). New codec `ZlibxTokenCodec` +
      `FileReceiver`/`MatchSearcher` compressed paths: DEFLATED_DATA framing (`(flag&0x3f)<<8|next`,
      sync-flush marker stripped/re-appended), TOKEN_REL/TOKENRUN_REL relative-block arithmetic
      (capture-pinned by `ssh31-pull-z-delta`), matched blocks excluded from the deflate window.
      Decoder byte-exact against `ssh31-pull-z-{zlibx,delta}`; encoder↔decoder round-trip; both
      directions live byte-identical (`SshP10InteropTests`). No compression string negotiated
      (`--new-compress`). See `transfer-spec.md` §2a, `wire-notes.md` Compression negotiation.
- [x] **`--secluded-args` (`-s`)** — pre-handshake NUL arg list before the version int32:
      `rsync\0.\0<remote-path>\0\0` (argv[0] `rsync`, dot-arg, remote paths only, empty-string
      terminator), byte-exact against `ssh31-secluded-spacepath`. `ServerArgvBuilder` drops the
      `. <paths>` tail and leads the bundle with `s`; `HandshakeRunner.WriteSecludedArgList` emits the
      list (ssh only — daemon args already go NUL-framed and space-safe). Live-gated (spaced remote path).
- [x] **Push `--checksum` / `--delete`** — F_SUM emission on every regular-file flist entry
      (`FileListWriter` + `PushSession.ComputeFlistChecksumsAsync`, capture `ssh31-push-checksum`);
      `--delete` reports deletions via **MSG_DELETED** (tag 0x6c) and adds the empty filter list to
      c2s (capture `ssh31-push-delete`). PushSession also consumes the five-varint `NDX_DEL_STATS`
      block that protocol-31 peers may emit before the final DONE. Both live-gated. The client still
      never sends `--delete-after`/`--stats`.

## Live interop matrix

The post-development verification gate is implemented by
[`scripts/Invoke-LiveInteropMatrix.ps1`](../scripts/Invoke-LiveInteropMatrix.ps1). Peer image and
source pins live in [`test-fixtures/interop/peer-matrix.json`](../test-fixtures/interop/peer-matrix.json).
Run `Smoke` during daily development, `Full` after Protocol/Engine/Transport/Fs/CLI changes, and
both peers with `Full` plus `Guard` before release. See [`interop-matrix.md`](interop-matrix.md) for
profiles, artifacts, and pass criteria.

## P11 — Application integrity, performance, and Linux image evaluation

Goal: prove the supported RsyncWin CLI surface end-to-end, quantify resource use against stock
rsync under reproducible conditions, and determine how far the current .NET implementation runs on
Linux without widening the protocol scope.

Contract:
- [x] **Process-level application matrix** — spawn the built `rsyncwin` CLI against real rsync over
      SSH and daemon transports. Cover pull/push, module listing/auth, `-r`/`-t`, pull `-a`,
      `-c`, `--delete`, `-s`/`--protect-args`, `-z`, `-e`, URL/double-colon daemon syntax, critical
      combined flags, exact failure exits, and a transfer-nothing re-run. Every case has a timeout;
      success is exit 0 plus path/type/size/SHA-256/mtime manifest equality.
- [x] **Deterministic stress harness** — fixed seed `0x5253594E4357494E` generates: 100,000 × 4 KiB
      small files; 8 × 1 GiB large files; 20,000 mixed files totalling approximately 8 GiB; a 1 GiB
      basis with deterministic 1% block edits; and 2 GiB each of compressible/incompressible data.
      Data is generated per scenario under `artifacts/` and removed before the next scenario so peak
      workspace use remains below 30 GiB.
- [ ] **Measurement contract** — one warm-up plus five measured iterations, alternating client
      order. Record each raw iteration and median/p95 for elapsed time, logical MiB/s, CPU time,
      peak working set/RSS, container cgroup CPU/memory/I/O, literal/matched bytes, exit code, and
      result manifest. Full copy, up-to-date, delta, checksum, compression, and delete are measured;
      SSH is a representative mixed-tree result while the full matrix uses daemon transport.
- [ ] **Fair and practical tracks** — the fair ratio uses the RsyncWin Linux image and stock rsync
      3.4.3 client against the same daemon, network, volumes, warm-cache policy, 4 CPUs, and 8 GiB
      memory. Windows Release measurements against Docker SSH/daemon are reported separately as
      practical absolute results and are never mixed into the fair ratio.
- [x] **Linux daemon-only image PoC** — runtime-selected local path policy keeps Windows sanitizing
      and case-insensitive collision rules while Linux uses native separators and ordinal
      case-sensitive names. Build a non-root .NET 10 image and gate module listing, pull, push,
      checksum, delete, compression, and a zero-transfer re-run against a real daemon. Linux SSH is
      assessed but is not a support promise.
- [ ] **Evidence and report** — preserve `docs/acceptance-report.md` as the P0–P10 snapshot. Commit
      compact JSON/CSV/SVG evidence and `docs/integrity-evaluation.md`, including environment,
      versions, supported/out-of-scope matrix, commands, failures, performance interpretation,
      bottlenecks, and separate compile/start/transfer/platform-semantics conclusions.

No new wire capture is planned: P11 does not change protocol bytes. If a live test finds a wire
discrepancy, stop that dependent task and run a decisive Docker capture before changing any codec.

Current blocker evidence is recorded in `docs/integrity-evaluation.md`. Do not mark the measurement,
fair/practical-track, or final-report checkboxes complete until the fair runner starts both clients,
five manifest-verified full iterations exist per cell, and Windows RSS/literal/matched fields are
observed rather than zero/null.

**Compose fair runner (`tools/RsyncWin.Perf/Invoke-ComposeBenchmark.ps1`).** Supersedes the
`--mount`-string approach that blocked the fair track: daemon + root helper share a **named volume**
(transfer stays VM-internal), clients launch via `docker compose run --name perf-client`, and
`VolumeBridge` stages data host↔volume through the helper (untimed). This starts both clients,
captures real cgroup `memory.peak`, and adds an **FDD/R2R/AOT publish-mode** comparison
(`artifacts/perf/compose/comparison.md`). Smoke-validated: all six operations × three modes,
manifest-verified; AOT ≈ stock rsync while FDD is ~15% slower and ~2× the peak memory — so the root
`Dockerfile` now defaults to `PUBLISH_MODE=aot`. Still open before P11 completion: the full-profile
five-iteration matrix (large-files for transfer-bound throughput; smoke is startup-dominated),
cgroup `cpu.stat` sampling (misses at fast scale → host-CPU fallback), literal/matched, and the
Windows practical track. Unrelated bug found: `rsyncwin --delete` crashes on a trailing-slash dest
(`LocalTreePruner` containment guard) — routed to its own fix; the runner uses no-trailing-slash dests.

## P12 — Local-to-local direct copy

Goal: `rsyncwin [flags] C:\src C:\dst` with both sides local, matching real rsync's user-visible
semantics without the wire protocol.

Design (decided up front, recorded here so a later session does not re-litigate):
- **Direct copy, not a loopback wire session.** Real rsync forks a local server over a socketpair
  but defaults to `--whole-file` there, so a direct copy with rsync's skip/replace semantics is
  behaviorally equivalent — and this codebase has no server-side handshake/generator to loop back to.
  Engine lives in `RsyncWin.Fs` (`LocalSyncEngine`), reusing `FileEnumerator`, `LocalPathPolicy`,
  `DestinationReplacer` (finalize helpers extracted from `PullSession`), and `LocalTreePruner`.
- **Trailing-slash semantics are real rsync's** (`src` creates `dest\src\...`; `src\` copies
  contents), intentionally diverging from the ssh/daemon push TODO(P9) always-contents convention —
  new surface starts out correct; align push later.
- Flags: `-r`/`-a` (as recursion), `-t` (no-op as everywhere), `-c` (streamed byte compare +
  attribute-only mtime update), `--delete` (source-read errors suppress deletion); `-z`/`-s` parse
  and are ignored (meaningless without a wire, same as real rsync locally).
- Skip fast path is size + exact `LastWriteTimeUtc` (full 100ns precision — no wire truncation).
- Exit codes: per-file failures → 23; source/dest-root failures → 11; shape errors (self-copy,
  dest-inside-source, contents-into-a-file) → 1. Never 12 — there is no protocol stream.

Contract:
- [x] Trailing-slash both forms (`\` and `/`), missing-dest creation, single-file source
- [x] Re-run transfers nothing; file and directory mtimes preserved (deepest-first restore)
- [x] `-c` three states (skip / attribute-only / recopy) and fast-path limitation documented by test
- [x] Non-recursive directory source skipped (`skipping directory`), file source works without `-r`
- [x] Replace hazards: read-only replaced; locked dest and non-empty-dir-conflict are per-file
      failures; empty dir / file / reparse point in the way replaced without following links
- [x] `--delete` prune with `LocalTreePruner`, suppressed on source read errors
- [x] Self-copy and dest-inside-source rejected; long paths (>260 chars) copy
- [x] No interop cases: a local copy exchanges zero wire bytes, and a Linux container cannot
      exercise NTFS local semantics — hermetic `LocalSyncEngineTests` are the gate
- [x] Adversarial review (protocol-reviewer): 2 confirmed findings fixed — a junction/mount-point
      **destination root** was unlinked instead of written through (now `EnsureDestinationRoot`:
      the user-named root is never deleted; nested reparse points remain replaceable transfer
      entries), and the `SourceReadError → PruneSkipped` suppression path had zero test coverage
      (now pinned by `LockedSourceFile_SuppressesDelete`). Prune failures now surface as a failed
      item (exit 23) instead of aborting with exit 11.

Known limitations (accepted, by design):
- The self-copy guard is lexical (`Path.GetFullPath`): an 8.3 short-name alias or a junction
  pointing back inside the source evades it. Bounded consequence — enumeration snapshots up
  front, so worst case is one nested self-copy, never unbounded recursion or deletion. A real fix
  needs file-identity comparison via `GetFinalPathNameByHandle` P/Invoke.
- The skip fast path compares exact 100ns mtimes; on FAT32/exFAT destinations (2s/10ms timestamp
  granularity) "re-run transfers nothing" only holds on NTFS. No `--modify-window`.
- The wire pull's `--delete` shares the unwrapped-pruner abort behavior this phase fixed locally
  (PullSession.cs `LocalTreePruner.Prune` call) — align it when next touching that path.

## Standing constraints (do not relax)

- `RsyncWin.Protocol` stays pure: no `Process`, no `Socket`, no filesystem.
- rsync binaries only in throwaway Docker containers, never on the Windows host.
- GPLv3 provenance rule: canonical rsync for behavior only; tables/expressions from
  openrsync/gokrazy or regenerated.
- Every interop test has a hang-detection timeout.
- No multi-step work is "done" without an independent verification pass (tests actually run and
  observed) — see `CLAUDE.md` orchestration rules.
