---
name: capture-vectors
description: Capture or extend rsync golden vectors in a throwaway Docker container — the only sanctioned way to run a real rsync. Use when a phase needs new wire captures, a decisive experiment, or fixture regeneration.
---

# Capture golden vectors from a real rsync

## Hard rules

- rsync runs **only** inside a throwaway container (alpine:3.21, rsync 3.4.3, protocol 32). Never install it on the Windows host.
- Every checked-in vector must be regenerable: new scenarios go **into `test-fixtures/capture/capture.sh`**, not one-off scripts. For a quick decisive experiment, a standalone script under `test-fixtures/capture/` is fine — but merge the scenario into capture.sh (and delete the script) before commit.
- The checksum seed is time-based by design → wire captures differ per run; tests must read the seed out of the capture, never assume it.
- File contents/mtimes must be deterministic: `detbytes` (aes-128-ctr keystream, fixed passphrase) + `touch -d`, `TZ=UTC`.

## Run

```bash
# Bash tool: MSYS_NO_PATHCONV=1 is REQUIRED — Git Bash rewrites /work into
# C:/Program Files/Git/work and the script won't be found
MSYS_NO_PATHCONV=1 docker run --rm -v "C:\Hantek\Project\rsync-win\test-fixtures:/work" alpine:3.21 sh /work/capture/capture.sh
```

Full regen rewrites **all** of `test-fixtures/vectors/` (capture.sh starts with `rm -rf`). That is safe — recipes are deterministic — but re-run the hermetic suite afterwards to confirm nothing pinned drifted. For an experiment, write a standalone script that only creates new vector dirs.

## Capture technique

- **ssh-style**: fake remote-shell wrapper (`-e fakessh.sh`) tees both raw directions to `c2s.bin` / `s2c.bin` and logs the exact `--server` argv. Byte-identical to a real ssh session.
- **daemon-style**: socat tap between client and rsyncd.
- Ambiguities are settled by **decisive experiments**: construct two inputs whose captured bytes discriminate the hypotheses (e.g. `ssh31-pull-partial`: one file stale in content+mtime vs one stale in mtime only → isolates the iflags rules). Never settle by reading canonical rsync harder — it's GPLv3, behavior reference only.

## After capturing

Record the verified fact in `docs/wire-notes.md` (and the byte layout in the relevant `docs/*-spec.md`) in the same commit as the vectors. Add a comment in capture.sh stating what the scenario proves.
