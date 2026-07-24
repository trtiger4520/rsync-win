# RsyncWin vs rsync 效能與發佈型態比較(FDD / R2R / AOT)

> 日期:2026-07-24　分支:`feature/performance-vs-rsync-81fcf7`
> 這份是 compose 公平比較跑出的可重現紀錄。它解掉了 P11 fair track「client 起不來」的
> blocker,並加上 FDD/R2R/AOT 三種發佈型態的並排比較。**尚未**跑 full profile 五輪矩陣,
> 所以不視為 P11 正式驗收完成;`docs/integrity-evaluation.md` 的 P11 狀態不變

## 1. 摘要與決策

- 大量小檔(啟動密集):今天出貨的 **FDD 版比 stock rsync 慢約 15%**;**AOT ≈ rsync**(0.94–1.12×)
- 大檔吞吐(傳輸密集):**FDD 也慢約 15–18%**(非只是啟動);**AOT ≈ rsync**(0.97–1.03×)
- 記憶體:FDD/R2R 峰值 ~10 MiB,**AOT ~6 MiB**,接近 rsync
- **決策:根 `Dockerfile` 預設改為 `PUBLISH_MODE=aot` / `FINAL_BASE=alpine`**。所有傳輸皆
  byte-for-byte 驗證通過(sha256 與來源一致)

## 2. 環境

| 項目 | 值 |
|---|---|
| Host | Windows 11 `10.0.26200`、x64、24 logical processors |
| Docker | `29.6.1`(Desktop / WSL2 backend) |
| .NET | SDK `10.0.301`、image `dotnet/sdk:10.0-alpine` / `dotnet/runtime:10.0-alpine`(digest pinned) |
| stock rsync | Alpine 3.21、`rsync=3.4.3-r0`(digest pinned) |
| 資源上限 | client `cpuset 0-3` + `mem_limit 8g`;daemon `cpuset 4-7`(避免搶 client 的核) |

## 3. 方法

拓撲 `tools/RsyncWin.Perf/docker/docker-compose.perf.yml`:stock rsync daemon + root helper
共用一個 **named volume**,傳輸全程留在 Docker Linux VM 內,不跨 Windows bind mount(9p/virtiofs
會嚴重扭曲吞吐)。公平性關鍵決定:

- **資料位置**:named volume(VM 內),不用 host bind mount。啟動比較用 `VolumeBridge` 把
  host 生成的資料以 `docker exec helper cp` 進出 volume(**不計時**的 setup);大檔吞吐則直接在
  volume 內用 `/dev/urandom` 生成,完全不碰 host↔volume 複製
- **client 執行身分**:rsyncwin 與 stock rsync 都以 **root** 執行(daemon 也是 root)。非 root
  映像(uid 1000)在共用的 root-owned volume 上會因無法對「非自己擁有」的 transfer-root 目錄設定
  屬性而 EPERM;uid 對吞吐無影響,兩邊都 root 才是對等
- **量測**:1 warm-up + N measured;啟動比較每格取 median、經 manifest(SHA-256)驗證;
  cgroup `memory.peak` 直接讀取
- **兩支 runner**:`Invoke-ComposeBenchmark.ps1`(六種操作 × 三模式的啟動比較)、
  `Invoke-ThroughputProbe.ps1`(大檔 full-copy 吞吐,volume 內生成、逐輪 checksum 驗證)

## 4. 結果 A:啟動密集(smoke small-files,128 × 4 KiB)

`elapsed median ms`(越低越好,median of 3):

| operation | rsync | rsyncwin fdd | rsyncwin r2r | rsyncwin aot | aot/rsync |
|---|---:|---:|---:|---:|---:|
| up-to-date | 880 | 1016 | 1080 | 978 | 1.11× |
| checksum | 897 | 1053 | 1068 | 907 | 1.01× |
| delta | 907 | 1077 | 1084 | 1017 | 1.12× |
| delete | 934 | 1073 | 1051 | 881 | 0.94× |
| full-copy | 944 | 1091 | 1049 | 950 | 1.01× |
| compression | 976 | 1095 | 1090 | 954 | 0.98× |

- 這個尺度下,~900 ms 大多是 `docker compose run` 容器啟動;**模式間的差**才是行程啟動訊號
- FDD 一致地比 rsync 多 ~130–160 ms(.NET 啟動/JIT 稅);R2R 幾乎沒改善;**AOT 抹平差距**
- cgroup `memory.peak`:FDD/R2R ~10 MiB、AOT 趨近 ~6 MiB、rsync ~5–9 MiB(舊 P11 的 RSS=0
  blocker 在 fair track 已解)

## 5. 結果 B:大檔吞吐(4 GiB incompressible,median of 3)

全部 `verify=OK`(dest sha256 與來源一致):

| mode loop | stock rsync | rsyncwin | rsyncwin / rsync |
|---|---:|---:|---:|
| fdd | 65.7 MiB/s | 53.9 MiB/s | 0.82× |
| r2r | 58.2 MiB/s | 56.5 MiB/s | 0.97× |
| aot | 61.6 MiB/s | 63.5 MiB/s | 1.03× |

rsyncwin 內部直接比(同來源,單調):**AOT 63.5 > R2R 56.5 > FDD 53.9 MiB/s**,AOT 比 FDD 快約 18%

- 一個超出預期的發現:傳輸長達 60–76 秒,早已過 JIT 暖身,但 **FDD 在純吞吐仍慢 rsync ~15–18%**。
  這是 **steady-state** 的成本,不只是啟動。此情境是 checksum-CPU 綁死(~65 MiB/s 遠低於 loopback
  頻寬),AOT 原生編譯的雜湊迴圈更貼近 rsync 的 C

## 6. 限制與雜訊(誠實說明)

- **量測雜訊真實存在**:rsync baseline 三輪飄 58–66 MiB/s(±~6%,共用 Docker VM、client 區塊未
  交錯)。小於 ~10% 的差(r2r、aot-vs-rsync)落在雜訊內;FDD 的 ~18% 差在雜訊外
- **絕對 ~60 MiB/s 是 Docker Desktop VM 受限值**(4 核 cpuset + checksum CPU + VM overhead),
  不是裸機數字;但兩邊條件相同,**比較**仍成立
- cgroup `cpu.stat` 在極快傳輸取樣不到(退回 host CPU,不具意義);啟動比較的 CPU 欄位不採用
- smoke 啟動比較被 `docker compose run` 容器啟動主導,是刻意用來凸顯啟動稅

## 7. 已知問題與待辦

- 🐛 **product bug**:`rsyncwin --delete` 在 dest 尾端帶 `/` 時 crash(`LocalTreePruner` 容器內含
  檢查在 root 帶結尾分隔符時誤判)。已開獨立修復 task(需先寫失敗測試,屬刪檔安全碼)。benchmark
  以不帶尾斜線的 dest 繞過
- ⏳ **full profile 五輪矩陣**(large-files 8×1 GiB 等)尚未跑;目前吞吐是 4 GiB 探針。註:六操作
  harness 的 host↔volume bridge 每輪複製,不適合多 GiB;探針改為 volume 內生成來繞過
- ⏳ **Windows `<PublishAot>`(csproj)**:使用者決定「後面測試看看」。需先驗 Windows AOT 能否
  乾淨 publish(需 VC++ build tools)再定

## 8. 重現指令

```powershell
# 啟動密集:六操作 × 三模式(會 build fdd/r2r/aot 映像)
pwsh -File tools/RsyncWin.Perf/Invoke-ComposeBenchmark.ps1 -Modes fdd,r2r,aot -Scenario small-files -Iterations 3

# 大檔吞吐:4 GiB incompressible,volume 內生成,逐輪 checksum 驗證
pwsh -File tools/RsyncWin.Perf/Invoke-ThroughputProbe.ps1 -FileCount 4 -FileSizeMiB 1024 -Iterations 3

# 確認預設 build 現在產出無 runtime 的原生 AOT 映像
docker build -f Dockerfile -t rsyncwin:aot .
docker run --rm --entrypoint sh rsyncwin:aot -c 'command -v dotnet || echo "no dotnet runtime — native AOT"'
```
