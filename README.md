# Command Palette PowerMeterBand

在 [PowerToys Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) 的 Dock 上，即時顯示整台電腦目前的耗電功率。

![Dock 上的即時耗電功率](docs/dock.png)

## 功能

- **即時功率**：用電池時，Dock 上每秒更新整機耗電（W）。滑鼠停在上面會顯示 CPU 功率和電池充放電功率。
- **插電時可選顯示內容**：插電時量不到整機功率（見下方說明），可以在詳細資訊頁切換 Dock 要顯示**充電功率**（例如 `+42 W`；充電器供電不足、電池在補電時會顯示負值，例如 `-12 W`）或 **CPU 功率**。設定會保留。
- **詳細資訊頁**：點一下會在命令選擇區打開詳細頁面，顯示整機功率、CPU 封裝功率、GPU 功率、電池充放電、電量（Wh）和預估剩餘或充飽時間。
- **跟內建項目一致**：圖示亮度、間距、等寬數字都跟內建的效能監視器（CPU、記憶體、電池）相同，數字跳動時寬度不變。
- **佔用很少**：約 0.4% 的單一 CPU 核心、約 60 MB 記憶體。

![詳細資訊頁](docs/details.png)

## 資料來源

| 項目 | 來源 |
|---|---|
| 整機功率 | Windows 效能計數器 `\Power Meter(Power Meter (0))\Power`（ACPI 電源計量器）。沒有這個計數器時，改用電池放電功率 |
| CPU 封裝功率 | `\Energy Meter(RAPL_Package0_PKG)\Power`（Intel RAPL） |
| GPU 功率 | `\Energy Meter(RAPL_Package0_PP1)\Power`（Intel RAPL 內顯電源平面） |
| 電池 | `Windows.Devices.Power.Battery` |

ACPI 電源計量器量的是「從電池流出的電」，**插電時讀數是 0**，筆電也沒有其他感測器能量整機用電，所以整機功率只在使用電池時有數值。

不是每台電腦都有 Power Meter 計數器，可以先用 PowerShell 檢查：

```powershell
Get-Counter '\Power Meter(*)\Power', '\Energy Meter(*)\Power'
```

## 需求

- Windows 11 x64
- PowerToys，並在命令選擇區設定中啟用 Dock
- [.NET 10 SDK](https://dotnet.microsoft.com/download)：`winget install Microsoft.DotNet.SDK.10`

不需要 Visual Studio，也不需要 Windows SDK 或開發人員模式。打包工具會由腳本自動從 NuGet 下載，安裝的是簽章過的 MSIX。

## 建置與安裝

1. 把專案 clone 到**路徑不含中文等非 ASCII 字元**的資料夾，例如 `D:\PowerMeterBand`。CsWinRT 在含中文的路徑下會編譯失敗。
2. 打開 `src/AppxManifest.xml`，把 `<Identity>` 的 `Publisher="CN=Terry"` 改成你自己的名稱（例如 `CN=YourName`），`PublisherDisplayName` 也一起改。
3. 第一次執行時加上 `-NewCert`，腳本會建立一張跟 Publisher 同名的自簽憑證：

   ```powershell
   .\src\build-and-install.ps1 -Version 1.0.0.0 -NewCert
   ```

4. 用**系統管理員身分**執行腳本最後印出的 `Import-Certificate ...` 指令，讓 Windows 信任這張憑證（只需要做一次）。
5. 再執行一次，就會編譯、打包、簽章並安裝：

   ```powershell
   .\src\build-and-install.ps1 -Version 1.0.0.0
   ```

6. 把它加到 Dock：在命令選擇區搜尋「即時耗電功率」，使用「Pin to Dock」；也可以進入 Dock 的編輯模式，用「Add band」加入。

之後修改程式碼，只要用**更大的版本號**再執行一次腳本就能更新，例如 `-Version 1.0.1.0`。版本號沒有比已安裝的大，腳本會直接擋下來。

## 專案結構

```
src/
  PowerSampler.cs              讀取功率與電池資料
  PowerDockBand.cs             Dock 上的項目（每秒更新）
  PowerDetailsPage.cs          點擊後的詳細資訊頁（Adaptive Card）
  PluggedInDisplay.cs          插電時顯示內容的設定與切換按鈕
  DockValueListItem.cs         固定標籤寬度、等寬數字
  PowerMeterCommandsProvider.cs / PowerMeterExtension.cs / Program.cs
                               Command Palette 擴充套件的進入點
  AppxManifest.xml             MSIX 套件資訊
  Assets/                      圖示（閃電 SVG 分深色、淺色兩版）
  build-and-install.ps1        編譯 → 打包 → 簽章 → 安裝
```

## 授權

[MIT](LICENSE)
