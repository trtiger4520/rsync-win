# progress-spec — `--progress` / `--info=progress2` output format

**Status:** implemented. **Provenance:** rsync 3.x `progress.c` behavior (GPLv3, read for behavior
only — this is a text-rendering format, not a wire codec).

## Scope and the one load-bearing fact

`--progress` and `--info=progress2` are **client-local display only**. They render running byte
counters this client already produces; **they exchange no new bytes with the server** and are never
added to the server argv (`ServerArgvBuilder` is untouched). Consequence, same as P12 local copy:
**no capture / no live-interop byte-exact gate applies** — hermetic tests are the gate. The only
thing pinned by real rsync here is the *console text*, not any wire byte.

Both flags work on all three transfer paths: ssh/daemon **pull**, ssh/daemon **push**, and
**local-to-local** copy.

## Two modes

| Flag | Mode | Line(s) |
|---|---|---|
| `--progress` | per-file | one filename line + one progress line **per transferred file** |
| `--info=progress2` | whole-transfer | a **single** progress line for the entire run |

If both are given, `--info=progress2` wins (matches rsync).

## The progress line

```
<bytes> <pct>% <rate> <time>[ (xfr#<n>, to-chk=<rem>/<total>)]
```

- `<bytes>` — cumulative bytes: this file (per-file) or the whole transfer (progress2). Grouped with
  invariant `,` thousands separators, right-aligned to width **15**.
- `<pct>` — `floor(100 * bytes / size)`, right-aligned width **3**, then `%`. A zero-length target is
  reported as `100%`.
- `<rate>` — `{value:F2}{unit}` with 1024-scaled unit ∈ `B/s kB/s MB/s GB/s TB/s`, `value ∈ [0,1024)`,
  right-aligned width **10** (e.g. `  47.68MB/s`). Computed from a trailing sliding-window sample of
  cumulative bytes vs elapsed time.
- `<time>` — `H:MM:SS` (hours un-padded, minutes/seconds zero-padded). **While transferring:** ETA =
  `remaining / rate`. **On completion:** elapsed time. Unknown/zero rate → `0:00:00`.
- `(xfr#<n>, to-chk=<rem>/<total>)` — appended when a file completes (per-file) or on the final line
  (progress2). `n` = files transferred so far; `total` = number of regular files considered;
  `rem` = `total - n`. This is a monotone approximation of rsync's generator-position `to-chk`
  semantics (rsync counts the generator's remaining list position, which this client does not model);
  the numbers agree at start (`rem == total`) and end (`rem == 0`).

Per-file mode prints the file's wire name (`/`-separated) on its own line **before** that file's
progress line.

## Rendering channel and TTY behavior

- Written to **stderr** (consistent with this client's existing end-of-run summary lines, and keeps
  the carriage-return spam out of a piped stdout, which daemon MOTD / module listings use).
- **TTY (`!Console.IsErrorRedirected`):** the progress line is rewritten in place with a leading
  `\r`; a newline is emitted when the file (per-file) or the whole transfer (progress2) completes.
- **Redirected:** no `\r` animation — only the completed line(s) are written, each newline-terminated.

## Deviations from stock rsync (intentional, v1)

- Column spacing/rounding follow the layout above but are **not asserted byte-for-byte** against a
  captured rsync `--progress` run. Pinning that is an optional follow-up (capture the real stdout via
  `/capture-vectors` and add a golden string test); it changes no wire behavior.
- `-P` (= `--partial --progress`) is **not** accepted — `--partial`'s keep-partial-file-on-failure
  semantics are a receiver behavior change not yet implemented; `-P` stays rejected (exit 1).
- Push updates per-file (one 0→100% step per file), not per-block: the push sender reads each source
  file whole before matching, so there is no intra-file byte cursor to animate. Pull and local update
  per-block.
