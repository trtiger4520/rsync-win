# Daemon transport spec (`rsync://`, TCP 873) — P8

Status: **MEASURED** against rsync 3.4.3 (alpine:3.21), vectors `daemon31-*`, `daemon30-pull-rt`,
`daemon29-auth-pull` under `test-fixtures/vectors/`. Every fact below carries the vector that pins
it. The socat tap records both raw TCP directions; there is no ssh wrapper and no `-e` argv file —
the client argv words appear on the wire itself.

The one-sentence summary: **a daemon session is the ssh session with the two binary version int32s
replaced by a textual line-based preamble.** After the preamble, the byte stream is identical —
`cmp` of `daemon31-pull-rt` against `ssh31-pull-rt` from the respective handshake tails matches
byte-for-byte (only the seed value differs per run).

## 1. Preamble — line-based text conversation

All lines are `\n`-terminated (no `\r`). The preamble is unframed text followed immediately by
binary: **never over-consume past a `\n`** — the byte after `@RSYNCD: OK\n` is already the compat
varint. Read lines by scanning the pipe buffer and consuming only through the newline.

Sequence (client perspective):

1. **Server greeting** — `@RSYNCD: <ver>.<sub>[ <digest list>]\n`.
   MEASURED: `@RSYNCD: 32.0 md5 md4` from 3.4.3. The digest list is the daemon-auth digest
   preference, space-separated. Older daemons may omit it.
2. **Client greeting** — `@RSYNCD: <ourver>.0 md5 md4\n`.
   MEASURED: `--protocol=31` lowers the advertised version to `31.0` (daemon31-pull-rt); 30 and 29
   keep the same shape — `.0` subversion and the digest list are present even at 29
   (daemon30-pull-rt, daemon29-auth-pull). Session protocol = `min(local, server ver)`, same sanity
   ceiling (40) as ssh. **Our floor for daemon sessions is 30**: the protocol-29 preamble facts are
   pinned (greeting, `\n`-separated argv), but the 29 binary phase (1-byte flags, 4-byte mtime, no
   varints) is unimplemented — `DaemonPreamble` rejects a negotiated version < 30 with a clear
   error instead of desyncing mid-flist.
3. **Module request** — `<module>\n`, or a bare empty line `\n` to request the module listing.
   MEASURED (daemon31-modlist): the real client sends the empty line, not `#list`.
4. **Server verdict lines**, zero or more text lines then one terminal line:
   - **motd**: raw text lines, no prefix, possibly including blank lines, before the verdict
     (daemon31-motd-list: motd lines sit between greeting and listing output).
   - `@RSYNCD: AUTHREQD <challenge>\n` — see §2, then the verdict continues after our reply.
   - `@RSYNCD: OK\n` — module accepted, preamble over, binary phase next.
   - `@ERROR: <text>\n` — fatal; server closes. Client prints the line to stderr and exits **5**
     (daemon31-badmodule: `@ERROR: Unknown module 'nonexistent'`, exit 5; daemon31-auth-fail:
     `@ERROR: auth failed on module secret`, exit 5).
   - Module listing (after empty module line): one line per module, `printf "%-15s\t%s\n"` shape —
     name space-padded to width 15, TAB, comment (TAB present even when the comment is empty) —
     terminated by `@RSYNCD: EXIT\n` and connection close, client exit 0 (daemon31-modlist).

## 2. Authentication (challenge–response)

MEASURED (daemon31-auth-pull, computationally reproduced; also daemon31-auth-fail and
daemon29-auth-pull):

- Challenge: 22 chars of standard-alphabet base64 (16 raw random bytes, trailing `==` stripped).
- Client reply line: `<user> <digest>\n` where
  **`digest = base64(MD5(password_bytes + challenge_bytes))` with trailing `=` stripped** —
  password first, challenge appended, no newline included, plain MD5 (seed-free), standard base64
  alphabet. 22 chars.
- Digest choice: of the greeting's advertised list (`md5 md4`), 3.4.3 uses **md5** — even at
  protocol 29 (daemon29-auth-pull digest matches MD5, not MD4). We implement md5 only and fail
  with a clear error if the server's list has no `md5`.
- The module line goes out **before** the challenge arrives; AUTHREQD is the server's response to
  requesting a guarded module.
- Password source: `RSYNC_PASSWORD` environment variable (CLI scope for P8; no prompt).

## 3. Server argv

After `@RSYNCD: OK\n` the client sends the server argv (the words the ssh transport would place
after `rsync` on the remote command line, minus the program name):

- **Protocol ≥ 30**: each arg NUL-terminated, list terminated by an **empty string** (a lone NUL).
  MEASURED: `--server\0--sender\0-tre.LsfxCIvu\0.\0tree/\0\0` (daemon31-pull-rt).
- **Protocol 29**: `\n`-separated instead, and the short-flags word has no checksum-choice suffix
  (`-tr`, no `.xxxx`) (daemon29-auth-pull).
- Push: `--server` **without** `--sender`, same as ssh (daemon31-push-rt).
- The path argument is **module-relative**: `tree/`, `push/` — not the daemon-side filesystem path.
  Module root itself = `<module>/`.

## 4. Binary phase

Identical to the ssh session after its version exchange (pinned by `cmp` against ssh31 vectors):

| step | direction | daemon31-pull-rt offset |
|---|---|---|
| checksum-offer vstring | c2s | right after argv terminator |
| compat_flags varint (`81 fe` = 0x1FE) | s2c | 0x22 (right after `OK\n`) |
| server checksum vstring | s2c | 0x24 |
| checksum_seed int32 LE | s2c | 0x43 — **pre-mux, last before mux** |
| server mux-out starts | s2c | 0x47 (first tag-7 flist frame) |
| client mux-out starts | c2s | first byte after the offer vstring (filter int32 0 in-mux on pull; no filter list on push — both same as ssh) |

No compress vstring is exchanged without `-z` (single negotiation string only). DONE choreography,
flist, delta, redo: byte-structurally identical to the ssh captures in both directions
(daemon31-push-rt / daemon31-push-uptodate mirror ssh31-push-rt / ssh31-push-uptodate exactly).

## 5. Post-OK errors are in-mux, with a real exit code

MEASURED (daemon31-push-readonly): pushing to a read-only module passes the preamble (`OK`,
compat, seed), then the server sends **MSG_ERROR (tag 8)** text frames
(`ERROR: module is read only`) followed by **MSG_ERROR_EXIT (tag 93 = MPLEX_BASE 7 + 86)** with a
4-byte LE payload carrying the exit code (`01 00 00 00` → exit **1**). The protocol-31 client
echoes an empty (len-0) MSG_ERROR_EXIT frame back and exits with the received code. So:

- preamble `@ERROR` → exit 5 (start-protocol failure)
- in-mux MSG_ERROR_EXIT → exit with the carried code (read-only module push → 1)

## 6. Traps

- **Over-reading the preamble** — the compat varint follows `OK\n` with no separator; a buffered
  text reader that swallows bytes past the newline desyncs the handshake.
- The greeting digest list is **auth** negotiation, unrelated to the in-band checksum-algorithm
  vstrings that follow OK.
- The argv terminator at ≥30 is an empty NUL-terminated string (`...\0` + `\0`) — forgetting the
  second NUL leaves the server waiting forever (hang, not error).
- `WaitForExitAsync` on TCP carries no rsync status — MSG_ERROR_EXIT and `@ERROR` are the only
  status channels (unlike ssh's remote exit code).
- Module listing and `@ERROR` end with the server closing the socket; treat EOF there as normal,
  not as a mid-stream desync.
