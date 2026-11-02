# 編譯、打包、簽章，並安裝成新版本（不需系統管理員）
# 用法：.\build-and-install.ps1 -Version 1.0.14.0
#       第一次使用可加 -NewCert，自動建立與 AppxManifest Publisher 相同名稱的自簽憑證
# 注意：專案路徑不能有中文等非 ASCII 字元（cswinrt 會失敗）
param(
    [Parameter(Mandatory)][string]$Version,
    [switch]$NewCert
)
$ErrorActionPreference = 'Stop'

$src     = $PSScriptRoot
$dist    = Join-Path (Split-Path $src) 'dist'
$stage   = "$dist\stage"
$tools   = "$dist\tools"
$env:PATH = "C:\Program Files\dotnet;$env:PATH"

# 明確用 UTF-8 讀寫：PowerShell 5.1 的 Get-Content 會把沒有 BOM 的檔案當 Big5，中文會變亂碼
$utf8 = New-Object Text.UTF8Encoding $false
$identity = ([xml][IO.File]::ReadAllText("$src\AppxManifest.xml", $utf8)).Package.Identity
$packageName = $identity.Name
$publisher = $identity.Publisher

$installed = (Get-AppxPackage $packageName).Version
if ($installed -and [version]$installed -ge [version]$Version) {
    throw "版本 $Version 不大於已安裝的 $installed，請用更大的版本號"
}

# 簽章憑證的主體必須和 AppxManifest 的 Publisher 完全相同
New-Item -ItemType Directory -Force $dist | Out-Null
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert -and $NewCert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName "$packageName signing" -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(10)
    Export-Certificate -Cert $cert -FilePath "$dist\$packageName.cer" | Out-Null
    Write-Host "已建立憑證 $publisher。請用系統管理員身分執行下面這行信任它，再重新執行本腳本："
    Write-Host "  Import-Certificate -FilePath `"$dist\$packageName.cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    return
}
if (-not $cert) {
    throw "找不到主體為 $publisher 的簽章憑證。第一次使用請加上 -NewCert 參數。"
}

# makeappx / signtool 來自 Microsoft.Windows.SDK.BuildTools NuGet 套件
if (-not (Test-Path "$tools\makeappx.exe")) {
    $v = (Invoke-RestMethod https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json).versions |
        Where-Object { $_ -notmatch '-' } | Select-Object -Last 1
    $zip = "$env:TEMP\sdk-buildtools.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$v/microsoft.windows.sdk.buildtools.$v.nupkg" -OutFile $zip
    Expand-Archive $zip "$env:TEMP\sdk-buildtools" -Force
    New-Item -ItemType Directory -Force $tools | Out-Null
    $bin = (Get-ChildItem "$env:TEMP\sdk-buildtools" -Recurse -Filter makeappx.exe |
        Where-Object FullName -match '\\x64\\' | Select-Object -First 1).DirectoryName
    Copy-Item "$bin\*" $tools -Recurse   # 連同 makeappx/signtool 依賴的 DLL 一起複製
}

dotnet publish "$src\PowerMeterBand.csproj" -c Release -o "$src\out"
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Recurse "$src\out\*" $stage
Get-ChildItem $stage -Filter *.pdb | Remove-Item
$manifest = "$stage\AppxManifest.xml"
$xml = [IO.File]::ReadAllText($manifest, $utf8) -replace '(<Identity [^>]*Version=")[^"]+', "`${1}$Version"
[IO.File]::WriteAllText($manifest, $xml, $utf8)

& "$tools\makeappx.exe" pack /o /d $stage /p "$dist\PowerMeterBand.msix"
if ($LASTEXITCODE) { throw 'makeappx failed' }
& "$tools\signtool.exe" sign /fd SHA256 /sha1 $cert.Thumbprint /s My "$dist\PowerMeterBand.msix"
if ($LASTEXITCODE) { throw 'signtool failed' }

Get-Process PowerMeterBand, Microsoft.CmdPal.UI -ErrorAction SilentlyContinue | Stop-Process -Force
try {
    Add-AppxPackage -Path "$dist\PowerMeterBand.msix" -ForceApplicationShutdown
}
finally {
    # 安裝失敗也要把命令選擇區開回來，不然 Dock 會消失
    Start-Process 'shell:AppsFolder\Microsoft.CommandPalette_8wekyb3d8bbwe!App'
}
Get-AppxPackage $packageName | Format-List Name, Version, InstallLocation
