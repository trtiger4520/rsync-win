#!/bin/sh
# Golden-vector capture harness for RsyncWin.
#
# Runs inside a throwaway alpine:3.21 container (rsync 3.4.3, protocol 32) and captures
# ground-truth bytes from a REAL rsync. Everything under test-fixtures/vectors/ is produced
# by this script; regenerate with:
#
#   docker run --rm -v "<repo>/test-fixtures:/work" alpine:3.21 sh /work/capture/capture.sh
#
# The "ssh" captures use a fake remote-shell wrapper (-e) that pipes the client to a local
# `rsync --server` through tee(1), recording both raw directions byte-for-byte. This is
# byte-identical to a real ssh session: ssh is a dumb pipe, and the wrapper also logs the
# exact --server argv the client generates (ServerArgvBuilder golden files).
#
# Determinism: file contents come from openssl aes-128-ctr keystreams with fixed passphrases;
# mtimes are fixed with touch -d; TZ=UTC. The checksum_seed is time-based by design, so wire
# captures differ per run — tests must read the seed out of the capture, never assume it.
set -eu
export TZ=UTC

OUT=/work/vectors
rm -rf "$OUT"
mkdir -p "$OUT"

# coreutils: GNU touch/stat for the nanosecond-mtime scenarios (busybox truncates fractions)
apk add --no-cache rsync openssl socat coreutils >/dev/null 2>&1

{
  echo "generated: (see git log; timestamps deliberately not embedded)"
  echo "image: alpine:3.21"
  rsync --version | head -n 2
  echo "capture method: test-fixtures/capture/capture.sh"
} > "$OUT/meta.txt"

# Deterministic pseudo-random bytes: aes-128-ctr keystream over /dev/zero, fixed passphrase.
detbytes() { # $1 = size, $2 = passphrase label
  openssl enc -aes-128-ctr -pass "pass:$2" -nosalt </dev/zero 2>/dev/null | head -c "$1"
}

# ---------------------------------------------------------------------------
# 1. BlockSizer sweep: sum_sizes_sqroot ground truth across file sizes.
#    Sparse files are fine -- sizing depends only on length. timeout+grep lets
#    us bail as soon as the generator prints the line for the huge sizes.
# ---------------------------------------------------------------------------
mkdir -p /t/bs
: > "$OUT/blocksizer_deltasum2.txt"
for size in 1 100 699 700 701 1000 4096 65536 100000 300000 490001 999999 \
            1000000 2000000 10000000 123456789 1073741824 8589934592 21474836480; do
  truncate -s "$size" /t/bs/src
  truncate -s "$size" /t/bs/dst
  line=$(timeout 30 rsync -I --no-whole-file --debug=deltasum2 /t/bs/src /t/bs/dst 2>&1 \
         | grep -m1 'count=' || true)
  if [ -n "$line" ]; then
    echo "$line" >> "$OUT/blocksizer_deltasum2.txt"
  else
    echo "MISSING flength=$size" >> "$OUT/blocksizer_deltasum2.txt"
  fi
  rm -f /t/bs/src /t/bs/dst
done

# ---------------------------------------------------------------------------
# 2. Rolling-checksum vectors: per-chunk weak sums (sum1) over KNOWN bytes.
#    The generator hashes the DEST file; dest is an identical copy of the blob
#    we check in, so each chunk[i] sum1 corresponds to bytes we have.
# ---------------------------------------------------------------------------
mkdir -p /t/roll "$OUT/rolling"
for spec in "4096 blob4k" "65536 blob64k" "300000 blob300k"; do
  size=${spec% *}; name=${spec#* }
  detbytes "$size" "rsyncwin-$name" > "/t/roll/$name.bin"
  cp "/t/roll/$name.bin" "$OUT/rolling/$name.bin"
  cp "/t/roll/$name.bin" /t/roll/dst.bin
  rsync -I --no-whole-file --debug=deltasum4 "/t/roll/$name.bin" /t/roll/dst.bin 2>&1 \
    | grep -E '^(count=|chunk\[)' > "$OUT/rolling/$name.sums.txt"
  rm -f /t/roll/dst.bin
done

# ---------------------------------------------------------------------------
# 3. Deterministic source tree for wire captures.
# ---------------------------------------------------------------------------
mkdir -p /t/tree/subdir
printf 'hello rsync\n'            > /t/tree/b001_small.txt
: >                                 /t/tree/b000_empty
detbytes 65536  tree64k           > /t/tree/b002_64k.bin
detbytes 300000 tree300k          > /t/tree/b003_300k.bin
printf 'unicode name\n'           > "/t/tree/b004_中文檔名.txt"
printf 'space name\n'             > "/t/tree/b005 name with space.txt"
printf 'nested\n'                 > /t/tree/subdir/nested.txt
touch -d '2020-01-02 03:04:05' /t/tree/b000_empty /t/tree/b001_small.txt
touch -d '2021-06-15 12:00:00' /t/tree/b002_64k.bin /t/tree/b003_300k.bin
touch -d '2022-12-31 23:59:59' "/t/tree/b004_中文檔名.txt" "/t/tree/b005 name with space.txt"
touch -d '2020-01-02 03:04:05' /t/tree/subdir/nested.txt /t/tree/subdir /t/tree

# Size/mtime probe tree: entries differ ONLY in the field being probed, so the
# varlong bytes are isolated in the flist. Sparse files keep it instant.
mkdir -p /t/sizes
i=0
for size in 0 1 127 128 255 256 65535 65536 8388607 8388608 16777215 16777216 \
            2147483647 2147483648 4294967295 4294967296 5000000000 1099511627775; do
  truncate -s "$size" "$(printf '/t/sizes/v%02d' $i)"
  i=$((i+1))
done
touch -d '2020-01-02 03:04:05' /t/sizes/v* /t/sizes
# mtime probes: same size, varied mtime (incl. one past 2^31 for the 64-bit path)
for spec in "m0 1970-01-01 00:00:01" "m1 2001-09-09 01:46:40" \
            "m2 2020-01-02 03:04:05" "m3 2106-02-07 06:28:15"; do
  name=${spec%% *}; when=${spec#* }
  printf 'mtime probe\n' > "/t/sizes/$name"
  touch -d "$when" "/t/sizes/$name"
done

{
  echo "# name<TAB>size<TAB>mtime-epoch  (as created; flist captures must decode to these)"
  for f in /t/sizes/* /t/tree/* /t/tree/subdir/*; do
    [ -e "$f" ] || continue
    printf '%s\t%s\t%s\n' "$f" "$(stat -c %s "$f")" "$(stat -c %Y "$f")"
  done
} > "$OUT/probe-manifest.tsv"

# ---------------------------------------------------------------------------
# 4. "ssh" transport captures via fake remote-shell wrapper.
# ---------------------------------------------------------------------------
mkdir -p /cap
cat > /cap/fakessh.sh <<'EOF'
#!/bin/sh
# Invoked by the rsync client as: fakessh.sh <host> rsync --server ...
# Records both raw protocol directions and the exact server argv.
#
# The naive `tee | "$@" | tee` deadlocks at session end: the first tee waits for
# EOF on the client pipe while the client waits for this wrapper to exit. A fifo
# decouples them — when rsync --server exits, the pipeline finishes and the
# wrapper exits; the orphaned background tee dies on client EOF. Background jobs
# get stdin rebound to /dev/null (POSIX), so stash the real stdin in fd 3 first.
shift                                   # drop the host argument
printf '%s\n' "$@" > "$CAPDIR/argv.txt"
exec 3<&0
mkfifo "/tmp/fifo.$$"
tee "$CAPDIR/c2s.bin" <&3 > "/tmp/fifo.$$" &
"$@" < "/tmp/fifo.$$" 2>"$CAPDIR/server-stderr.txt" | tee "$CAPDIR/s2c.bin"
rc=$?
rm -f "/tmp/fifo.$$"
exit $rc
EOF
chmod +x /cap/fakessh.sh

sshcap() { # $1 = capture name; rest = client rsync args
  name=$1; shift
  export CAPDIR="$OUT/$name"
  mkdir -p "$CAPDIR"
  printf 'client: rsync %s\n' "$*" > "$CAPDIR/client-cmd.txt"
  rc=0
  timeout 60 rsync -e /cap/fakessh.sh "$@" \
    >"$CAPDIR/client-stdout.txt" 2>"$CAPDIR/client-stderr.txt" || rc=$?
  echo "exit=$rc" >> "$CAPDIR/client-cmd.txt"
}

# Pull, minimal v1-like flags, per protocol version
for p in 31 30 29; do
  rm -rf /t/pull && mkdir -p /t/pull
  sshcap "ssh$p-pull-rt" --protocol=$p -rt --no-inc-recursive fake:/t/tree/ /t/pull/
done

# Pull with archive flags (reference for ServerArgvBuilder)
rm -rf /t/pull && mkdir -p /t/pull
sshcap "ssh31-pull-a" --protocol=31 -a --no-inc-recursive fake:/t/tree/ /t/pull/

# List the size/mtime probe tree (varlong vectors in flist)
sshcap "ssh31-sizes-list" --protocol=31 -rt --no-inc-recursive --list-only fake:/t/sizes/

# Fast-path pulls (P5): destination already (partially) up to date. Up-to-date
# files produce ZERO generator bytes — no ndx, no iflags; only changed files are
# requested. Verified 2026-07: uptodate c2s logical stream = filter-list int 0
# plus the five NDX_DONE bytes and nothing else.
rm -rf /t/pull && mkdir -p /t/pull
cp -a /t/tree/. /t/pull/
sshcap "ssh31-pull-uptodate" --protocol=31 -rt --no-inc-recursive fake:/t/tree/ /t/pull/

# One file stale in content+mtime (iflags 0x800C), one identical but older
# mtime (iflags 0x8008). --whole-file keeps the sum heads all-zero, matching
# what our generator emits before P6 delta support.
rm -rf /t/pull && mkdir -p /t/pull
cp -a /t/tree/. /t/pull/
printf 'stale\n' > /t/pull/b001_small.txt
touch -d '2019-01-01 00:00:00' /t/pull/b001_small.txt
touch -d '2019-01-01 00:00:00' /t/pull/b002_64k.bin
sshcap "ssh31-pull-partial" --protocol=31 -rt --no-inc-recursive --whole-file fake:/t/tree/ /t/pull/

# Delta pull: basis differs from source at known offsets -> real sum header,
# block sums, matched tokens and literals in the capture.
rm -rf /t/delta && mkdir -p /t/delta
cp /t/tree/b003_300k.bin /t/delta/b003_300k.bin
printf 'XXXXXXXX' | dd of=/t/delta/b003_300k.bin bs=1 seek=1000 conv=notrunc 2>/dev/null
printf 'YYYY'     | dd of=/t/delta/b003_300k.bin bs=1 seek=150000 conv=notrunc 2>/dev/null
touch -d '2019-01-01 00:00:00' /t/delta/b003_300k.bin
sshcap "ssh31-pull-delta" --protocol=31 -t --no-whole-file fake:/t/tree/b003_300k.bin /t/delta/
cp /t/delta/b003_300k.bin "$OUT/ssh31-pull-delta/result.bin" 2>/dev/null || true

# Redo capture (P6): corrupt the basis mid-transfer so the receiver's
# reconstruction mismatches the sender's whole-file sum -> server redo phase.
# 4 MiB source; basis differs in the first 256 KiB (early literals) with an
# older mtime; --bwlimit=200 stretches the transfer to ~20 s and 1 s in a tail
# block of the basis is corrupted. The generator computed its sums at t~0 from
# the clean basis, the receiver copies the matched tail from the corrupted one.
# Verified: exit 0, result byte-identical to source, c2s carries BOTH the
# phase-0 sums and the full-length phase-1 redo sums.
mkdir -p /t/redo /t/redodst
detbytes 4194304 redo4m > /t/redo/big.bin
touch -d '2023-05-05 05:05:05' /t/redo/big.bin
cp /t/redo/big.bin /t/redodst/big.bin
detbytes 262144 redobasis | dd of=/t/redodst/big.bin bs=1024 count=256 conv=notrunc 2>/dev/null
touch -d '2019-01-01 00:00:00' /t/redodst/big.bin
( sleep 1; printf 'ZZZZ' | dd of=/t/redodst/big.bin bs=1 seek=4100000 conv=notrunc 2>/dev/null ) &
CORRUPTER=$!
sshcap "ssh31-pull-redo" --protocol=31 -t --no-whole-file --bwlimit=200 fake:/t/redo/big.bin /t/redodst/
wait $CORRUPTER 2>/dev/null || true
cp /t/redodst/big.bin "$OUT/ssh31-pull-redo/result.bin"

# P9: --checksum (-c). Under -c the sender appends a 16-byte xxh128 F_SUM to
# every regular-file flist entry (absent on dirs), and the transfer decision is
# by content, not mtime+size. Dest discriminators: a_match = identical content +
# stale mtime (-c fixes only the time, no data transfer); b_fast = SAME size+mtime
# but DIFFERENT content (the plain fast path skips it, -c must transfer it);
# c_new = present only in src. Verified: c2s a_match=0x0008 (REPORT_TIME),
# b_fast=0x8002 (TRANSFER|REPORT_CHANGE)+real sum head, c_new=0xa002+null head.
rm -rf /t/c1src /t/c1dst
mkdir -p /t/c1src/subdir
printf 'hello rsync\n'      > /t/c1src/a_match.txt
detbytes 65536 c1blob       > /t/c1src/b_fast.bin
printf 'brand new file\n'   > /t/c1src/c_new.txt
printf 'nested\n'           > /t/c1src/subdir/nested.txt
touch -d '2021-06-15 12:00:00' /t/c1src/a_match.txt /t/c1src/b_fast.bin \
      /t/c1src/c_new.txt /t/c1src/subdir/nested.txt /t/c1src/subdir /t/c1src
mkdir -p /t/c1dst/subdir
printf 'hello rsync\n'      > /t/c1dst/a_match.txt
touch -d '2019-01-01 00:00:00' /t/c1dst/a_match.txt
detbytes 65536 c1blobDIFF   > /t/c1dst/b_fast.bin
touch -d '2021-06-15 12:00:00' /t/c1dst/b_fast.bin
printf 'nested\n'           > /t/c1dst/subdir/nested.txt
touch -d '2021-06-15 12:00:00' /t/c1dst/subdir/nested.txt /t/c1dst/subdir /t/c1dst
mkdir -p "$OUT/ssh31-pull-checksum"
cp /t/c1dst/b_fast.bin "$OUT/ssh31-pull-checksum/dst-b_fast.bin" # pre-pull basis for the hermetic full-session replay
sshcap "ssh31-pull-checksum" --protocol=31 -rtc --no-inc-recursive fake:/t/c1src/ /t/c1dst/

# P9: --delete on a pull. The client is the receiver, so deletion is LOCAL: the
# server argv carries no --delete, the filter list stays the empty int32 0, and
# NO NDX_DEL_STATS crosses the wire in either direction (choreographically a
# plain pull — the del-stats live only in the local receiver). Dest holds one
# extraneous regular file and one extraneous dir with a file inside.
rm -rf /t/c2src /t/c2dst
mkdir -p /t/c2src/keepdir
printf 'keep one\n' > /t/c2src/keep1.txt
printf 'keep two\n' > /t/c2src/keepdir/keep2.txt
touch -d '2021-06-15 12:00:00' /t/c2src/keep1.txt /t/c2src/keepdir/keep2.txt \
      /t/c2src/keepdir /t/c2src
mkdir -p /t/c2dst/keepdir /t/c2dst/extradir
printf 'keep one\n' > /t/c2dst/keep1.txt
printf 'keep two\n' > /t/c2dst/keepdir/keep2.txt
printf 'extraneous\n' > /t/c2dst/extra.txt
printf 'inside extra\n' > /t/c2dst/extradir/inside.txt
touch -d '2021-06-15 12:00:00' /t/c2dst/keep1.txt /t/c2dst/keepdir/keep2.txt \
      /t/c2dst/keepdir /t/c2dst/extra.txt /t/c2dst/extradir/inside.txt /t/c2dst/extradir
sshcap "ssh31-pull-delete" --protocol=31 -rt --delete --no-inc-recursive fake:/t/c2src/ /t/c2dst/

# Push (sender-side capture; P7 reference, free to record now)
rm -rf /t/pushdst && mkdir -p /t/pushdst
sshcap "ssh31-push-rt" --protocol=31 -rt --no-inc-recursive /t/tree/ fake:/t/pushdst/

# P7 push experiments. E1: re-push an identical tree — proves the server-side
# mtime+size fast path (zero generator requests; s2c is handshake + 5 DONEs).
rm -rf /t/pushdst && mkdir -p /t/pushdst
cp -a /t/tree/. /t/pushdst/
sshcap "ssh31-push-uptodate" --protocol=31 -rt --no-inc-recursive /t/tree/ fake:/t/pushdst/

# E2: push with a stale server basis (same offsets as ssh31-pull-delta). Pins
# the s2c sum head + block sums arriving at the sender AND the sender's exact
# token stream (match greediness, literal chunking, remainder block, trailer)
# — the MatchSearcher byte gate.
rm -rf /t/pushdelta && mkdir -p /t/pushdelta
cp /t/tree/b003_300k.bin /t/pushdelta/b003_300k.bin
printf 'XXXXXXXX' | dd of=/t/pushdelta/b003_300k.bin bs=1 seek=1000 conv=notrunc 2>/dev/null
printf 'YYYY'     | dd of=/t/pushdelta/b003_300k.bin bs=1 seek=150000 conv=notrunc 2>/dev/null
touch -d '2019-01-01 00:00:00' /t/pushdelta/b003_300k.bin
sshcap "ssh31-push-delta" --protocol=31 -t --no-whole-file /t/tree/b003_300k.bin fake:/t/pushdelta/
cp /t/pushdelta/b003_300k.bin "$OUT/ssh31-push-delta/result.bin"

# E3: induced-mismatch redo, push direction (pull-redo trick with roles
# swapped: the basis lives on the SERVER; corrupt it mid-transfer so the
# server receiver's reconstruction fails the whole-file sum and the server
# generator re-requests in phase 1). Pins the sender-side redo: full-length
# re-request between DONE#1/#2, sums recomputed from the current on-disk
# basis, ndx codec state persisting across phases in both directions.
mkdir -p /t/pushredo-src /t/pushredo-dst
detbytes 4194304 redo4m > /t/pushredo-src/big.bin
touch -d '2023-05-05 05:05:05' /t/pushredo-src/big.bin
cp /t/pushredo-src/big.bin /t/pushredo-dst/big.bin
detbytes 262144 redobasis | dd of=/t/pushredo-dst/big.bin bs=1024 count=256 conv=notrunc 2>/dev/null
touch -d '2019-01-01 00:00:00' /t/pushredo-dst/big.bin
( sleep 1; printf 'ZZZZ' | dd of=/t/pushredo-dst/big.bin bs=1 seek=4100000 conv=notrunc 2>/dev/null ) &
CORRUPTER=$!
sshcap "ssh31-push-redo" --protocol=31 -t --no-whole-file --bwlimit=200 /t/pushredo-src/big.bin fake:/t/pushredo-dst/
wait $CORRUPTER 2>/dev/null || true
cp /t/pushredo-dst/big.bin "$OUT/ssh31-push-redo/result.bin"

# E4: fractional source mtime — proves the sender emits XMIT_MOD_NSEC (xflag
# 0x2000, varint nsec between mtime and mode) and that the re-push quick-check
# still passes with nanosecond mtimes (nsec2 transfers nothing). Requires GNU
# touch/stat (coreutils) for sub-second -d.
rm -rf /t/nsrc /t/nsdst && mkdir -p /t/nsrc /t/nsdst
printf 'nsec probe\n' > /t/nsrc/frac.txt
touch -d '2024-03-03 03:03:03.123456789' /t/nsrc/frac.txt
sshcap "ssh31-push-nsec1" --protocol=31 -rt --no-inc-recursive /t/nsrc/ fake:/t/nsdst/
sshcap "ssh31-push-nsec2" --protocol=31 -rt --no-inc-recursive /t/nsrc/ fake:/t/nsdst/
{
  echo "# path size mtime-sec mtime-full (GNU stat)"
  stat -c '%n %s %Y %y' /t/nsrc/frac.txt /t/nsdst/frac.txt
} > "$OUT/ssh31-push-nsec1/nsec-manifest.txt"

# ---------------------------------------------------------------------------
# 4b. P10 ssh captures: --secluded-args (-s), push --checksum/--delete, -z.
# ---------------------------------------------------------------------------

# --secluded-args (-s): the file args are held back from the server argv and sent as a
# NUL-terminated list BEFORE the version int32 -- "rsync\0.\0<remote-path>\0\0" (spacepath is the
# cleanest to decode). Only the remote-side path is sent; spaces are preserved verbatim.
rm -rf /t/pull && mkdir -p /t/pull
sshcap "ssh31-secluded-pull" --protocol=31 -rt -s --no-inc-recursive fake:/t/tree/ /t/pull/
rm -rf /t/spushdst && mkdir -p /t/spushdst
sshcap "ssh31-secluded-push" --protocol=31 -rt -s --no-inc-recursive /t/tree/ fake:/t/spushdst/
mkdir -p "/t/space dir"
printf 'spaced\n' > "/t/space dir/file.txt"
touch -d '2021-06-15 12:00:00' "/t/space dir/file.txt" "/t/space dir"
rm -rf /t/pull2 && mkdir -p /t/pull2
sshcap "ssh31-secluded-spacepath" --protocol=31 -rt -s --no-inc-recursive "fake:/t/space dir/" /t/pull2/

# Push --checksum (-c): our sender appends a 16-byte xxh128 F_SUM to every regular-file flist entry.
rm -rf /t/pcdst && mkdir -p /t/pcdst
sshcap "ssh31-push-checksum" --protocol=31 -rtc --no-inc-recursive /t/tree/ fake:/t/pcdst/

# Push --delete: the remote receiver deletes extraneous entries and reports each via a MSG_DELETED
# mux frame (tag 0x6c), right after the seed, deepest-first -- NO del-stats block on a default
# (delete-during) push. --delete also adds the empty filter list (int32 0) to the sender's c2s.
rm -rf /t/pdsrc /t/pddst
mkdir -p /t/pdsrc/keepdir
printf 'keep one\n' > /t/pdsrc/keep1.txt
printf 'keep two\n' > /t/pdsrc/keepdir/keep2.txt
touch -d '2021-06-15 12:00:00' /t/pdsrc/keep1.txt /t/pdsrc/keepdir/keep2.txt /t/pdsrc/keepdir /t/pdsrc
mkdir -p /t/pddst/keepdir /t/pddst/extradir
printf 'keep one\n' > /t/pddst/keep1.txt
printf 'keep two\n' > /t/pddst/keepdir/keep2.txt
printf 'extraneous\n' > /t/pddst/extra.txt
printf 'inside extra\n' > /t/pddst/extradir/inside.txt
touch -d '2021-06-15 12:00:00' /t/pddst/keep1.txt /t/pddst/keepdir/keep2.txt /t/pddst/keepdir \
      /t/pddst/extra.txt /t/pddst/extradir/inside.txt /t/pddst/extradir
sshcap "ssh31-push-delete" --protocol=31 -rt --delete --no-inc-recursive /t/pdsrc/ fake:/t/pddst/

# -z compression. A mixed tree: a highly compressible repeat file, a run of 'A', an incompressible
# random blob, a small compressible file -- exercises both the deflated and stored-block paths.
mkdir -p /t/ztree
yes 'the quick brown fox jumps over the lazy dog' | head -c 200000 > /t/ztree/repeat.txt
head -c 100000 /dev/zero | tr '\0' 'A'                              > /t/ztree/runs.txt
detbytes 65536 zincompressible                                     > /t/ztree/random.bin
printf 'small compressible payload aaaaaaaaaaaaaaaaaaaa\n'          > /t/ztree/small.txt
touch -d '2021-06-15 12:00:00' /t/ztree/repeat.txt /t/ztree/runs.txt /t/ztree/random.bin /t/ztree/small.txt /t/ztree

# Plain -z negotiates zstd (NOT deflate -- BCL cannot decode it): the client offers a compression
# vstring "zstd lz4 zlibx zlib" right after the checksum vstring. Kept as the "trap" reference.
rm -rf /t/zpull && mkdir -p /t/zpull
sshcap "ssh31-pull-z-default" --protocol=31 -rt -z --no-inc-recursive fake:/t/ztree/ /t/zpull/

# Forced zlibx (--new-compress): NO compression vstring; the choice rides the server arg. This is
# the mode we implement (raw deflate, matched blocks excluded from the window). Full transfer.
rm -rf /t/zpull && mkdir -p /t/zpull
sshcap "ssh31-pull-z-zlibx" --protocol=31 -rt -z --compress-choice=zlibx --no-inc-recursive fake:/t/ztree/ /t/zpull/
( cd /t/ztree && find . -type f -print0 | sort -z | xargs -0 -r sha256sum ) > "$OUT/ssh31-pull-z-zlibx/src-tree.sha256"

# Forced old zlib (--old-compress): inserts matched-block bytes into the deflate window (needs a
# window-insert primitive the BCL lacks) -- kept as the reference for why we do NOT use it. For a
# full transfer with no matches it is byte-identical to zlibx (demuxed).
rm -rf /t/zpull && mkdir -p /t/zpull
sshcap "ssh31-pull-z-zlib" --protocol=31 -rt -z --compress-choice=zlib --no-inc-recursive fake:/t/ztree/ /t/zpull/

# zlibx DELTA pull: basis differs at offsets 1000 and 150000 -> matched blocks (TOKEN_REL/TOKENRUN_REL)
# interleaved with compressed literals (DEFLATED_DATA). Pins the relative-block token arithmetic.
rm -rf /t/zdelta && mkdir -p /t/zdelta
cp /t/tree/b003_300k.bin /t/zdelta/b003_300k.bin
printf 'XXXXXXXX' | dd of=/t/zdelta/b003_300k.bin bs=1 seek=1000 conv=notrunc 2>/dev/null
printf 'YYYY'     | dd of=/t/zdelta/b003_300k.bin bs=1 seek=150000 conv=notrunc 2>/dev/null
touch -d '2019-01-01 00:00:00' /t/zdelta/b003_300k.bin
sshcap "ssh31-pull-z-delta" --protocol=31 -t -z --compress-choice=zlibx --no-whole-file fake:/t/tree/b003_300k.bin /t/zdelta/
cp /t/zdelta/b003_300k.bin "$OUT/ssh31-pull-z-delta/result.bin" 2>/dev/null || true

# zlibx CROSS-RUN pull (protocol-reviewer finding 2): two literal runs sharing the SAME 256-B marker,
# planted far apart (matched blocks between) but close in DEFLATE-input distance (zlibx excludes
# matched blocks from the 32 KB window). rsync's continuous deflate sender back-references run A from
# run B -> a per-run inflate throws; only a CONTINUOUS inflate reconstructs it. Pins that regression.
detbytes 256 crossrun-marker > /t/marker
rm -rf /t/zcr-dst && mkdir -p /t/zcr-dst
detbytes 300000 zcrossrun > /t/src.bin
dd of=/t/src.bin bs=1 seek=1000   conv=notrunc if=/t/marker 2>/dev/null
dd of=/t/src.bin bs=1 seek=150000 conv=notrunc if=/t/marker 2>/dev/null
touch -d '2021-06-15 12:00:00' /t/src.bin
detbytes 300000 zcrossrun > /t/zcr-dst/src.bin   # pristine basis (no markers) -> both regions are literals
touch -d '2019-01-01 00:00:00' /t/zcr-dst/src.bin
mkdir -p "$OUT/ssh31-pull-z-crossrun"
cp /t/zcr-dst/src.bin "$OUT/ssh31-pull-z-crossrun/basis.bin"
sshcap "ssh31-pull-z-crossrun" --protocol=31 -t -z --compress-choice=zlibx --no-whole-file fake:/t/src.bin /t/zcr-dst/
cp /t/zcr-dst/src.bin "$OUT/ssh31-pull-z-crossrun/result.bin"
sha256sum /t/src.bin | awk '{print $1}' > "$OUT/ssh31-pull-z-crossrun/source.sha256"

# zlibx PUSH (our sender compresses; reference only -- deflate output is implementation-defined, so
# this is NOT a byte-exact send gate, only a shape reference).
rm -rf /t/zpushdst && mkdir -p /t/zpushdst
sshcap "ssh31-push-z-zlibx" --protocol=31 -rt -z --compress-choice=zlibx --no-inc-recursive /t/ztree/ fake:/t/zpushdst/

# ---------------------------------------------------------------------------
# 5. Daemon transport captures via socat tap (P8).
# ---------------------------------------------------------------------------
mkdir -p /t/dpush
printf 'alice:opensesame\n' > /etc/rsyncd.secrets
chmod 600 /etc/rsyncd.secrets
cat > /etc/rsyncd.conf <<'EOF'
port = 8730
use chroot = no
[tree]
    path = /t/tree
    read only = yes
[sizes]
    path = /t/sizes
    read only = yes
[push]
    path = /t/dpush
    read only = no
    uid = root
    gid = root
[secret]
    path = /t/tree
    read only = yes
    auth users = alice
    secrets file = /etc/rsyncd.secrets
EOF
rsync --daemon --config=/etc/rsyncd.conf --no-detach &
DPID=$!

# Second daemon with an motd file (motd text arrives before @RSYNCD: OK)
printf 'Welcome to the rsyncwin capture daemon\nsecond motd line\n' > /etc/rsyncd.motd
cat > /etc/rsyncd2.conf <<'EOF'
port = 8731
use chroot = no
motd file = /etc/rsyncd.motd
[tree]
    path = /t/tree
    read only = yes
EOF
rsync --daemon --config=/etc/rsyncd2.conf --no-detach &
DPID2=$!
sleep 1

daemoncap() { # $1 = capture name; $2 = daemon port; rest = client args
  name=$1; port=$2; shift 2
  mkdir -p "$OUT/$name"
  socat -r "$OUT/$name/c2s.bin" -R "$OUT/$name/s2c.bin" \
        TCP-LISTEN:9000,reuseaddr TCP:127.0.0.1:$port &
  SPID=$!
  sleep 0.3
  printf 'client: rsync %s\n' "$*" > "$OUT/$name/client-cmd.txt"
  rc=0
  timeout 60 rsync "$@" >"$OUT/$name/client-stdout.txt" 2>"$OUT/$name/client-stderr.txt" || rc=$?
  echo "exit=$rc" >> "$OUT/$name/client-cmd.txt"
  wait $SPID 2>/dev/null || true
}

daemoncap "daemon31-list-tree"  8730 --protocol=31 -r --no-inc-recursive --list-only rsync://127.0.0.1:9000/tree/
daemoncap "daemon31-list-sizes" 8730 --protocol=31 -r --no-inc-recursive --list-only rsync://127.0.0.1:9000/sizes/
rm -rf /t/dpull && mkdir -p /t/dpull
daemoncap "daemon31-pull-rt"    8730 --protocol=31 -rt --no-inc-recursive rsync://127.0.0.1:9000/tree/ /t/dpull/

# Module listing (no module in URL). Also under motd to pin pre-OK text lines.
daemoncap "daemon31-modlist"   8730 --protocol=31 rsync://127.0.0.1:9000/
daemoncap "daemon31-motd-list" 8731 --protocol=31 rsync://127.0.0.1:9000/

# Authenticated pull: server sends @RSYNCD: AUTHREQD <challenge>; client replies
# "<user> <digest>". Challenge is random per run -- tests must recompute the
# digest from the captured challenge + the password recorded in the manifest.
rm -rf /t/dauth && mkdir -p /t/dauth
RSYNC_PASSWORD=opensesame daemoncap "daemon31-auth-pull" 8730 --protocol=31 -rt --no-inc-recursive rsync://alice@127.0.0.1:9000/secret/ /t/dauth/
printf 'user=alice\npassword=opensesame\n' > "$OUT/daemon31-auth-pull/auth-manifest.txt"

# Auth failure: wrong password -> @ERROR line; record the client exit code
RSYNC_PASSWORD=wrongpass daemoncap "daemon31-auth-fail" 8730 --protocol=31 -rt --no-inc-recursive rsync://alice@127.0.0.1:9000/secret/ /t/dauth/
printf 'user=alice\npassword=wrongpass\n' > "$OUT/daemon31-auth-fail/auth-manifest.txt"

# Digest-selection contrast: protocol 29 client sends no digest list in its greeting
rm -rf /t/dauth29 && mkdir -p /t/dauth29
RSYNC_PASSWORD=opensesame daemoncap "daemon29-auth-pull" 8730 --protocol=29 -rt rsync://alice@127.0.0.1:9000/secret/ /t/dauth29/
printf 'user=alice\npassword=opensesame\n' > "$OUT/daemon29-auth-pull/auth-manifest.txt"

# Unknown module -> @ERROR path
rm -rf /t/dx && mkdir -p /t/dx
daemoncap "daemon31-badmodule" 8730 --protocol=31 -rt --no-inc-recursive rsync://127.0.0.1:9000/nonexistent/ /t/dx/

# Push into a read-only module -> @ERROR path
daemoncap "daemon31-push-readonly" 8730 --protocol=31 -rt --no-inc-recursive /t/tree/ rsync://127.0.0.1:9000/tree/

# Protocol 30 greeting/gating reference
rm -rf /t/dpull30 && mkdir -p /t/dpull30
daemoncap "daemon30-pull-rt" 8730 --protocol=30 -rt --no-inc-recursive rsync://127.0.0.1:9000/tree/ /t/dpull30/

# Push into a writable daemon module, then re-push (server quick-check)
daemoncap "daemon31-push-rt"       8730 --protocol=31 -rt --no-inc-recursive /t/tree/ rsync://127.0.0.1:9000/push/
daemoncap "daemon31-push-uptodate" 8730 --protocol=31 -rt --no-inc-recursive /t/tree/ rsync://127.0.0.1:9000/push/

# Server-side hashes of the pushed tree (push gate reference)
( cd /t/dpush && find . -type f -print0 | sort -z | xargs -0 -r sha256sum ) > "$OUT/daemon31-push-rt/server-tree.sha256"

kill $DPID $DPID2 2>/dev/null || true

# ---------------------------------------------------------------------------
# 6. MD4 sanity vectors via openssl legacy provider (best-effort).
# ---------------------------------------------------------------------------
if printf 'abc' | openssl md4 -provider legacy -provider default >/dev/null 2>&1; then
  {
    for s in "" "a" "abc" "message digest" "abcdefghijklmnopqrstuvwxyz" \
             "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" \
             "12345678901234567890123456789012345678901234567890123456789012345678901234567890"; do
      printf '%s' "$s" | openssl md4 -provider legacy -provider default 2>/dev/null \
        | awk -v s="$s" '{print $NF "  \"" s "\""}'
    done
  } > "$OUT/md4-openssl-vectors.txt"
else
  echo "openssl legacy provider unavailable; rely on RFC 1320 vectors" > "$OUT/md4-openssl-vectors.txt"
fi

echo "capture complete"
find "$OUT" -type f | sort
