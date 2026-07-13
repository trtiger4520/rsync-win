# RsyncWin P11 完整性、效能與跨平台評估

> 本報告是 P11 當次執行的可重現證據，P0–P10 完成時的歷史驗收仍保留在
> [`acceptance-report.md`](acceptance-report.md)。所有數字必須來自本次執行產生的原始
> JSON/CSV，不引用舊報告替代

## 1. 評估狀態

**P11 尚未通過最終驗收。** 應用完整性、完整資料集生成、Linux image PoC 與既有互通測試
已通過，但正式五輪 Linux 公平比較仍在 client 啟動前失敗；Windows practical smoke 雖傳輸
正確，peak RSS 與 literal/matched 欄位沒有取得。因此本報告不發布 RsyncWin/rsync 效能比率，
也不建立 `P11 complete` commit

## 2. 評估邊界

P11 驗證 RsyncWin 已承諾的 CLI 功能面，不宣稱等同 stock rsync 的完整功能。支援面包含
SSH/daemon、pull/push、daemon module listing/auth、遞迴與時間、checksum、delete、
secluded args、zlibx compression，以及 SSH executable override

下列項目維持設計外：protocol 27/29 file-list decode、push archive 的 uid/gid/link/device、
單檔超過 2 GiB、zstd/lz4/old-zlib、delete-after/delete-delay 與 stats del-stats block

## 3. 環境

| 項目 | 本次證據 |
|---|---|
| Git 基準 | `9db9f842cb77438287800512c60b6e119cbcacf1` 加工作樹 P11 變更 |
| Host | Windows `10.0.26200`、x64、24 logical processors |
| .NET | SDK `10.0.301`、runtime `10.0.9` |
| Docker | client/server `29.6.1` |
| stock rsync | Alpine 3.21、`rsync=3.4.3-r0` |
| 執行前 C 槽可用 | 196,322,127,872 bytes |

固定 image digest 與完整欄位見 [`evidence/p11-environment.json`](evidence/p11-environment.json)

## 4. 應用完整性結果

| Gate | 結果 | 證據 |
|---|---:|---|
| Release build | PASS | 0 warnings、0 errors |
| Hermetic | PASS | 426/426：Protocol 274、Fs 33、CLI 43、Interop replay 76 |
| Process-level CLI | PASS | 6/6，每次有 15/120 秒 timeout |
| 既有 live interop | PASS | verifier 實跑原 29 案全綠；新增 process 案另以 focused 6/6 實跑 |
| Linux image smoke | PASS | module list、pull、push、checksum、delete、compression、雙向 zero re-run |

Process-level matrix 實際啟動 Release `rsyncwin`，涵蓋 SSH/daemon pull+push、module listing、
auth、`-r/-t/-a/-c/-s/-z/-e/--delete` 與 long aliases。manifest 比對 path、file/dir type、
size、SHA-256、mtime；另驗證空檔、空目錄、唯讀但可讀來源、Windows 敵意名稱、double-colon
固定 port 873（拒絕時 exit 10）、鎖定來源 exit 23、其他檔仍完成且沒有半成品

10 萬檔由 full dataset gate 負責；vanished source 由既有 `SshPushInteropTests` 負責，避免把
大規模或競態案例重複塞入每次 Application live suite

## 5. 完整資料集結果

固定 seed `0x5253594E4357494E` 的六個 full datasets 均完整生成、雜湊、覆寫重生並再次雜湊，
353.2 秒內全數得到相同 manifest。詳細值見
[`evidence/p11-dataset-manifests.csv`](evidence/p11-dataset-manifests.csv)

| Scenario | Files | Logical bytes | 兩次重生 |
|---|---:|---:|---:|
| small-files | 100,000 | 409,600,000 | PASS |
| large-files | 8 | 8,589,934,592 | PASS |
| mixed-tree | 20,000 | 8,589,934,592 | PASS |
| delta | 2 | 2,147,483,648 | PASS |
| compressible | 4 | 2,147,483,648 | PASS |
| incompressible | 4 | 2,147,483,648 | PASS |

可壓縮與不可壓縮資料各拆成 4 × 512 MiB，總量維持 2 GiB，同時不跨越既有單檔 2 GiB
設計界線

## 6. 效能 smoke 與正式比較狀態

Windows direct practical smoke 對 128 × 4 KiB 資料完成一次 warm-up 與一次 measured iteration，
12/12 raw exit 0、manifest 全部相符、6/6 operation cells 有 wall/throughput/CPU 結果。這些數字
只證明 runner 與操作狀態可用，不是核准計畫要求的五輪正式結果

| Operation | Elapsed ms | Logical MiB/s | CPU ms | Peak RSS | literal/matched |
|---|---:|---:|---:|---:|---|
| full-copy | 1260.25 | 0.397 | 328.13 | 無效（0） | 未取得 |
| up-to-date | 966.03 | 0.518 | 171.88 | 無效（0） | 未取得 |
| delta | 1003.29 | 0.498 | 203.13 | 無效（0） | 未取得 |
| checksum | 1202.45 | 0.416 | 281.25 | 無效（0） | 未取得 |
| compression | 1410.08 | 0.355 | 406.25 | 無效（0） | 未取得 |
| delete | 1039.24 | 0.481 | 187.50 | 無效（0） | 未取得 |

Linux fair smoke 正確產生 24 raw / 12 summary，但兩個 client 都在 Docker 建立前 exit 125：
`--mount` 經 `cmd /c` 傳遞後被解析成單一 type 欄位，Docker 回報 `Target must not be empty`
。summary 防呆已生效：12/12 cells 都是 `hasResult=false`、median/p95 為 null，沒有產生誤導圖表

因此目前沒有可發布的 RsyncWin/rsync 比率，也沒有執行 full profile 五輪；這是 P11 的主要 blocker

## 7. 跨平台結論

| 層級 | 結論 |
|---|---|
| 可編譯 | PASS，固定 .NET 10 SDK/runtime digest 的 multi-stage build |
| 可啟動 | PASS，non-root `$APP_UID`、`/data` volume |
| daemon 傳輸 | PASS，pull/push/checksum/delete/compression/zero re-run |
| Linux 路徑語意 | PASS for PoC，`a:b`/`a_b`、`Case`/`case` 可並存，native `/` 與 ordinal comparer |
| SSH | 僅確認預設 executable 為 PATH `ssh`，沒有列入 Linux 正式支援 |
| 正式跨平台支援 | 尚未宣告；symlink、Unix ownership/mode 與其他平台語意仍在既有範圍外 |

## 8. 剩餘 blocker

1. 修正 Linux runner 的 Windows bind-mount command 傳遞，讓兩個 client 實際啟動
2. Windows direct runner 必須在程序執行期間採樣 peak working set；目前程序結束後讀值為 0
3. Pull CLI 目前只輸出 transferred files/bytes，harness 無法取得 literal/matched；不得用 null 當 0
4. 完成上述項目後重跑 Linux smoke、Windows smoke，再跑 full profile：一次 warm-up＋五次 measured
5. 只有 full raw JSON/CSV 可重建、每 cell 五輪 manifest 正確、fresh verifier PASS 後，才能完成 P11

依專案 failure policy，這三個效能阻擋經兩輪 repair 後仍存在，本次停止擴修並保留精確證據，
不擴張第三輪或降低驗收標準

## 9. 重現命令

```powershell
dotnet build RsyncWin.slnx -c Release --no-restore
dotnet test RsyncWin.slnx -c Release --no-build --filter "Category!=Interop"
dotnet test tests\RsyncWin.Interop.Tests -c Release --filter "FullyQualifiedName~CliApplicationInteropTests"
& .\tests\container\Invoke-LinuxDaemonSmoke.ps1
dotnet run --project tools\RsyncWin.Perf -c Release -- correctness --profile full --scenario all
& .\tools\RsyncWin.Perf\Invoke-LinuxBenchmark.ps1 -Profile full -Scenario all -Iterations 5
& .\tools\RsyncWin.Perf\Invoke-WindowsBenchmark.ps1 -Profile full -Scenario all -Iterations 5
```
