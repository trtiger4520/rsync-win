---
name: phase
description: Run the next roadmap phase (or a named one) end-to-end with the project's 8-step working method and the proven orchestration pattern. Use when the user says 請繼續 / start P<n>.
---

# Execute a roadmap phase

The method itself is in `docs/roadmap.md` ("The working method") — spec → capture → validate →
implement → hermetic tests → live interop → adversarial review → commit+docs. This skill encodes
**how to orchestrate it** so each phase doesn't re-derive the pattern.

## Orchestration pattern (proven in P4/P5)

1. **Planner first**: spawn the `planner` agent with pointers to the phase's roadmap section,
   `AGENTS.md`, the files to be touched, and the relevant wire-notes open questions. Demand:
   dependency-ordered tasks, files touched per task, exact `dotnet test --filter` per task,
   byte/hash-exact acceptance criteria, and a list of **capture experiments that must precede
   implementation** (a wrong wire guess is a silent desync — never let an implementer guess).
2. **Run capture experiments yourself** (see `capture-vectors` skill) in a background Bash task
   *in parallel with* the first implementer. Decode with the `wire-decode` skill; record the
   fact in wire-notes before the dependent task starts.
3. **Group implementer tasks by file overlap, not by topic.** Tasks touching the same file go
   to ONE implementer sequentially; only disjoint-file tasks run as parallel agents. (P5:
   WindowsPathMapper + temp names + skip reasons shared PullSession.cs → one implementer;
   fast path + Channels shared the request loop → the next one.)
4. Implementer prompt must include: do NOT commit; comments in English, no trailing 。;
   docs to update in the same working tree (roadmap checkboxes, wire-notes entries);
   the verification commands to actually run and report.
5. **Verifier before "done"** — independent `verifier` agent checks the diff against the
   planner's acceptance criteria and re-runs the tests. Implementer self-report is never
   sufficient. Fix FAILs narrowly; do not expand scope.
6. **Adversarial review** at phase end via the `protocol-reviewer` agent; verify findings
   before fixing, defer non-blockers with a written note in wire-notes open questions.
7. **Commit at the breakpoint**: `P<n> complete: <one-line capability>` (no trailing 。, no
   Co-Authored-By), with roadmap status table + checkboxes, wire-notes, and specs updated in
   the same commit.

## Gates that end a phase

- `dotnet build` 0 warnings; `dotnet test --filter "Category!=Interop"` green (fast, hermetic)
- `dotnet test --filter "Category=Interop"` green against a real rsync (Docker); every interop
  test has a hang-detection timeout — phase-boundary bugs hang, they don't fail
- Acceptance is byte/hash equality or "re-run transfers nothing" — never "looks right"

## Report to the user

繁體中文, lead with what capability now works, evidence (test counts, interop result,
commit hash), and what phase is next.
