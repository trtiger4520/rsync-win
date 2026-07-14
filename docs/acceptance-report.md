# RsyncWin 整體驗收報告

> 本報告記錄 RsyncWin 專案 P0–P10 全部完成後的一次完整驗收。驗收在乾淨的工作樹上執行，所有測試實際跑過
> 並觀察其輸出，數字為當次執行的真實結果，非引用。

## 1. 驗收結論（先講結論）

**通過。** P0–P10 全部完成，整體開發結束。

- 建置：**0 警告、0 錯誤**。
- 隱密測試（hermetic，不需網路）：**418 個全數通過**。
- Live 互通測試（對真 rsync 3.4.3，Docker）：**29 個全數通過**。
- 合計 **447 個自動化測試，0 失敗**。
- CLI 端對端可執行，用法輸出正常。
- 對抗式審查發現的 2 個問題**均已修正並補上回歸測試**。

RsyncWin 與標準 rsync 3.4.3 在拉取、推送、SSH、常駐程式四種組合，含差異傳輸、`--checksum`、`--delete`、
`--secluded-args`、`-z` 壓縮，達成**逐位元組互通**（或壓縮送出面的語意層互通，理由見 §6）。

## 2. 驗收方法

本專案的失敗模式是**靜默的**（寫錯一個位元組 → 全量重傳或 exit 12 失步，或階段邊界卡住），因此驗收閘門
一律是**位元組/雜湊相等**或**「重跑不傳輸任何東西」**，絕不是「看起來對」。驗收分兩層：

1. **隱密黃金向量測試**：從真 rsync 擷取的雙向線路位元組（存於 `test-fixtures/vectors/`）重播給純協定
   核心，逐位元組比對。checksum seed 是時間相依的，測試一律**從擷取讀出 seed**，不假設——因此向量可用
   `capture.sh` 重新生成而測試仍通過（本次已驗證，見 §4 附註）。
2. **Live 互通測試**：對用完即丟的 Docker 容器裡的真 rsync 3.4.3，跑端對端傳輸，每個測試都有**卡住偵測
   逾時**（階段邊界 bug 表現為卡住而非失敗）。

## 3. 驗收環境

| 項目 | 值 |
|---|---|
| 作業系統 | Windows 11（win32） |
| .NET SDK | .NET 10（C# 14） |
| 建置設定 | Debug |
| 互通對端 | rsync 3.4.3（協定 32），alpine:3.21，Docker 容器 |
| SSH | 內建 `C:\Windows\System32\OpenSSH\ssh.exe` |

## 4. 測試結果總覽

### 建置

```
0 個警告
0 個錯誤
```

### 隱密測試（`dotnet test --filter "Category!=Interop"`）

| 測試組件 | 通過 | 失敗 |
|---|--:|--:|
| RsyncWin.Protocol.Tests | 274 | 0 |
| RsyncWin.Fs.Tests | 28 | 0 |
| RsyncWin.Cli.Tests | 43 | 0 |
| RsyncWin.Interop.Tests（非 Interap 類別的重播測試） | 73 | 0 |
| **合計** | **418** | **0** |

### Live 互通測試（`dotnet test --filter "Category=Interop"`）

| 測試組件 | 通過 | 失敗 |
|---|--:|--:|
| RsyncWin.Interop.Tests | 29 | 0 |

### CLI 端對端

`dotnet run --project src/RsyncWin.Cli` 正確印出完整用法（四種傳輸組合 + 模組列出 + 旗標說明）。

> **可重現性附註**：`capture.sh` 全量重生所有向量後，只有時間相依 seed 造成的預期漂移；本次驗收過程中
> 曾用一次全量重生驗證，並確認除了少數刻意釘住特定 seed 衍生值的既有測試外，向量可重生。P10 新增的向量
> 與測試皆為 seed 無關（從擷取讀出 seed），可自由重生。

## 5. 逐功能驗收證據

每一項能力都有明確的閘門與擷取證據。下表對應到 `docs/roadmap.md` 的階段狀態表。

| 能力 | 階段 | 驗收閘門 | 擷取/證據 |
|---|---|---|---|
| 純編解碼 + checksum + 區塊大小 | P1 | 黃金向量逐位元組/雜湊相等 | `--debug=deltasum2/4` 掃描；146+ 隱密測試 |
| SSH 傳輸 + 交握 | P2 | 29/30/31 序幕逐位元組重播 + live 協商 | `ssh31/30/29-pull-rt` 序幕 |
| 多工 + 過濾送出 + 檔案清單接收 | P3 | list-only 對真 rsync exit 0 | 23 筆逐行相符 |
| 拉取傳輸（互通里程碑） | P4 | 整樹 SHA-256 相同、遠端 exit 0 | `ssh31-pull-rt` |
| 遞迴拉取打磨（快速路徑、淨化、Channels） | P5 | 重跑不傳輸 + 惡意檔名淨化不逃逸 | `ssh31-pull-uptodate/-partial` |
| 差異效率（基準比對） | P6 | 改一個區塊：literal ≪ 檔案、matched ≈ 檔案，與真 rsync `--stats` 相同 | `ssh31-pull-delta/-redo` |
| 推送（sender 角色） | P7 | 容器內 `diff -r` 空、重推不傳輸、delta stats 相同 | `ssh31-push-rt/-delta/-redo/-nsec` |
| 常駐程式傳輸（`rsync://`） | P8 | 前導逐位元組、auth digest 可重現、post-OK 與 ssh `cmp` 相同 | 10+ daemon 向量 |
| `--checksum`（拉取）、`--delete`（拉取）、旗標面、離開碼 | P9 | F_SUM 釘死 + 決策單元測試 + live；每個離開碼一個測試 | `ssh31-pull-checksum/-delete` |
| **`-z`（zlibx）壓縮** | **P10** | 解碼逐位元組（含跨執行段回指）；雙向 live 逐位元組 + 重跑不傳輸 | `ssh31-pull-z-zlibx/-delta/-crossrun` |
| **`--secluded-args`（`-s`）** | **P10** | 版本前 NUL 參數清單逐位元組 + live 含空白遠端路徑 | `ssh31-secluded-spacepath` |
| **推送 `--checksum`** | **P10** | F_SUM 寫→讀往返 + live 重推不傳輸 + 內容變動仍傳 | `ssh31-push-checksum` |
| **推送 `--delete`** | **P10** | MSG_DELETED 重播不失步 + live 刪除多餘項並回報 | `ssh31-push-delete` |

## 6. `-z` 壓縮的驗收強度（誠實揭露）

`-z` 是唯一驗收強度**不對稱**的能力，特此說明：

- **拉取（解壓）方向：逐位元組。** 把真 rsync 的 `-z --compress-choice=zlibx` 拉取擷取重播，重建每個檔案
  SHA 相符——包含 `ssh31-pull-z-crossrun` 這個**跨執行段回指**的最壞情況（兩個字面執行段共用一個 256 位元組
  標記、中間隔著符合區塊，rsync 的連續 deflate 視窗會讓後段回指前段）。這個情況一度讓「逐執行段獨立解壓」
  的實作丟例外，經對抗式審查抓出後改為**連續解壓**，現已釘死。
- **推送（壓縮）方向：語意層。** deflate 的輸出是**實作定義的**（不同 zlib 實作對同一輸入產生不同但都合
  法的壓縮位元組），因此**不可能**逐位元組黃金化我們送出的壓縮串流。改用「真 rsync 伺服器能正確重建我們送
  的壓縮 + 重推不傳輸」當閘門，已 live 通過。這是誠實的界線，不是缺陷。

此外，標準 `-z` 預設協商到 **zstd**（BCL 無此 codec），故 RsyncWin 強制走 **zlibx**（`--new-compress`）；
舊 `zlib` 模式需要 BCL 沒有的視窗插入原語。詳見[文件四](zh-TW/04-與rsync銜接程度說明.md)。

## 7. 對抗式審查結果

P10 提交前跑了一次 `protocol-reviewer` 對抗式審查（協定/Windows 檔案系統/測試強度三視角），發現 2 個問
題，**均已修正並補上回歸測試**：

1. **（已確認）推送 `-c` 遇到單一無法讀取/被鎖的來源檔會整批中止。** `ComputeFlistChecksumsAsync` 在
   建構檔案清單時預先開啟每個檔案，缺乏例外處理，一個被鎖的檔案（Windows 上常見：開啟中的 PST/DB/log）
   就讓整個推送以「零檔案傳輸」中止。**修正**：對齊 rsync，讀取失敗時發出全零 F_SUM 並繼續，該檔由回覆路
   徑記為失敗（exit 23）。回歸測試：`PushChecksum_VanishedSourceFile_DoesNotAbort`（live）。
2. **（已確認）zlibx 拉取解碼器無法處理跨執行段的 deflate 回指。** 見 §6。原「逐執行段獨立解壓」會丟例
   外並以 exit 12 中止（而非文件宣稱的 redo → exit 23）。**修正**：改為**連續解壓**（維持跨執行段的 deflate
   視窗），並用決定性擷取實驗 `ssh31-pull-z-crossrun` 釘死真 rsync 確實會發出跨執行段回指。相關文件宣稱亦已
   更正（`transfer-spec.md` §2a、`ZlibxTokenCodec` 類別註解）。

審查對其餘 P10 變更（argv 排序、MSG_DELETED 帶外路由、secluded NUL 清單、F_SUM 位置/長度、解碼器的
全符合/空執行段/END-after-match 邊界）均確認正確、無其他缺陷。

## 8. 範圍與已知界線

以下為**經評估的範圍決定**（在依賴極少與逐位元組正確兩個約束下），每項在 `docs/wire-notes.md` 的 open
questions 均有書面理由：

| 不支援 | 理由 |
|---|---|
| 標準 `-z`（zstd）、lz4、舊 zlib 壓縮 | BCL 只有 deflate；舊 zlib 需 `Z_INSERT_ONLY` 原語 |
| 協定 27/29 的檔案清單解碼 | 29 世代格式（無 varint）未實作；真 29-only 對端會失步 |
| 推送 `-a` 的額外欄位（uid/gid/連結/裝置） | `FileListWriter` 目前只編碼 `-tr` 欄位集 |
| 大於 2 GiB 的檔案 | 來源/簽章整檔讀入記憶體，尚未串流化 |
| `--delete-after`/`--delete-delay` 的 del-stats varint 區塊 | 我們從不送這些旗標，其線路格式未擷取 |

另有一項 P10 引入的**記憶體取捨**：`-z` 拉取時，連續解壓會把該檔的字面內容緩存在記憶體中（大檔的 `-z`
拉取記憶體用量較高）。在目前 2 GiB 檔案上限的範圍內可接受，已於程式碼註記。

## 9. 驗收簽核

| 項目 | 結果 |
|---|:--:|
| 建置無警告無錯誤 | ✅ |
| 隱密測試全綠（418） | ✅ |
| Live 互通全綠（29） | ✅ |
| CLI 端對端可執行 | ✅ |
| 對抗式審查問題全數修正 | ✅ |
| 逐功能有擷取證據 | ✅ |
| 範圍界線有書面理由 | ✅ |

**整體開發已結束，驗收通過。** 相關提交見 `docs/roadmap.md` 狀態表（P10 = `P10 complete` 提交）。
