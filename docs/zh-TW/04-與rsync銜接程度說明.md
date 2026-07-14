# RsyncWin 與 rsync 的銜接程度說明（含協定）

本文說明 RsyncWin 與標準 rsync 的互通到什麼程度、實作了協定的哪些部分、哪些是逐位元組驗證、哪些是語意
層驗證、以及刻意不支援的界線與理由。

---

## 一、銜接的總結論

**RsyncWin 與標準 rsync 3.4.3 逐位元組互通**，涵蓋拉取、推送、SSH 與常駐程式四種組合，含差異傳輸、
`--checksum`、`--delete`、`--secluded-args`、`-z` 壓縮。互通的定義不是「跑得動」，而是：

- **線路位元組相等**：我們寫出的位元組，與真 rsync 用戶端在相同情境下寫出的位元組，逐位元組相同（解多工
  後的邏輯串流）。
- **雜湊相等**：傳輸後檔案的 SHA-256 與來源相同。
- **重跑不傳輸**：對相同內容再跑一次，一個檔案都不傳（快速路徑正確）。
- **遠端離開 0**：真 rsync 伺服器正常結束。

這四個閘門在每個階段都被強制通過，因此「互通」是有證據的，不是「看起來對」。

---

## 二、協定版本與協商

RsyncWin **針對協定 31 實作，向下協商到 30/29**：

- 雙方各自宣告版本，session 用 `min(本地, 對端)`。
- **對端常常比我們新**：標準 rsync 3.4.3 宣告**協定 32**，我們宣告 31，協商成 31——這是正常情況，不是
  錯誤。因此「對端聲稱的版本上限」（`MaxProtocolVersion = 40`）只是理智界，不等於我們實作的版本。
- 協定 27/29 只支援到序幕/argv 層級——29 世代的檔案清單格式（1 位元組 xflags、4 位元組 mtime、無 varint）
  沒有解碼路徑，真 29-only 對端會在檔案清單中途失步。常駐程式 session 把協商下限設在 30 並給清楚錯誤。

### 已測得的相容矩陣

對真 rsync 3.4.3（alpine:3.21）驗證，每格都用 `cmp` 逐位元組核對序幕：

| 傳輸 | 協定 27 | 29 | 30 | 31 |
|---|:--:|:--:|:--:|:--:|
| 常駐程式拉取（`rsync://`） | OK | OK | OK | OK |
| SSH 拉取 | OK | OK | OK | OK |
| SSH 推送 | OK | — | — | OK |

---

## 三、交握順序（協定的第一個雷區）

交握順序**逐位元組驗證**、並對照 `compat.c` 的行為確認。錯誤的順序不會噴錯，而是失步成 exit 12：

```
版本交換（各寫各的 int32，再讀對方）
  → compat_flags varint（僅伺服器寫、我們讀，協定 ≥ 30）
  → negotiate_the_strings()：checksum vstring 雙向
      → 若 -z：緊接 compression vstring 雙向（見下）
  → checksum_seed（最後，4 位元組小端）
  → 多工開始
```

**seed 一定在最後**。太早讀 seed 會把 compat_flags varint 吃掉，之後每一次讀都失步。

`compat_flags` 由我們在 server argv 的 `-e.` 後面送出的**能力字母**（`client_info`：`LsfxCIvu`）推導，
伺服器原封回送成 compat flags。我們刻意**不送 `i`**（不做增量遞迴），所以觀察到的值是 510（除
`CF_INC_RECURSE` 外全開）。`CF_VARINT_FLIST_FLAGS` 一位雙用，也是「對端能協商字串」的訊號。

---

## 四、逐位元組釘死的協定事實（節選）

以下事實都由 Docker 擷取的真 rsync 位元組釘死（完整清單見 `docs/wire-notes.md`）：

- **角色**：拉取時**伺服器是 sender**、我們是 receiver/generator。Generator 必須主動請求每個檔案
  （`write_ndx` + sum head + iflags）才會有資料進來。
- **NDX_DONE 編排**：協定 31 的 c2s 有 5 個 DONE、s2c 有 3 個 DONE + stats + goodbye（協定 30 是 4/3、
  無 goodbye）。這套多階段編排正是差異重傳（redo）機制的根源。
- **stats 區塊**（拉取 s2c）：5 個 `varlong(min_bytes=3)`，順序是 sender 的 total_read（我們送出的位元
  組）**在前**，再 total_written、total_size、flist 建構/傳輸時間。
- **推送方向**：推送用戶端**不送過濾清單**（連拉取的結尾 int32 0 都沒有，除非 `--delete`）；**兩個方向
  都沒有 stats 區塊**（sender 自己握有總數）。
- **檔案清單結尾**：`varint(0)` + `varint(io_error)` + id0 名稱尾巴。

---

## 五、Checksum 協商

- 標準用戶端提供 `xxh128 xxh3 xxh64 md5 md4`，伺服器回同樣清單加 ` none`，取**第一個共同項**。標準組建的
  傳輸 checksum 協商**沒有 sha1/sha256**（SHA 只出現在 openssl 組建的 `--checksum-choice` 與常駐程式驗證）。
- **協定 30/31 絕不提供 `md4`**：標準 rsync 的 OpenSSL-EVP 路徑**前置** seed，內建路徑**後置** seed，兩個
  標準組建彼此會不一致。MD4 嚴格只當協定 29 的後備。
- RsyncWin v1 提供 `md5`（BCL 內建）。整檔 xxh128 已可用於整檔和。

---

## 六、`-z` 壓縮的銜接程度（含為什麼強制 zlibx）

這是銜接程度差異最微妙、也最需要說明的部分。

### 標準 `-z` 預設是 zstd —— BCL 無法解碼

`negotiate_the_strings()` 在 checksum vstring 之後，緊接一組 compression vstring：用戶端提供
`zstd lz4 zlibx zlib`、伺服器回同樣清單加 ` none`，取第一個共同項 = **zstd**。.NET 的 BCL 只有 deflate，
沒有 zstd/lz4，所以 RsyncWin **不能**把壓縮交給協商。

### 因此強制 zlibx（`--new-compress`）

RsyncWin 強制 `--compress-choice=zlibx`，對應到 server argv 的 `--new-compress`：

- **強制某個選擇會使壓縮 vstring 完全不出現**——交握位元組與非 `-z` session 位元組相同，模式只由
  `--new-compress` 這個 server arg 承載（bundle 裡沒有 `z` 字母）。
- **為什麼是 zlibx 而不是舊 zlib**：兩者都是原始 deflate（windowBits −15），但舊 `zlib` 模式會把**符合
  區塊的位元組塞進 deflate 視窗**（讓後面的字面能回指符合資料），這需要一個「塞進視窗但不輸出」的原語
  （`Z_INSERT_ONLY`），BCL 沒有。zlibx（「新」模式）**把符合區塊排除在視窗外**，所以 receiver 直接從基
  準檔複製符合區塊、只把字面餵給 `DeflateStream`——這正是能用 BCL 只靠 `DeflateStream` 實作的關鍵。
- 對全量傳輸（無符合區塊），舊 zlib 與 zlibx 的解多工位元組**完全相同**。

### 銜接的驗證強度不對稱

- **拉取（解壓）方向**：**逐位元組**——把真 rsync 的 `-z` 壓縮拉取擷取重播，重建的每個檔案 SHA 相符
  （`ssh31-pull-z-zlibx` 全量、`ssh31-pull-z-delta` 差異）。
- **推送（壓縮）方向**：**語意層**——deflate 的輸出是實作定義的（不同 zlib 實作對同一輸入會產生**不同但
  都合法**的壓縮位元組），所以**不可能**逐位元組比對我們送出的壓縮串流。改用「真 rsync 伺服器能正確重建
  我們送的壓縮 + 重推不傳輸」當閘門。這是誠實的界線：壓縮送出面本質上無法逐位元組黃金化。

### 壓縮 token 格式的細節

`DeflateStream.Flush()` 正好產生 rsync 在線路上剝除的那個 `Z_SYNC_FLUSH` 標記（`… 00 00 ff ff`）——這一
點經實測確認，正是編碼器能運作的基礎。完整 token 文法見[文件三第七節](03-結構與算法說明.md)與
`docs/transfer-spec.md` §2a。

---

## 七、常駐程式（`rsync://`）的銜接

- **常駐程式 socket 上沒有二進位版本整數**——版本已由文字 `@RSYNCD: <ver>.<sub>` 問候協商，
  `setup_protocol` 跳過二進位交換。
- 驗證：`@RSYNCD: AUTHREQD <challenge>` → 用戶端回 `<user> <digest>`，其中
  `digest = base64(MD5(password + challenge))` 去掉結尾 `=`，challenge 是 16 隨機位元組的 22 字元 base64。
  即使協定 29 也用 MD5。
- OK 之後的串流與 SSH 路徑**逐位元組相同**（只差兩個版本 int32）——`cmp` 核對過。
- 序幕之後的 argv 以 NUL 分隔傳送（協定 29 用換行）。這也是為什麼**常駐程式天生對含空白的路徑安全**，
  不需要 `-s`。

---

## 八、`--delete` 的銜接：拉取與推送機制不同

這是一個容易誤解的地方，兩個方向的線路行為**不對稱**，且都由擷取釘死：

- **拉取 `--delete`**：**完全是本機動作**。server argv **不帶** `--delete`，過濾清單維持空的 int32 0，
  雙向都**沒有** `NDX_DEL_STATS`。刪除是 receiver（本機）的工作。
- **推送 `--delete`**：server argv **帶** `--delete`，遠端接收端刪除並用 **`MSG_DELETED`**（多工 tag
  0x6c）**逐項回報**，位在 seed 之後、傳輸階段之前，最深的先報。3.4.3 的既有擷取沒有
  del-stats，但 3.4.4 會在 DONE#3 與 DONE#4 之間送出 `FF 02` 加五個 varint；用戶端會消費這個可選區塊
  `--delete` 也讓推送用戶端多送一個空過濾清單到 c2s。用戶端本身仍不送
  `--delete-after`/`--stats`

---

## 九、`--secluded-args`（`-s`）的銜接

保護含空白/特殊字元的遠端路徑。逐位元組釘死（`ssh31-secluded-spacepath`）：

- server argv **丟掉** `. <paths>` 尾段，bundle 以 `s` 開頭（`-stre.LsfxCIvu`）。
- 被扣住的**遠端側**檔案參數在**版本 int32 之前**以 NUL 分隔清單送出：
  `rsync\0` (argv[0]) + `.\0` (dot-arg 分隔) + 每個遠端路徑 `\0` + 一個孤立的 `\0`（空字串）結尾。
- 只有遠端側路徑上線（拉取=來源、推送=目的），本機路徑不上線，空白原樣保留。
- 常駐程式 session 沒有版本整數，無處放這個前置清單，因此 `-s` 只作用於 SSH（常駐程式本來就 NUL 分隔，
  天生安全）。

---

## 十、刻意不銜接的界線（附理由）

每一條界線都有工程理由，並在 `docs/wire-notes.md`「open questions」留有書面說明：

| 不支援 | 理由 |
|---|---|
| 標準 `-z`（zstd）、lz4、舊 zlib 壓縮 | BCL 只有 deflate；舊 zlib 需要 `Z_INSERT_ONLY` 原語 |
| 協定 27/29 的檔案清單解碼 | 29 世代格式（無 varint）未實作；真 29-only 對端會失步 |
| 推送 `-a` 的額外欄位（uid/gid/連結/裝置） | `FileListWriter` 目前只編碼 `-tr` 欄位集 |
| 大於 2 GiB 的檔案 | 來源整檔讀入記憶體，尚未串流化 |
| `--delete-after`/`--delete-delay` 的 del-stats 區塊 | 我們從不送這些旗標，其線路格式未擷取（仍為推測） |
| 增量遞迴（inc-recurse） | 我們不送 `i` 能力字母；`--no-inc-recursive` 也是官方的 CVE 緩解 |

這些不是「還沒做完」，而是在依賴極少（dependency-light）與逐位元組正確兩個約束下，**經過評估的範圍決定**。

---

## 十一、安全相關的對齊

RsyncWin 對齊了 rsync 3.4.x 的幾個防禦性修正：
- `read_sum_head` 的 count 算術有界檢查。
- del-stats 每個 varint 上限 `MAX_WIRE_DEL_STAT = 1<<28`。
- receiver 對「無基準卻收到符合區塊」的檢查。
- `s2length` 對協商的 digest 長度做驗證（CVE-2024-12084 的實務約束）。
- `--no-inc-recursive` 一貫開啟（CVE-2026-43620 的官方緩解）。

壓縮相關的 CVE（3.4.2/3.4.3 的壓縮 token 邊界）屬 zstd/壓縮解碼路徑；我們的 zlibx 解碼器對 token 長度與
區塊索引都做界檢查，且整檔尾碼是最終安全網（重建歧異 → redo → exit 23，絕不靜默損毀）。
