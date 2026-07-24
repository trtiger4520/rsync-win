# RsyncWin

**English** · [繁體中文](README.zh-TW.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

> **⚠️ Experimental / Personal Project:** This tool was developed with AI assistance for
> personal use/learning purposes. It is provided as-is under the MIT License, without warranty,
> and is **not actively maintained**.

**RsyncWin is a native Windows rsync client written in C# / .NET 10.** Instead of porting rsync's
code, it implements the rsync wire protocol directly (protocol 31, negotiating down to 30/29), so
Windows machines can sync byte-for-byte with **stock rsync servers** — **no WSL, no Cygwin, and no
rsync binary required**.

## Download & install

**Prebuilt executable (recommended):** download `rsyncwin-<version>-win-x64.zip` from the
[Releases](https://github.com/trtiger4520/rsync-win/releases) page, unzip it, and run
`rsyncwin.exe`. It is a self-contained single file — no separate .NET runtime required.

**Build from source:** requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
# Run directly
dotnet run --project src/RsyncWin.Cli -- -rt user@example.com:/srv/data/ D:\backup\data

# Produce a self-contained single-file executable
dotnet publish src/RsyncWin.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

**Requirements:** Windows x64. The SSH transport uses the in-box
`C:\Windows\System32\OpenSSH\ssh.exe`, so no extra SSH client is needed. (Docker is only required
for the live interop tests, not for normal use.)

## Usage

`rsyncwin` always takes **one local path and one remote path**; the direction is decided by **which
side is remote** — remote as the source means pull, remote as the destination means push. A remote
path can be written three ways:

| Form                                       | Meaning                                             |
| ------------------------------------------ | --------------------------------------------------- |
| `[user@]host:path`                         | Over SSH (single colon, uses the in-box `ssh.exe`)  |
| `rsync://[user@]host[:port]/module[/path]` | Over the rsync daemon (default port 873)            |
| `[user@]host::module[/path]`               | Shorthand for the same                              |

Windows paths are never misparsed: `D:\backup`, `D:/backup`, and `\\server\share` are always treated
as local paths.

### Common examples

```powershell
# SSH pull: pull a whole directory tree from a remote (recursive + preserve times)
rsyncwin -rt user@example.com:/srv/data/ D:\backup\data

# SSH push: push a local directory to a remote
rsyncwin -rt D:\projects\site\ user@example.com:/var/www/site/

# Anonymous daemon pull
rsyncwin -rt rsync://mirror.example.com/debian/ D:\mirror\debian

# Daemon password auth (password via environment variable, not the command line)
$env:RSYNC_PASSWORD = "opensesame"
rsyncwin -rt rsync://alice@host/secret/ D:\secret

# List the modules a daemon offers
rsyncwin rsync://mirror.example.com/
```

### Flags at a glance

| Flag | Long form         | Description                                                        | pull | push |
| ---- | ----------------- | ----------------------------------------------------------------- | :--: | :--: |
| `-r` | `--recursive`     | Recurse into subdirectories                                       |  ✅  |  ✅  |
| `-t` | `--times`         | Preserve modification times (on by default)                       |  ✅  |  ✅  |
| `-a` | `--archive`       | Archive mode (implies -r -t plus links/owner/group/device/perms)  |  ✅  |  ❌  |
| `-c` | `--checksum`      | Compare by whole-file checksum instead of mtime+size              |  ✅  |  ✅  |
|      | `--delete`        | Delete files on the destination that the source no longer has     |  ✅  |  ✅  |
| `-s` | `--secluded-args` | Protect remote paths containing spaces / special characters       |  ✅  |  ✅  |
| `-z` | `--compress`      | Compress the transferred token stream with zlibx                  |  ✅  |  ✅  |
| `-e` | `--rsh`           | Remote-shell command, args allowed e.g. `-e "ssh -p 2222"`        |  ✅  |  ✅  |
| `-h` | `--help`          | Show the built-in help (usage forms + all options) and exit 0     |  —  |  —  |

Short flags can be combined: `-rtcz`, `-rtz --delete`, and so on. For each flag's key behavior, the
rsync numeric exit-code table, passwords / credentials, and troubleshooting, see the
[usage guide](docs/zh-TW/02-使用說明.md) (Traditional Chinese).

## Features & scope

Delta transfer sends only the changed blocks; interoperability has been validated against a live
rsync 3.4.3 / 3.4.4 interop matrix. It is CLI-only, with no GUI. The items deliberately left out of
scope, with rationale (protocol-29-generation file lists, zstd / old zlib, the extra `-a` fields on
push, files larger than 2 GiB, and so on), are documented in the
[project overview](docs/zh-TW/01-專案說明.md) (Traditional Chinese).

## Documentation

User-facing guides are in Traditional Chinese; the byte-level protocol references are in English.

### Traditional Chinese (start here)

| #   | Document                                                     | Contents                                                                            |
| --- | ----------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| —   | [docs/zh-TW/README.md](docs/zh-TW/README.md)                | Traditional Chinese doc index                                                       |
| 1   | [專案說明](docs/zh-TW/01-專案說明.md)                        | Positioning, problem solved, scope, core design decisions, method, status           |
| 2   | [使用說明](docs/zh-TW/02-使用說明.md)                        | Command-line syntax, the four transport combinations, flags, exit codes, troubleshooting |
| 3   | [結構與算法說明](docs/zh-TW/03-結構與算法說明.md)            | Five-layer architecture, wire codecs, checksums, delta transfer, compression, engine, Windows filesystem |
| 4   | [與 rsync 銜接程度說明](docs/zh-TW/04-與rsync銜接程度說明.md) | Interop level, handshake, byte-exact facts, `-z` / `--delete` / `-s` behavior, boundaries |

### English (byte-level protocol facts)

- [docs/roadmap.md](docs/roadmap.md) — phase plan, status table with commit evidence, and the mandatory per-phase working method
- [docs/wire-notes.md](docs/wire-notes.md) — verified protocol facts, provenance, and the trap list
- [docs/codec-spec.md](docs/codec-spec.md), [flist-spec.md](docs/flist-spec.md),
  [transfer-spec.md](docs/transfer-spec.md), [daemon-spec.md](docs/daemon-spec.md) — per-layer byte layouts
- [docs/interop-matrix.md](docs/interop-matrix.md) — live interop matrix (rsync 3.4.3 / 3.4.4)
- [docs/acceptance-report.md](docs/acceptance-report.md) — overall acceptance report

## Testing

```powershell
dotnet test --filter "Category!=Interop"   # fast hermetic tests (no rsync needed)
dotnet test --filter "Category=Interop"    # live interop tests against a real rsync (needs Docker)
```

The release workflow runs only the cross-platform pure-core tests (`RsyncWin.Protocol.Tests`) as its
release gate; run the full hermetic suite locally before tagging. For the verification method, see the
working-method section of [docs/roadmap.md](docs/roadmap.md).

## License & provenance

This project's own code is licensed under the **MIT License** (see [LICENSE](LICENSE)).

It contains **no GPL rsync code**: canonical rsync (GPLv3) is used as a **behavior** reference only,
while expression-level content is taken from the BSD/ISC-licensed
[openrsync](https://github.com/kristapsdz/openrsync) and
[gokrazy/rsync](https://github.com/gokrazy/rsync), or regenerated from the documented algorithm.
