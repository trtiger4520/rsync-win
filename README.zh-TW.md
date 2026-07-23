# RsyncWin

[English](README.md) · **繁體中文**

> **⚠️ Experimental / Personal Project:** This tool was developed with AI assistance for
> personal use/learning purposes.
>
> 實驗性 / 個人專案：本工具在 AI 協助下開發，供個人使用與學習之用。依 MIT 授權以現狀（as-is）
> 提供，不含任何擔保，且**不會積極維護**。

**RsyncWin 是一個用 C# / .NET 10 撰寫的原生 Windows rsync 用戶端**。它不移植 rsync 的程式碼，
而是直接實作 rsync 的線路協定（protocol 31，可協商降至 30/29），讓 Windows 機器能與**標準的
rsync 伺服器**逐位元組互通——**不需要 WSL、Cygwin，也不需要任何 rsync 二進位檔**。

## 下載與安裝

**預編譯執行檔（建議）**：到 [Releases](https://github.com/trtiger4520/rsync-win/releases) 下載
`rsyncwin-<版本>-win-x64.zip`，解壓後直接執行裡面的 `rsyncwin.exe`。這是自包含（self-contained）
的單一執行檔，不需要另外安裝 .NET runtime。

**從原始碼建置**：需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```powershell
# 直接執行
dotnet run --project src/RsyncWin.Cli -- -rt user@example.com:/srv/data/ D:\backup\data

# 產生自包含的單一執行檔
dotnet publish src/RsyncWin.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

**執行需求**：Windows x64。SSH 傳輸使用系統內建的 `C:\Windows\System32\OpenSSH\ssh.exe`，不需要
額外的 SSH 用戶端。（Docker 只有在跑實機互通測試時才需要，一般使用不需要。）

## 使用方式

`rsyncwin` 一定是「一個本機路徑 + 一個遠端路徑」，方向由**哪一邊是遠端**決定：遠端在來源就是
拉取（pull），遠端在目的就是推送（push）。遠端路徑有三種寫法：

| 形式                                       | 意義                                       |
| ------------------------------------------ | ------------------------------------------ |
| `[user@]host:path`                         | 透過 SSH（單一冒號，使用內建的 `ssh.exe`） |
| `rsync://[user@]host[:port]/module[/path]` | 透過 rsync 常駐程式（預設埠 873）          |
| `[user@]host::module[/path]`               | 同上的簡寫                                 |

Windows 路徑不會被誤判：`D:\backup`、`D:/backup`、`\\server\share` 一律視為本機路徑。

### 常用範例

```powershell
# SSH 拉取：從遠端拉整個目錄樹（遞迴 + 保留時間）
rsyncwin -rt user@example.com:/srv/data/ D:\backup\data

# SSH 推送：把本機目錄推到遠端
rsyncwin -rt D:\projects\site\ user@example.com:/var/www/site/

# daemon 匿名拉取
rsyncwin -rt rsync://mirror.example.com/debian/ D:\mirror\debian

# daemon 密碼驗證（密碼放環境變數，不進命令列）
$env:RSYNC_PASSWORD = "opensesame"
rsyncwin -rt rsync://alice@host/secret/ D:\secret

# 列出 daemon 提供的模組
rsyncwin rsync://mirror.example.com/
```

### 旗標一覽

| 旗標 | 長格式            | 說明                                              | pull | push |
| ---- | ----------------- | ------------------------------------------------- | :--: | :--: |
| `-r` | `--recursive`     | 遞迴子目錄                                        |  ✅  |  ✅  |
| `-t` | `--times`         | 保留修改時間（預設即開啟）                        |  ✅  |  ✅  |
| `-a` | `--archive`       | 封存模式（含 -r -t 及連結/擁有者/群組/裝置/權限） |  ✅  |  ❌  |
| `-c` | `--checksum`      | 以整檔 checksum 比對，而非 mtime+大小             |  ✅  |  ✅  |
|      | `--delete`        | 刪除目的端多出來、來源端沒有的檔案                |  ✅  |  ✅  |
| `-s` | `--secluded-args` | 保護含空白/特殊字元的遠端路徑                     |  ✅  |  ✅  |
| `-z` | `--compress`      | 用 zlibx 壓縮傳輸的 token 串流                    |  ✅  |  ✅  |
| `-e` | `--rsh`           | 指定要用的遠端 shell（預設內建 ssh.exe）          |  ✅  |  ✅  |
| `-h` | `--help`          | 顯示內建使用說明（用法與所有選項）並以 0 離開     |  —  |  —  |

短旗標可以合併：`-rtcz`、`-rtz --delete` 等。各旗標的重點行為、rsync 數值離開碼對照表、
密碼與憑證、疑難排解，見[使用說明](docs/zh-TW/02-使用說明.md)。

## 功能與範圍

差異傳輸（delta transfer）只傳有變動的區塊；已通過與真實 rsync 3.4.3 / 3.4.4 的實機互通矩陣
驗證。純 CLI、沒有 GUI；設計上刻意排除的項目與理由（29 世代檔案清單、zstd/舊 zlib、push 的
`-a` 額外欄位、>2 GiB 檔案等）詳見[專案說明](docs/zh-TW/01-專案說明.md)。

## 文件導覽

### 繁體中文（從這裡開始）

| #   | 文件                                                          | 內容                                                                   |
| --- | ------------------------------------------------------------- | ---------------------------------------------------------------------- |
| —   | [docs/zh-TW/README.md](docs/zh-TW/README.md)                  | 繁中文件目錄與導覽                                                     |
| 1   | [專案說明](docs/zh-TW/01-專案說明.md)                         | 定位、解決的問題、範圍、核心設計決策、開發方法、現況                   |
| 2   | [使用說明](docs/zh-TW/02-使用說明.md)                         | 命令列語法、四種傳輸組合、旗標一覽、離開碼、疑難排解                   |
| 3   | [結構與算法說明](docs/zh-TW/03-結構與算法說明.md)             | 五層架構、線路編解碼、checksum、差異傳輸、壓縮、引擎、Windows 檔案系統 |
| 4   | [與 rsync 銜接程度說明](docs/zh-TW/04-與rsync銜接程度說明.md) | 互通程度、協定交握、逐位元組事實、`-z`/`--delete`/`-s` 的銜接、界線    |

### 英文（逐位元組協定事實）

- [docs/roadmap.md](docs/roadmap.md) — 階段計畫、狀態表與提交證據，以及每階段的強制工作方法
- [docs/wire-notes.md](docs/wire-notes.md) — 已驗證的協定事實、出處與陷阱清單
- [docs/codec-spec.md](docs/codec-spec.md)、[flist-spec.md](docs/flist-spec.md)、
  [transfer-spec.md](docs/transfer-spec.md)、[daemon-spec.md](docs/daemon-spec.md) — 各層位元組佈局
- [docs/interop-matrix.md](docs/interop-matrix.md) — 實機互通矩陣（rsync 3.4.3 / 3.4.4）
- [docs/acceptance-report.md](docs/acceptance-report.md) — 整體驗收報告

## 測試

```powershell
dotnet test --filter "Category!=Interop"   # 快速 hermetic 測試（不需要 rsync）
dotnet test --filter "Category=Interop"    # 對真實 rsync 的實機互通測試（需 Docker）
```

Release workflow 只跑跨平台的純核心測試（`RsyncWin.Protocol.Tests`）當發佈門檻；打 tag 前請在
本機跑完整 hermetic 套件。驗證方法見 [docs/roadmap.md](docs/roadmap.md) 的 working method 章節。

## 授權與出處

本專案自身的程式碼採 **MIT 授權**（見 [LICENSE](LICENSE)）。

本專案不含 GPL rsync 的程式碼：canonical rsync（GPLv3）僅作為**行為**參考，表達式層級的內容
取自 BSD/ISC 授權的 [openrsync](https://github.com/kristapsdz/openrsync) 與
[gokrazy/rsync](https://github.com/gokrazy/rsync)，或依文件化演算法重新產生。
