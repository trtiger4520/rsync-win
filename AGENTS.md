# AGENTS.md — entry point for AI coding agents

This file is the model-agnostic starting point (Codex, Cursor, Claude, or any other agent).
Read these, in order, before changing anything:

1. **[`CLAUDE.md`](CLAUDE.md)** — project rules: architecture layering, protocol traps,
   licensing/provenance, build & test commands. Despite the filename, every rule in it applies
   to ALL agents, not just Claude.
2. **[`docs/roadmap.md`](docs/roadmap.md)** — the living plan: what is done (with commit
   evidence), what is next, per-phase tasks and acceptance gates, and the mandatory working
   method (spec → capture → validate → implement → hermetic tests → live interop → review →
   commit).
3. **[`docs/wire-notes.md`](docs/wire-notes.md)** — verified protocol facts, provenance, the
   trap list, and open questions. Read it before touching any codec.
4. Layer specs as needed: `docs/codec-spec.md`, `docs/flist-spec.md`, `docs/transfer-spec.md`.

Reusable workflow recipes live in `.claude/skills/*/SKILL.md` (phase orchestration, Docker
vector capture, wire-byte decoding) and reviewer/analyst role prompts in `.claude/agents/`.
They are written for Claude Code's skill/agent machinery, but the content is plain markdown —
non-Claude agents should read and follow them as ordinary recipes.

## What this project is

A native Windows rsync **client** in C#/.NET 10 that talks the real rsync wire protocol
(protocol 31, negotiating down to 30/29) to stock rsync servers over ssh.exe and `rsync://` —
no WSL, no Cygwin. The load-bearing decision is a **pure protocol core**
(`src/RsyncWin.Protocol`, zero I/O) gated by golden vectors captured from a real rsync.

## Hard rules (violating any of these has burned us before)

- **Never** add I/O (`Process`, `Socket`, filesystem) to `RsyncWin.Protocol`.
- **Never** install or run rsync on the Windows host — use throwaway Docker containers only.
- **Never** copy code or tables from canonical rsync (GPLv3) — behavior reference only; port
  expressions from BSD openrsync / gokrazy-rsync.
- **Never** trust "looks right": phase gates are byte/hash equality or "re-run transfers
  nothing". Protocol bugs here fail *silently* (full resend) or as exit-12 desyncs, and
  phase-boundary bugs manifest as **hangs** — every interop test needs a timeout.
- Names, sort order, and checksums are byte-exact contracts; see the trap list in
  `CLAUDE.md` / `docs/wire-notes.md`.

## Build & test

```powershell
dotnet build
dotnet test --filter "Category!=Interop"   # fast, hermetic — the default gate
dotnet test --filter "Category=Interop"    # live rsync via Docker (daemon must be running)
```

## Documentation upkeep (mandatory)

Documentation here is load-bearing: sessions are context-limited, and the docs are how the next
session (or the next model) avoids re-deriving byte-level facts. In the **same commit** as your
change:

- Phase progress or re-scoping → update the status table and task checkboxes in
  `docs/roadmap.md`.
- New/changed protocol facts, resolved or newly deferred questions → `docs/wire-notes.md`.
- Byte-layout changes → the relevant `docs/*-spec.md`.
- New standing rules or traps → `CLAUDE.md` (and mirror anything agent-relevant here only if it
  changes the reading order above).

## Commit conventions (release-affecting)

Releases are automated by **semantic-release**, so **every commit MUST follow
[Conventional Commits](https://www.conventionalcommits.org/)** or the release is silently skipped.
The full ruleset lives in one place — **[`CLAUDE.md` → "Commit conventions"](CLAUDE.md)** — read it
before committing. Squash-merge PRs and make the PR title the Conventional Commit; a CI check
(`pr-title-lint`) enforces this.
