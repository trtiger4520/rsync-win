# RsyncWin Codec Specification (reconciled)

Byte-level specification for the pure-core codecs in `src/RsyncWin.Protocol`, reconciled from
five independent derivations and re-verified arithmetically (executable check script, 130+
assertions, zero failures; XXH64 values additionally confirmed against the canonical `xxhash`
reference library). Target: rsync protocol **31**, negotiating down to 30/29. Peer reference:
rsync 3.4.3.

Every worked example below was recomputed independently; none were taken on faith. Items that
could not be verified without a live wire capture are collected in §12 (pin by capture).

Provenance: canonical rsync (GPLv3) was read for **behavior only**. All tables in this document
are given as **generation rules**, never copied literals. The weak checksum may be ported
directly from openrsync `hash.c` (ISC); the MD4 core from `golang.org/x/crypto/md4` (BSD-3).
No permissive implementation exists for varint/varlong/ndx/iflags or rsync's exact block sizing
(openrsync and gokrazy/rsync stop at protocol 27) — implement those from this spec.

---

## 1. The `int_byte_extra` table (shared by varint and varlong decode)

64 entries, indexed by `header >> 2`. **Generation rule**: for index `i` in 0..63, treat `i` as
a 6-bit integer and count consecutive 1-bits from bit 5 downward, stopping at the first 0.

```
i in [ 0,31] -> 0     [48,55] -> 2     [60,61] -> 4     {63} -> 6
i in [32,47] -> 1     [56,59] -> 3     {62}    -> 5
```

Equivalently, table-free: `extra = Math.Min(BitOperations.LeadingZeroCount(~((uint)header << 24)), 6)`.
The `>> 2` granularity is what caps headers 0xFD/0xFE/0xFF (7–8 leading ones as full bytes) at 6.
Generate the table in a static initializer from this rule and cite the rule in a comment
(project licensing policy: never copy the literal from `rsync.h`/`io.c`).

---

## 2. `write_varint` / `read_varint` (int32, protocol >= 30)

**Not LEB128.** Prefix-length code: the FIRST byte is a header whose count of leading 1-bits
equals the number of extra bytes that follow. The header's remaining low bits are the value's
MOST-significant bits; the extra bytes are the low-order bytes, little-endian (LSB immediately
after the header). Values are signed int32 carried as two's-complement uint32.

Notation: `u = (uint)x`, LE bytes `B1..B4` (`B1` = LSB). `tag(n)` = byte with top n bits set
(`tag(1)=0x80, tag(2)=0xC0, tag(3)=0xE0, tag(4)=0xF0`).

### Encode
1. `cnt` = largest `i` in 1..4 with `B_i != 0`; if all zero, `cnt = 1`.
2. `threshold = 1 << (8 - cnt)`  (cnt=1→0x80, 2→0x40, 3→0x20, 4→0x10).
3. Three cases:
   - **ESCALATE** (`B_cnt >= threshold`): emit `tag(cnt)` (== `(byte)~(threshold-1)`, payload
     bits all zero), then `B1..B_cnt`. Total `cnt+1` bytes.
   - **FOLD** (else if `cnt > 1`): header = `B_cnt | tag(cnt-1)`; emit header then
     `B1..B_(cnt-1)`. Total `cnt` bytes.
   - **LITERAL** (else, `cnt == 1` and `B1 < 0x80`): emit `B1` alone.

Output is always the unique minimal form. Length by unsigned magnitude:
`<= 0x7F` → 1; `<= 0x3FFF` → 2; `<= 0x1FFFFF` → 3; `<= 0x0FFFFFFF` → 4; else (including every
negative x) → 5 bytes `F0 <LE32>`. Bit budget: an n-extra-byte form carries `8n` low bits plus
`7-n` header payload bits (n=1: 14 bits, n=2: 21, n=3: 28 — one header bit is the 0 separator
after the tag ones).

### Decode
1. `h` = one byte; `extra = table[h >> 2]`.
2. `extra == 0`: value = `h` (0..0x7F). Done.
3. `extra > 4` (headers 0xF8..0xFF): **protocol error** → exit 12 (RERR_STREAMIO), not 11.
4. Read exactly `extra` bytes into the low positions of a zeroed 4-byte LE buffer (first byte
   read = LSB); `v = ReadUInt32LittleEndian`.
5. `residual = h & ((1 << (8 - extra)) - 1)` — use exactly this mask (it retains the separator
   0-bit, which is always 0 for a header in that class) for byte-compatibility on malformed input.
6. `extra < 4`: `v |= (uint)residual << (8 * extra)`. `extra == 4`: **discard** the residual —
   rsync stores it at byte offset 4, outside the int32.
   **C# hazard**: `(uint)residual << 32` is a shift by 0 (shift count masked mod 32) and would
   corrupt the value; the `extra < 4` guard is mandatory.
7. Return `(int)v` — sign falls out of two's complement.

The decoder does **not** enforce minimality: `80 05` decodes to 5. Accept it (rsync does).

### Worked examples (all recomputed)
```
0          -> 00                      0x1FFFFF   -> DF FF FF        (FOLD max, 21 bits)
1          -> 01                      0x200000   -> E0 00 00 20
0x7F       -> 7F                      0xFFFFFF   -> E0 FF FF FF
0x80       -> 80 80    (ESCALATE)     0x0FFFFFFF -> EF FF FF FF     (FOLD max, 28 bits)
0xFF       -> 80 FF                   0x10000000 -> F0 00 00 00 10
0x100      -> 81 00    (FOLD)         0x7FFFFFFF -> F0 FF FF FF 7F
0x3FFF     -> BF FF    (FOLD max)     -1         -> F0 FF FF FF FF
0x4000     -> C0 00 40                0xFFFF     -> C0 FF FF
```
Sample arithmetic: `0x100`: B=(00,01), cnt=2, threshold=0x40, B2=0x01 < 0x40 → header
`0x01|0x80 = 0x81`, payload `B1=00`. Decode: extra=1, residual=`0x81 & 0x7F = 0x01` →
`0x01<<8 | 0x00 = 256`.

### Wire sites (protocol 30/31)
- **compat_flags**: one varint, server→client, immediately after the version exchange and
  BEFORE `negotiate_the_strings()` and the checksum seed. rsync >= 3.2.4 sets
  CF_VARINT_FLIST_FLAGS (0x80), making it TWO bytes `80 xx`. Reading a single raw byte desyncs
  the handshake one byte before the vstring negotiation.
- flist xflags as varint when CF_VARINT_FLIST_FLAGS negotiated; uid/gid in flist entries
  (proto >= 30); hard-link group numbers; NDX_DEL_STATS payload (5 varints).
- `write_varint30`/`write_varlong30` fall back to plain fixed-width 4-/8-byte LE ints when the
  negotiated protocol is < 30 (needed for the protocol-29 downgrade path).
- The ndx codec (§4) is a DIFFERENT encoding — never conflate.

---

## 3. `write_varlong` / `read_varlong` with `min_bytes` (int64, protocol >= 30)

Same scheme generalized with a floor `M` (`min_bytes`). Wire form: header byte, then `N-1`
payload bytes little-endian; total `N = M + extra`, `M <= N <= 9`. Shorter forms zero-extend;
negatives are representable ONLY in the maximal 9-byte form. `write_varint` encode is exactly
`write_varlong` with M=1 over a 5-byte buffer, but the decode overflow bounds DIFFER (see below)
— encode may share code; decode bounds must be per-codec.

**Wire sites**: flist file length = `write_varlong30(len, 3)`; mtime/atime/crtime =
`write_varlong(t, 4)`. Both sides must agree per-field: the same value encodes differently
under each M (see 2^31 example).

### Encode(long x, int M)
1. `B[1..8]` = LE bytes of `(ulong)x` (two's complement).
2. `cnt = 8; while (cnt > M && B[cnt] == 0) cnt--;`  — floors at M even through zero bytes.
3. `bit = 1 << (7 - (cnt - M))`.
4. Cases:
   - **ESCALATE** (`B[cnt] >= bit`): `cnt++`; header = `(byte)~(bit - 1)` (pure prefix,
     `cnt_pre - M + 1` tag ones, no value bits).
   - **FOLD** (`cnt > M`): header = `B[cnt] | (byte)~(2*bit - 1)` (top `cnt-M` bits are the tag).
   - **FLOOR** (`cnt == M`, `B[M] < 0x80` guaranteed since bit==0x80 here): header = `B[M]`
     tag-free.
5. Emit header, then `B[1..cnt-1]` (in ESCALATE this includes the old top byte). Total `cnt` bytes.

Capacity: length N carries `8*(N-1) + (7 - extra)` bits, capped at 64. At N=9 the canonical
header is a fixed byte — `0xFC` for M=3 (six tag ones), `0xF8` for M=4 (five) — and the 8
payload bytes carry the full 64 bits. Boundary rolls: M=3 grows 3→4 bytes at 2^23 and 8→9 at
2^58; M=4 grows 4→5 at 2^31 and 8→9 at 2^59.

### Decode(int M)
1. Read M bytes: `header` + the low `M-1` payload bytes into a zeroed 9-byte buffer `v[0..M-2]`.
2. `extra = table[header >> 2]`.
3. `extra == 0`: `v[M-1] = header`.
   Else:
   - `M + extra > 9`: **protocol error** (exit 12). For M=3 unreachable (3+6=9); for M=4 fires
     on headers 0xFC..0xFF. Unit-test this asymmetry.
   - Read `extra` more bytes into `v[M-1 .. M+extra-2]`;
     `v[M+extra-1] = header & ((1 << (8-extra)) - 1)`.
     If that index is 8 (only when M+extra == 9) the residual lies beyond the int64 and is
     **silently discarded** (mirrors the varint extra==4 discard; canonical writers send 0 there,
     so `FD ...` decodes identically to `FC ...` at M=3 — accept, do not reject).
4. Result = `(long)ReadUInt64LittleEndian(v[0..8])`. Zero-extension only; a short form can never
   decode negative. Field-level validation (e.g. rejecting negative file sizes) belongs to the
   flist layer, not the codec — mtimes legitimately decode negative via the 9-byte form.

### Worked examples (all recomputed; M=3 unless noted)
```
0            -> 00 00 00           8388607 (2^23-1) -> 7F FF FF
1            -> 00 01 00           8388608 (2^23)   -> 80 00 00 80
127          -> 00 7F 00           2^31             -> C0 00 00 00 80
128          -> 00 80 00           5000000000       -> C1 00 F2 05 2A
255          -> 00 FF 00           0xDEADBEEF       -> C0 EF BE AD DE
256          -> 00 00 01           2^58 - 1         -> FB FF FF FF FF FF FF FF
65535        -> 00 FF FF           2^58             -> FC 00 00 00 00 00 00 00 04
65536        -> 01 00 00
1000         -> 00 E8 03           M=4:
                                   0            -> 00 00 00 00
                                   1577934245   -> 5E A5 5D 0D      (2020-01-02T03:04:05Z)
                                   2^31         -> 80 00 00 00 80   (!= the M=3 bytes)
                                   4300000000   -> 81 00 CB 4C 00   (interior 00 at B4 kept)
                                   -1           -> F8 FF FF FF FF FF FF FF FF
                                   -2082844800  -> F8 80 4F DA 83 FF FF FF FF  (1904 mtime)
                                   2^59 - 1     -> F7 FF FF FF FF FF FF FF
                                   2^59         -> F8 00 00 00 00 00 00 00 08
```
Sample arithmetic: `5000000000 = 0x1_2A05_F200`, M=3: B1=00 B2=F2 B3=05 B4=2A B5=01; cnt=5,
bit=`1<<(7-2)`=0x20, B5=0x01 < 0x20 → FOLD: header = `0x01 | 0xC0 = 0xC1`, payload B1..B4.
Decode: `0xC1>>2 = 48` → extra=2; read `00 F2` (with header), then `05 2A`; MSB byte =
`0xC1 & 0x3F = 0x01` → `0x012A05F200`. ✓

Notes: unlike LEB128, nothing happens at the LSB's 7-bit boundary (128 is still 3 bytes at M=3)
— only the TOP byte interacts with the header. Only bytes ABOVE the highest nonzero index are
trimmed; interior zeros are transmitted. The C `~(bit-1)` / `~(2*bit-1)` expressions need
`(byte)` casts in C# (int promotion).

---

## 4. `write_ndx` / `read_ndx` — the stateful file-index codec (protocol >= 30)

Protocol 29 uses a plain 4-byte LE `write_int(ndx)` instead. Protocol >= 30 uses this
delta-from-previous encoding.

### Special index constants (scalar wire facts)
```
NDX_DONE = -1   NDX_FLIST_EOF = -2   NDX_DEL_STATS = -3   NDX_FLIST_OFFSET = -101
```
Only NDX_DONE and NDX_DEL_STATS occur with incremental recursion off (which we must keep off).

### State
TWO independent int32 registers **per stream direction**:
```
prevPositive = -1   (initial)
prevNegative = +1   (initial; holds the MAGNITUDE of the last negative value)
```
One encoder per outbound logical stream, one decoder per inbound stream. Never shared, never
reset — state persists across all phases, all NDX_DONE markers, and mux frame boundaries for
the whole session.

### Encode(ndx)
1. `ndx == NDX_DONE (-1)`: emit single byte `0x00`; touch neither register.
2. `ndx >= 0`: `diff = ndx - prevPositive; prevPositive = ndx; v = ndx`. No prefix byte.
3. `ndx <= -2`: emit prefix `0xFF`; `v = -ndx`; `diff = v - prevNegative; prevNegative = v`.
4. Choose form by diff:
   - `1 <= diff <= 0xFD`: one byte = diff.
   - `diff == 0` or `254 <= diff <= 0x7FFF`: `0xFE`, then diff as 2 bytes **BIG-endian**
     (hi = `diff>>8` <= 0x7F, so its top bit is clear — that clear bit is the discriminator).
   - `diff < 0` or `diff > 0x7FFF`: `0xFE`, then the ABSOLUTE value `v` (not the diff) as
     4 bytes in this exact order: `((v>>24)&0xFF)|0x80`, `v&0xFF`, `(v>>8)&0xFF`, `(v>>16)&0xFF`
     — MSB-with-flag, LSB, mid-low, mid-high (neither LE nor BE). Valid because `v` is a
     non-negative int32, so `v>>24 <= 0x7F`.

### Decode
1. Read `b0`. `0xFF` → negative context, read next byte into b0, register = prevNegative.
   `0x00` → return NDX_DONE (state untouched). Else positive context, register = prevPositive.
2. `b0 == 0xFE`: read 2 bytes x,y. If `x & 0x80`: read 2 MORE bytes m,n;
   `v = y | (m<<8) | (n<<16) | ((x&0x7F)<<24)` (absolute; register ignored for the value).
   Else `v = ((x<<8)|y) + register`.
3. Else `v = b0 + register` (conforming writers send 0x01..0xFD here; be rsync-lenient otherwise).
4. Write `v` back through the chosen register. Negative context → return `-v`, else `v`.

### Worked examples (all recomputed, fresh state unless noted)
```
[0,1,2,5,3,NDX_DONE] -> 01 01 01 03 FE 80 03 00 00 00
   (0: diff=1; 1,2: diff=1; 5: diff=3; 3: diff=-2 -> 4-byte absolute of 3; DONE -> 00)
300                  -> FE 01 2D          (diff = 301 = 0x012D, BE)
7 then 7             -> 08  FE 00 00      (diff=0 MUST escape; bare 00 means NDX_DONE)
100000               -> FE 80 A0 86 01    (diff 100001 > 0x7FFF; v=0x000186A0)
-2 (NDX_FLIST_EOF)   -> FF 01             (m=2, diff = 2-1 = 1)
-101 then -2         -> FF 64  FF FE 80 02 00 00
   (m=101, diff=100=0x64; then m=2, diff=-99 -> 4-byte absolute of 2)
```

### Edge rules
- diff 254/255 fit one byte numerically but MUST escape: lead bytes 0xFE/0xFF are reserved.
- Any backward jump forces the 5-byte form, however small the target.
- prevNegative stores magnitudes and starts at +1; NDX_DONE is special-cased BEFORE the
  negative branch.
- read_batch's write_int fallback is batch-file-only; never on a live wire — do not implement.

---

## 5. The iflags word, fnamecmp_type, and vstring (protocol >= 29)

After every non-negative ndx on the request channel (generator→sender) and data channel
(sender→receiver): a 16-bit unsigned word, **little-endian** (`b[0]=x&0xFF, b[1]=x>>8`).

### iflags bits (scalar wire facts)
```
0x0001 ITEM_REPORT_ATIME          0x0400 ITEM_REPORT_CRTIME  (--crtimes only)
0x0002 ITEM_REPORT_CHANGE         0x0800 ITEM_BASIS_TYPE_FOLLOWS  -> 1 byte follows
0x0004 ITEM_REPORT_SIZE (=TIMEFAIL for symlinks)
                                  0x1000 ITEM_XNAME_FOLLOWS       -> vstring follows
0x0008 ITEM_REPORT_TIME           0x2000 ITEM_IS_NEW
0x0010 ITEM_REPORT_PERMS          0x4000 ITEM_LOCAL_CHANGE
0x0020 ITEM_REPORT_OWNER          0x8000 ITEM_TRANSFER            -> sum head follows (requests)
0x0040 ITEM_REPORT_GROUP
0x0080 ITEM_REPORT_ACL            local-only, NEVER wire (mask with & 0xFFFF before writing):
0x0100 ITEM_REPORT_XATTR          0x10000 MISSING_DATA, 0x20000 DELETED, 0x40000 MATCHED
```
Payload order after the word is fixed: fnamecmp_type byte (iff bit 11), then xname vstring
(iff bit 12). Nothing else follows without -X (out of scope).

### fnamecmp_type
`0x00..0x7F` = basis-dir index; `0x80` FNAMECMP_FNAME (default — never sent; readers default to
0x80 when bit 11 absent); `0x81` PARTIAL_DIR; `0x82` BACKUP; `0x83` FUZZY.

### vstring codec
len <= 0x7F: one length byte. `0x80 <= len <= 0x7FFF`: two bytes `(len>>8)|0x80`, `len&0xFF`
(big-endian-with-flag). > 0x7FFF: protocol error. Then len raw bytes, NO NUL.
`"foo.txt"` → `07 66 6F 6F 2E 74 78 74`; a 200-byte name → `80 C8` + 200 bytes.

(The vstring length and the 2-byte ndx diff are the only BE-ish encodings in this whole area.)

### Message shapes
Generator→sender per file: `Encode(ndx)`, iflags word (+payloads); iff ITEM_TRANSFER, the sum
head (§7) + block sums. Full-transfer request = all-zero sum head (16 zero bytes), no block sums.
Attribute-only messages (ITEM_TRANSFER clear) are legal and carry NO sum head; the generator
only emits one when `(iflags & ~(0x0800|0x1000|0x4000)) != 0` or an xname is present.

Sender→receiver per file: `Encode(ndx)` on its OWN outbound codec state, the SAME iflags word +
payloads; iff ITEM_TRANSFER: echo of the received sum head (4 LE int32s, block sums NOT echoed),
then the token/delta stream, then the whole-file checksum. ITEM_TRANSFER requests for
non-regular files, or arriving in sender phase 2, are protocol errors.

Worked examples (recomputed):
```
new-file full-transfer request, ndx 0, fresh codec (iflags 0xA000 = TRANSFER|IS_NEW):
  01  00 A0  <16 zero bytes>                                        (19 bytes)
fuzzy-basis request payloads (iflags 0x9800, type FUZZY, xname "a.txt"):
  00 98  83  05 61 2E 74 78 74
```

Protocol-29-only keep-alive quirk (only if we fall back): ndx == fileListCount with iflags ==
exactly 0x2000 is a keep-alive — discard. Proto 30/31 use mux MSG_NOOP instead. For proto < 29
there is no iflags word at all (out of our floor).

---

## 6. Phases and NDX_DONE choreography (behavior summary; pin by capture)

Sender loop: `maxPhase = 2` for proto >= 29. Each NDX_DONE read increments phase; while
`phase <= maxPhase` the sender echoes one NDX_DONE to the receiver and keeps reading; the third
NDX_DONE breaks the loop with no echo, then stats, then the final-goodbye exchange.

Generator emission order (our pull client), proto 31 defaults, non-incremental:
1. all phase-0 requests; 2. NDX_DONE #1; 3. redo requests (sum heads carry FULL-length s2length
— a reader must take s2length from the sum head, never assume the phase-0 truncation);
4. NDX_DONE #2, then #3 (delay-updates phase, vacuous without --delay-updates, sent early), and
on proto >= 31 #4 (delete phase); 5. after receiver confirmation: NDX_DONE #5 (final goodbye).

Receiver view: echo #1 → phase 1 (redo results); echo #2 → phase 2; echo #3 (proto >= 31 only)
→ done. On proto 29/30 the receiver gets only 2 echoes then clean EOF — do not wait for a third.
NDX_DEL_STATS (-3) may arrive at a phase boundary: consume exactly 5 varints and continue (it is
not a phase marker). A whole-file checksum mismatch triggers a redo in a later phase — that is
why multiple phases and multiple NDX_DONE markers exist.

The exact marker counts/timing (especially the proto-31 back-to-back #2/#3/#4 burst and the
proto-30 2-echoes-then-EOF end) are code-derived, not yet captured: **pin by capture** (§12).

---

## 7. `sum_sizes_sqroot` block sizing and the sum head

### Constants (scalar facts)
```
BLOCK_SIZE=700  MAX_BLOCK_SIZE=131072 (1<<17, proto>=30)  OLD_MAX_BLOCK_SIZE=1<<29 (proto<30)
BLOCKSUM_BIAS=10  SUM_LENGTH=16  SHORT_SUM_LENGTH=2  CHAR_OFFSET=0
```
`bitlen(x)` = highest set bit index + 1 (`bitlen(700)=10`); in C#:
`64 - BitOperations.LeadingZeroCount((ulong)x)` for x > 0.

### blength
```
if flength <= 490000 (700*700): blength = 700
else: blength = clamp(floor(sqrt(flength)) & ~7, 700, cap)     cap = proto<30 ? 1<<29 : 131072
```
Compute the square root with the digit-by-digit binary method — NOT floating point (flength >
2^53 is inexact as a double and near-perfect squares round wrong):
```
c = 1; l = flength; while ((l >>= 2) != 0) c <<= 1;   // c = 2^floor((bitlen-1)/2)
// (rsync short-circuits to the cap here if c >= cap — equivalent to the clamp)
blength = 0;
do { blength |= c;
     if (flength < (long)blength * blength) blength &= ~c;
     c >>= 1;
} while (c >= 8);                                     // bits 0..2 stay 0 => already & ~7
```
Edge (verified): flength in (490000, 495616) gives floor(sqrt) 700..703 → `& ~7` = 696 —
**below** the floor — and the max() pulls it back to 700. A round-UP implementation
(openrsync-style, 704) fails this vector. The 131072 cap fires iff flength >= 2^34; below that
the greedy result cannot exceed 131064.

### s2length (per-block strong-sum truncation, proto >= 27)
Redo phase (`fullChecksum`): s2length = 16 — only reachable via redo; the formula tops out at 11.
Normal phase:
```
b  = 10 + 2*(bitlen(flength) - 1)          // flength==0 adds nothing
b -= min(bitlen(blength) - 1, b)
s2length = clamp((b - 24) / 8, 2, 16)      // C trunc-toward-zero == C# int division;
                                           // negative intermediates absorbed by the clamp
```
**Ground truth check** (measured on rsync 3.4.3): flength=300000 → 300000 has 19 bits →
`b = 10 + 36 = 46`; 700 has 10 bits → `b = 37`; `(37-24)/8 = 1`; clamp → **2**. Matches
`--debug=deltasum2`: `count=429 rem=400 blength=700 s2length=2`.

### count / remainder
```
remainder = flength % blength          // 0 => last block is FULL-size, not zero-size
count     = ceil(flength / blength)    // compute in 64-bit; must fit int32 or file is unprocessable
```

### Wire format (`write_sum_head`) — plain 4-byte LE ints on EVERY protocol (never varints)
`count, blength, s2length, remainder`. Null sum = 16 zero bytes.
Read-side validation (violation = exit 12): `count >= 0`; `0 <= blength <= cap`;
`0 <= s2length <= xfer digest length`; `0 <= remainder <= blength`.

### Worked sum heads (all recomputed)
```
flength=300000  -> AD 01 00 00  BC 02 00 00  02 00 00 00  90 01 00 00   (429,700,2,400) MEASURED
flength=2^30    -> 00 80 00 00  00 80 00 00  03 00 00 00  00 00 00 00   (32768,32768,3,0)
flength=490001  -> BD 02 00 00  BC 02 00 00  02 00 00 00  01 00 00 00   (701,700,2,1)
flength=0       -> (0, 700, 2, 0)
```

---

## 8. Weak rolling checksum (`get_checksum1`)

An Adler-32 *variant* with two deliberate divergences: bytes are **SIGNED** (sbyte cast:
0x80..0xFF contribute -128..-1), and each half is plain mod 2^16 (no 65521). CHAR_OFFSET = 0
(keep symbolic). Directly portable from openrsync `hash.c` `hash_fast` (ISC) — cite it.

### Reference form
```
uint s1 = 0, s2 = 0;                       // unchecked; wraps mod 2^32
foreach (byte raw in buf) {
    int x = (sbyte)raw;                    // SIGNED — the #1 divergence from Adler-32
    s1 += (uint)(x + CHAR_OFFSET);
    s2 += s1;
}
return (s1 & 0xFFFF) | ((s2 & 0xFFFF) << 16);
```
The 4-byte unrolled form is a pure optimization (per quad x0..x3:
`s2 += 4*s1 + 4*x0 + 3*x1 + 2*x2 + x3 + 10*OFF; s1 += x0+x1+x2+x3 + 4*OFF`). Porting trap:
rsync's `i < len-4` bound relies on signed len — guard with `if (len > 4)` under unsigned sizes.

### O(1) roll (re-derived identity, window length n, remove `old`, append `new`)
```
s1' = s1 - (old+OFF) + (new+OFF)
s2' = s2 - n*(old+OFF) + s1'
```
(Justification: windowed s2 = Σ (n-i)·X[i]; removing X[0] subtracts n·X[0], and re-weighting
adds each remaining byte once = +s1'.) Omitting the add shrinks the window from the left (EOF);
the s2 multiplier is the CURRENT window length, which shrinks near EOF. Truncating s1/s2 to 16
bits between rolls is congruence-safe, but the roll itself needs >= 32-bit wrapping arithmetic
(`unchecked`).

### Wire
Each block entry in the sum body: sum1 as 4-byte LE, then the strong sum truncated to s2length.

### Worked examples (all recomputed)
```
[01 02 03]                    -> 06 00 0A 00    (s1=6, s2=10)
"abcde"                       -> EF 01 C3 05    (s1=495, s2=1475)
[80 FF 00 7F]  SIGNEDNESS PIN -> FE FF 7C FE    (s1=-2->0xFFFE, s2=-388->0xFE7C;
                                                 an unsigned impl gives 057C01FE — catches it)
roll [1,2,3]-1+4 -> [2,3,4]   -> 09 00 10 00    (s1'=9, s2'=16; equals direct recompute)
```

---

## 9. MD4 core (RFC 1320)

One plain RFC 1320 MD4 serves protocols 29, 30 and 31. CSUM_MD4_OLD (proto 27–29) is
bit-identical to RFC MD4 for every input length; the bug emulations (32-bit length field,
skipped finalization at len % 64 == 0) belong to proto <= 26 (BUSTED/ARCHAIC), which we never
negotiate. **No bug emulation whatsoever.** The only proto-29 "old" behavior is seed placement
(§10). Port source: `golang.org/x/crypto/md4` (BSD-3) — structure below; a from-scratch
implementation of this text passes all 7 RFC vectors (re-verified here).

State: `uint s0..s3` = `0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476`; 64-byte partial buffer
+ fill count; `ulong len`. Streaming Append: top up partial buffer → Block per full 64 bytes →
stash tail. (rsync's own `mdfour_update` finalizes on any short chunk — that is NOT a streaming
API; do not copy that discipline. A normal buffer-and-finalize-once MD4 produces identical
bytes.)

Block (per 64 bytes): load X[0..15] LE uint32; three rounds of 16 steps, each step
`a = RotateLeft32(a + f + X[k] (+ K), s)` then rotate registers `(a,b,c,d) = (d,a,b,c)`
(after 16 steps the registers realign, so the final `s += ...` needs no reshuffle — port this
trick exactly); then wrapping `s0+=a; s1+=b; s2+=c; s3+=d`.

**Table generation rules** (nothing copied):
- Round functions: R1 `f = (b&c) | (~b&d)`; R2 majority `(b&c)|(b&d)|(c&d)`; R3 `b^c^d`.
- Shifts: R1 `{3,7,11,19}`, R2 `{3,5,9,13}`, R3 `{3,9,11,15}` by `i % 4` (RFC 1320 §3.4 facts).
- Constants: R1 none; R2 `0x5A827999 = floor(2^30·sqrt 2)`; R3 `0x6ED9EBA1 = floor(2^30·sqrt 3)`.
- Word order: R1 `k = i`; R2 = transpose of the 4x4 grid, `k = (i%4)*4 + i/4`; R3 = 4-bit
  bit-reversal `rev4(i) = ((i&1)<<3)|((i&2)<<1)|((i&4)>>1)|((i&8)>>3)`.

Finalize: append `0x80`, zero-pad to ≡ 56 (mod 64) — the padding may spill into a second block
(exact-64-byte inputs get a full extra padding block; see the 60+4 vector) — then the 64-bit
**little-endian bit count** `L*8`; digest = s0..s3 each LE (16 bytes). A destructive
finalize-once contract is fine (rsync never appends after finalizing) — document it.

### RFC 1320 vectors (gate the port on all 7; re-verified)
```
""                 31d6cfe0d16ae931b73c59d7e0c089c0
"a"                bde52cb31de33e46245e05fbdbd6fb24
"abc"              a448017aaf21d8525fc10ae87aa6729d
"message digest"   d9130a8164549fe818874806e1c7014b
a..z               d79e1c308aa5bbcdeea8ed63df412da9
A..Za..z0..9       043f8582f241db351ce627e153e7f0e4
"1234567890" x 8   e33b4ddc9c38f2199c3e7b164fcc0536
```

---

## 10. Checksum contexts and seed placement

`checksum_seed` is a signed int32, read LE from the wire LAST in the handshake (after
compat_flags and string negotiation — reading it earlier desyncs everything). Whenever hashed
as bytes it is written little-endian. THREE distinct contexts with different rules:

### A. Block strong sums (`get_checksum2`; wire carries the FIRST s2length bytes — prefix-take)
```
MD4 (proto 29 and negotiated md4):  seed != 0: MD4(data ++ seed_le4)   — APPENDED
                                    seed == 0: MD4(data)               — short-circuit
MD5 with CF_CHKSUM_SEED_FIX (both ends >= 3.1.2):
                                    seed != 0: MD5(seed_le4 ++ data)   — PREPENDED
MD5 without the flag:               seed != 0: MD5(data ++ seed_le4)   — APPENDED
                                    seed == 0 (either): MD5(data)
XXH64:  value = XXH64(data, (ulong)(long)seedInt32)   — numeric seed, SIGN-extended;
        no zero short-circuit (0 is just seed 0)
```
CF_CHKSUM_SEED_FIX is bit `1<<5` (0x20) of the compat_flags varint — key off the bit, never the
peer's version string.

### B. Whole-file transfer sum (after each file's delta stream; FULL digest length; may be a
different negotiated algorithm than A — `file_sum_nni` vs `xfer_sum_nni`)
```
CSUM_MD4_OLD (proto 29):    MD4(seed_le4 ++ data) — prepended UNCONDITIONALLY, even seed==0
                            (the ONE place the zero short-circuit does NOT apply)
CSUM_MD4 (negotiated @30+): MD4(data)             — NO seed (differs from proto 29!)
CSUM_MD5:                   MD5(data)             — NO seed, regardless of seed-fix flag
CSUM_XXH64:                 streaming XXH64 with seed 0 — session seed IGNORED here
```
A mismatch here triggers the redo phase (§6).

### C. Pre-transfer `--checksum` file-list sums: never seeded, any algorithm, full length
(proto 29: MD4(file); proto 30/31: MD5(file) or negotiated).

### Negotiation
When both ends >= 3.2.0 each sends a space-separated preference list; first mutual name wins for
both A and B. Fallback: proto >= 30 → md5, proto 29 → md4(OLD). **Recommendation: advertise
"md5" (optionally "xxh64 md5"); never advertise "md4" at proto 30/31** — stock rsync's
OpenSSL-EVP md4 path prepends the block-sum seed while the builtin path appends it, so two stock
builds disagree with each other.

### XXH64 byte order (verified against .NET and the canonical library)
rsync emits the 64-bit value **little-endian** for both block sums (truncated) and whole-file
sums (8 bytes). .NET `System.IO.Hashing.XxHash64` `GetCurrentHash()`/`Hash()` return
**big-endian** — never memcpy that span. Correct pattern:
```csharp
ulong v = XxHash64.HashToUInt64(data, (long)seedInt32);   // int->long sign-extends correctly
BinaryPrimitives.WriteUInt64LittleEndian(dest, v);         // (long)(uint)seed would be WRONG
```
Whole-file streaming: `new XxHash64(0)` always.

### Seeded worked examples (all recomputed; seed = 0x12345678, LE bytes 78 56 34 12)
```
MD4 block "abc" (append):                dfc21629291028d56ee023a48767e5c9
MD4_OLD file "abc" (prepend):            4d713279fde8d43637584c88006e02f8
MD4_OLD file "abc", seed=0 (prepends 4 zero bytes): 280f4da968ef17b2ca2fb5d289874be9
MD4 block 60x'a' + seed = exactly 64 hashed bytes (finalization pin):
                                         267197955a81efc3101b737b22b8bab5
MD5 block "abc" WITH seed-fix (prepend): 5a035b41f39449760a0c6a01e4060c62
MD5 block "abc" without (append):        a7e33be7c2164ebddcb3da6720a2d7d4
MD5 file "abc" (always unseeded):        900150983cd24fb0d6963f7d28e17f72
XXH64 block "abc" seed 0x12345678 = 0x0F7FD1655F1AF42B -> wire 2B F4 1A 5F 65 D1 7F 0F
XXH64 file  "abc" (seed always 0) = 0x44BC2CF5AD770999 -> wire 99 09 77 AD F5 2C BC 44
XXH64 block "abc" seed -1 (sign-extended to 0xFFFFFFFFFFFFFFFF) = 0x28306E589CC02176
```

---

## 11. Cross-cutting C# hazards (collected)

- `(uint)x << 32` masks the shift count to 0 — guard `extra < 4` in read_varint (§2.6).
- Byte comparisons must be unsigned; C# `byte` is fine — never port via `sbyte` (except the
  weak-checksum accumulation, where `sbyte` is REQUIRED, §8).
- `~(bit-1)`-style expressions need `(byte)` casts (int promotion).
- Wrapping arithmetic everywhere: `unchecked` + `uint`/`ulong`.
- Integer sqrt only for block sizing; never `Math.Sqrt` (§7).
- `System.IO.Hashing` xxHash bytes are big-endian; rsync's are little-endian (§10).
- Protocol errors (overflow headers, sum-head validation) map to exit 12, not 11.
- All of §§2–7 ride inside MSG_DATA frames when multiplexing is on; codec state and message
  boundaries freely span frame boundaries — never reset any codec on a frame or phase boundary.

## 12. Pin by capture (unresolvable without live rsync 3.4.3 wire bytes)

1. compat_flags end-to-end: expect a 2-byte `80 xx` varint from rsync >= 3.2.4.
2. NDX_DONE choreography: proto-31 back-to-back #2/#3/#4 burst; proto-30 two-echoes-then-EOF
   receiver end; --delay-updates/--delete-after timing shifts.
3. Sender stats block bytes (between the 3rd NDX_DONE and the final goodbye) — spec separately
   before implementing the end-of-run reader.
4. Which mux message payloads (MSG_SUCCESS/MSG_NO_SEND/MSG_REDO) carry an ndx as a plain varint
   — verify per message type, not from memory.
5. Full flist entry layout at proto 31 (xflags-as-varint context around the varlong fields;
   locating mtime vectors requires XMIT_SAME_TIME clear).
6. Whether recv_file_entry rejects negative decoded file lengths (our flist layer rejects
   size < 0 regardless).
7. Redo-phase full-length s2length live capture (source-confirmed only).
8. sum-head captures for flength in (490000, 495616), ~1 MB, ~1 GB, >= 2^34 (worked examples
   above predict the values).
9. xxh3/xxh128 seeded variants before ever advertising them (only xxh64 verified here).

---

## Appendix: verification method

Every vector in this document was recomputed by an executable script (scratchpad
`verify_codecs.py`) implementing the algorithms exactly as written above: 130+ assertions,
0 failures, including encode→decode round-trips, the non-minimal-decode tolerance, the
varint/varlong overflow-bound asymmetry, boundary rolls at 2^23/2^31/2^58/2^59, the signed-byte
weak-checksum pin, the roll-vs-recompute identity, and the measured s2length ground truth.
MD5 vectors via a known-good library; XXH64 vectors via the canonical `xxhash` library;
MD4 via a from-scratch implementation of §9 gated on all 7 RFC 1320 vectors.
