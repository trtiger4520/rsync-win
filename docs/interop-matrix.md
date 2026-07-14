# Live rsync 互通矩陣

Live 測試只在 Windows 主機透過 PowerShell 啟動，rsync 執行檔固定放在一次性的 Docker
peer image 內。測試不會在 Windows 主機安裝 rsync

## 前置條件

- Windows 11、PowerShell 7、.NET 10 SDK
- Docker Desktop 已啟動，`docker info` 可正常回應
- 主機具備 OpenSSH `ssh.exe`
- 首次建立 3.4.4 image 需要能連線到官方 rsync 下載站

## 執行

```powershell
pwsh -File scripts/Invoke-LiveInteropMatrix.ps1 -Profile Smoke
pwsh -File scripts/Invoke-LiveInteropMatrix.ps1 -Profile Full
pwsh -File scripts/Invoke-LiveInteropMatrix.ps1 -Profile Guard
pwsh -File scripts/Invoke-LiveInteropMatrix.ps1 -Peer rsync-3.4.4 -Profile Full
```

`-Peer` 是 `-PeerId` 的別名。未指定 peer 時會依 manifest 順序逐一執行 3.4.3、3.4.4，
不並行建立 fixture 或 daemon

## 固定版本

`test-fixtures/interop/peer-matrix.json` 固定每個 peer 的 id、版本、image tag、官方來源
tarball SHA-256 與 pinned Alpine base image digest

- rsync 3.4.3 使用 Alpine pinned package `rsync=3.4.3-r0`，同時保留官方 tarball URL 與
  SHA-256 作為 provenance
- rsync 3.4.4 從官方 `rsync-3.4.4.tar.gz` 建置，建置前以 manifest SHA-256 驗證
- fixture 啟動後立即在 container 內執行 `rsync --version`，版本不符合 manifest 會使該 row
  失敗

## Profile

| Profile | 執行內容 |
|---|---|
| Smoke | SSH handshake、SSH pull/push 全量與 rerun 零傳輸、daemon anonymous/auth pull、daemon push、module listing |
| Full | 全部 `[Category=Interop]` 測試，包含 delta、checksum、delete、compression、secluded args、vanished/error 與 daemon 錯誤情境 |
| Guard | SSH `--protocol=29` handshake preamble；只驗證 handshake，不宣稱完整 29-era flist/transfer 相容 |

Smoke 使用既有測試加上的 `Profile=Smoke` trait，不複製大型 Theory。Full 使用所有 live
interop tests，新增情境只要加入一般 interop test 即會納入 Full

## Artifact 與通過門檻

每次執行會建立 `artifacts/live-interop/<timestamp>/<peer>/`，包含：

- `summary.json`、`peer-info.json`：manifest、image digest、版本輸出、profile、exit code、耗時
- `dotnet-test.stdout.log`、`dotnet-test.stderr.log`、TRX
- fixture dispose 前擷取的 SSH/daemon container log

每個 peer row 必須在 timeout 內完成、版本 probe 必須符合 manifest、測試 exit code 必須為 0
現有測試另外驗證檔案 SHA-256、rerun 零傳輸、delta literal/matched 統計、auth/readonly/
unknown module 錯誤不 hang。fixture 使用 `--rm`，測試後應沒有
`rsyncwin-interop` 殘留 container

## 執行節奏與邊界

- 日常開發執行 Smoke
- 修改 Protocol、Engine、Transport、Fs 或 CLI 後執行 Full
- 發布前對 3.4.3 與 3.4.4 都執行 Full，再執行 Guard
- 目前 live tests 直接呼叫 `PullSession`/`PushSession`，不是 CLI E2E。CLI SSH 參數注入能力
  完成後，另行增加 CLI 專用 row
