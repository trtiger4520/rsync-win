# Windows 檔案系統驗證

本文件記錄 Windows NTFS 相關的驗證矩陣、前置條件與判定方式

## 驗證政策

- `RsyncWin.Fs` 負責 Windows 路徑、屬性、reparse point 與本機檔案操作
- symlink、junction、device 與其他 reparse point 維持警告後跳過，不追蹤目標、不映射 Unix metadata
- 長路徑採用目前 .NET 原生路徑策略，測試必須實際超過 260 字元，不新增 `\\?\` 抽象層
- Windows 檔案系統錯誤歸類為 exit 11，協定串流錯誤維持 exit 12
- 測試不得用無訊息 `return` 略過能力不足，環境不支援時必須輸出明確 Skip 原因

## 前置條件

在 PowerShell 執行：

```powershell
Get-ComputerInfo -Property WindowsProductName,WindowsVersion,OsBuildNumber
Get-Command ssh.exe
Get-Process 'Docker Desktop','com.docker.backend' -ErrorAction SilentlyContinue
docker info --format 'ServerVersion={{.ServerVersion}} OSType={{.OSType}} OperatingSystem={{.OperatingSystem}}'
```

symlink/junction 案例需要 Windows 開發人員模式或 `SeCreateSymbolicLinkPrivilege`

Docker interop 只允許使用 throwaway container，不能在 Windows host 安裝 rsync

## 測試矩陣

| 層級 | 案例 | 通過條件 |
|---|---|---|
| `RsyncWin.Fs.Tests` | DOS 保留名、非法字元、控制字元、空 segment、尾端點與空白 | 映射結果安全且碰撞行為穩定 |
| `RsyncWin.Fs.Tests` | 超過 260 字元的 enumerate、prune | 檔案可讀取、可刪除、無暫存殘留 |
| `RsyncWin.Fs.Tests` | 唯讀檔案與唯讀目錄 | 屬性可清除，檔案與目錄正常刪除 |
| `RsyncWin.Fs.Tests` | file/directory symlink、junction 與其他 reparse point | 不追蹤外部 target，保留項目不被誤刪 |
| `RsyncWin.Fs.Tests` | 缺檔、鎖定檔、目錄路徑 | 回傳 null 或明確錯誤，不造成例外外溢 |
| `RsyncWin.Interop.Tests` | hostile wire names 與 mapped path containment | 所有結果留在目的根目錄 |
| `RsyncWin.Interop.Tests` | PullSession delete 與外部 reparse target | extraneous 項目移除，外部資料保留 |
| Docker SSH interop | 長路徑 pull 與 push | exit 0、檔案內容 SHA-256 一致 |
| Docker SSH interop | hostile names、重跑與 delete | 映射正確、重跑無額外傳輸、無誤刪 |

## 執行流程

```powershell
dotnet build RsyncWin.slnx --configuration Release -warnaserror
dotnet test tests/RsyncWin.Fs.Tests/RsyncWin.Fs.Tests.csproj --filter "Category=WindowsFs"
dotnet test tests/RsyncWin.Interop.Tests/RsyncWin.Interop.Tests.csproj --filter "Category=WindowsFs"
dotnet test RsyncWin.slnx --filter "Category!=Interop"
dotnet test RsyncWin.slnx --filter "Category=Interop"
```

Interop 測試組件會停用平行化，避免多個 Docker fixture 同時啟動造成 SSH timeout

每個 interop 案例都必須使用 timeout，並在測試後確認：

```powershell
Get-ChildItem $env:TEMP -Directory -Filter 'rsyncwin-interop-*' -ErrorAction SilentlyContinue
Get-ChildItem $env:TEMP -Directory -Filter 'rsyncwin-pushdel-*' -ErrorAction SilentlyContinue
docker ps -a --filter 'label=rsyncwin-interop=1'
```

若能力不足而 Skip，結果必須記錄為未驗證，不可視為通過

## 本輪驗證證據

2026-07-14 在 Windows 主機完成以下檢查：

- Release build：`dotnet build RsyncWin.slnx --no-restore --configuration Release -warnaserror`，0 warnings、0 errors
- `Category=WindowsFs`：90/90 通過，其中 Fs 61、PullSession/Interop 29
- `Category!=Interop`：455/455 通過
- `Category=Interop`：37/37 通過，包含長路徑 pull 與 push，內容 SHA-256 一致
- CLI smoke：未知旗標 exit 1；junction push 輸出 `skipping symlink`，SSH 連線拒絕時 exit 5
- 本輪產生的 `rsyncwin-interop-*`、`rsyncwin-pushdel-*` 無輸出，沒有新增暫存殘留
- `docker ps -a --filter label=rsyncwin-interop=1` 無輸出，沒有殘留 interop container

本輪使用 `--no-restore` 重用已還原套件；直接 restore 會因目前沙盒無法讀取使用者 NuGet 設定檔而失敗，並非專案編譯或測試錯誤

## 驗收標準

- Windows FS 分類、hermetic 測試全部通過
- 長路徑檔案實際建立、讀取、傳輸與刪除成功
- hostile name 不可逃逸目的根目錄
- 外部 symlink/junction target 永不被刪除
- 唯讀檔案可替換，鎖定 basis 不造成協定失步
- Pull/Push Docker interop exit 0 且 SHA-256 一致
- 重跑傳輸量為零或符合既有 quick-check 結果
- 測試與 interop container 清理完成

## 本輪不包含

- 單一檔案來源與 trailing-slash semantics
- symlink 保留、ACL、owner、group、Unix permissions 映射
- 公開 repo 的授權、秘密、CI 與供應鏈審查
