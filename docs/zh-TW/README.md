# RsyncWin 繁體中文說明文件

本目錄是 RsyncWin 專案的繁體中文說明文件。RsyncWin 是一個用 C# / .NET 10 撰寫的**原生 Windows rsync
用戶端**，直接實作 rsync 線路協定，讓 Windows 與標準 rsync 伺服器逐位元組互通，不需要 WSL 或 Cygwin。

## 文件導覽

| # | 文件 | 內容 |
|---|---|---|
| 1 | [專案說明](01-專案說明.md) | 定位、解決的問題、範圍、核心設計決策、開發方法、現況 |
| 2 | [使用說明](02-使用說明.md) | 命令列語法、四種傳輸組合、旗標一覽、離開碼、疑難排解 |
| 3 | [結構與算法說明](03-結構與算法說明.md) | 五層架構、線路編解碼、checksum、差異傳輸、壓縮、引擎、Windows 檔案系統 |
| 4 | [與 rsync 銜接程度說明](04-與rsync銜接程度說明.md) | 互通程度、協定交握、逐位元組事實、`-z`/`--delete`/`-s` 的銜接、界線 |
| P11 | [完整性、效能與跨平台評估](../integrity-evaluation.md) | CLI E2E、壓力與 rsync 對照、Linux image PoC |

## 快速開始

```powershell
dotnet build
dotnet run --project src/RsyncWin.Cli -- -rt user@host:/srv/data/ D:\backup\data
```

## 相關文件（英文，逐位元組協定事實）

- [`docs/roadmap.md`](../roadmap.md)：階段計畫與提交證據（P0–P10 全部完成）
- [`docs/wire-notes.md`](../wire-notes.md)：逐位元組協定事實與 open questions
- [`docs/codec-spec.md`](../codec-spec.md)、[`flist-spec.md`](../flist-spec.md)、
  [`transfer-spec.md`](../transfer-spec.md)、[`daemon-spec.md`](../daemon-spec.md)：各層位元組佈局
- [`docs/acceptance-report.md`](../acceptance-report.md)：整體驗收報告
- [`docs/integrity-evaluation.md`](../integrity-evaluation.md)：P11 當次完整性、效能與跨平台證據
