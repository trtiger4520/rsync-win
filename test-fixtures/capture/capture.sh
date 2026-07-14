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

apk add --no-cache rsync openssl socat >/dev/null 2>&1

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
shift                                   # drop the host argument
printf '%s\n' "$@" > "$CAPDIR/argv.txt"
tee "$CAPDIR/c2s.bin" | "$@" 2>"$CAPDIR/server-stderr.txt" | tee "$CAPDIR/s2c.bin"
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

# Delta pull: basis differs from source at known offsets -> real sum header,
# block sums, matched tokens and literals in the capture.
rm -rf /t/delta && mkdir -p /t/delta
cp /t/tree/b003_300k.bin /t/delta/b003_300k.bin
printf 'XXXXXXXX' | dd of=/t/delta/b003_300k.bin bs=1 seek=1000 conv=notrunc 2>/dev/null
printf 'YYYY'     | dd of=/t/delta/b003_300k.bin bs=1 seek=150000 conv=notrunc 2>/dev/null
touch -d '2019-01-01 00:00:00' /t/delta/b003_300k.bin
sshcap "ssh31-pull-delta" --protocol=31 -t --no-whole-file fake:/t/tree/b003_300k.bin /t/delta/
cp /t/delta/b003_300k.bin "$OUT/ssh31-pull-delta/result.bin" 2>/dev/null || true

# Push (sender-side capture; P7 reference, free to record now)
rm -rf /t/pushdst && mkdir -p /t/pushdst
sshcap "ssh31-push-rt" --protocol=31 -rt --no-inc-recursive /t/tree/ fake:/t/pushdst/

# ---------------------------------------------------------------------------
# 5. Daemon transport captures via socat tap.
# ---------------------------------------------------------------------------
cat > /etc/rsyncd.conf <<'EOF'
port = 8730
use chroot = no
[tree]
    path = /t/tree
    read only = yes
[sizes]
    path = /t/sizes
    read only = yes
EOF
rsync --daemon --config=/etc/rsyncd.conf --no-detach &
DPID=$!
sleep 1

daemoncap() { # $1 = capture name; rest = client args
  name=$1; shift
  mkdir -p "$OUT/$name"
  socat -r "$OUT/$name/c2s.bin" -R "$OUT/$name/s2c.bin" \
        TCP-LISTEN:9000,reuseaddr TCP:127.0.0.1:8730 &
  SPID=$!
  sleep 0.3
  printf 'client: rsync %s\n' "$*" > "$OUT/$name/client-cmd.txt"
  rc=0
  timeout 60 rsync "$@" >"$OUT/$name/client-stdout.txt" 2>"$OUT/$name/client-stderr.txt" || rc=$?
  echo "exit=$rc" >> "$OUT/$name/client-cmd.txt"
  wait $SPID 2>/dev/null || true
}

daemoncap "daemon31-list-tree"  --protocol=31 -r --no-inc-recursive --list-only rsync://127.0.0.1:9000/tree/
daemoncap "daemon31-list-sizes" --protocol=31 -r --no-inc-recursive --list-only rsync://127.0.0.1:9000/sizes/
rm -rf /t/dpull && mkdir -p /t/dpull
daemoncap "daemon31-pull-rt"    --protocol=31 -rt --no-inc-recursive rsync://127.0.0.1:9000/tree/ /t/dpull/

kill $DPID 2>/dev/null || true

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
