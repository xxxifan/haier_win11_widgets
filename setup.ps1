# 编译并把海尔智家小组件注册到 Windows 11 小组件面板（需要开启“开发人员模式”）。
# 用法：
#   .\setup.ps1             编译 + 注册（已注册则先卸载再注册）
#   .\setup.ps1 -SkipBuild  跳过编译，只重新注册
#   .\setup.ps1 -Uninstall  卸载
#   .\setup.ps1 -Uninstall -Purge   卸载并清除登录态与缓存（不加 -Purge 会保留，重装后仍是登录状态）
param(
    [switch]$Uninstall,
    [switch]$SkipBuild,
    [switch]$Purge
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "HaierWidget\HaierWidget.csproj"
$publish = Join-Path $root "publish"
$packageName = "Xifan.HaierWidget"

function Remove-Existing {
    $pkg = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
    if ($pkg) {
        Write-Host "卸载已注册的包 $($pkg.PackageFullName) ..."
        if ($Purge) {
            # 不加 -PreserveApplicationData，Windows 会连同 LocalState（session.json、models、widget.log）一起删掉
            Write-Host "  同时清除应用数据（登录态、设备缓存、日志）"
            Remove-AppxPackage -Package $pkg.PackageFullName
        }
        else {
            # 默认保留 LocalState，重装后不用重新登录
            Remove-AppxPackage -Package $pkg.PackageFullName -PreserveApplicationData
        }
    }
}

if ($Uninstall) {
    Remove-Existing
    Write-Host "已卸载。"
    exit 0
}

$devMode = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devMode -ne 1) {
    Write-Warning "未开启开发人员模式：设置 > 系统 > 开发者选项 > 开发人员模式。未签名的松散包只能在开发人员模式下注册。"
}

$runtime = Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.1.8" -ErrorAction SilentlyContinue
if (-not $runtime) {
    Write-Warning "未安装 Windows App Runtime 1.8，请先安装：https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe"
}

if (-not $SkipBuild) {
    Write-Host "编译并发布 ..."
    # 面板可能已经拉起了 publish 里的 HaierWidget.exe（COM 服务），先结束，否则目录删不掉
    Get-Process -Name HaierWidget -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false
    Start-Sleep -Milliseconds 500
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish $proj -c Release -r win-x64 --self-contained false -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }
}

Copy-Item (Join-Path $root "HaierWidget\AppxManifest.xml") $publish -Force
New-Item -ItemType Directory -Force (Join-Path $publish "Assets") | Out-Null
Copy-Item (Join-Path $root "HaierWidget\Assets\*") (Join-Path $publish "Assets") -Force
New-Item -ItemType Directory -Force (Join-Path $publish "Public") | Out-Null

Remove-Existing
Write-Host "注册包 ..."
Add-AppxPackage -Register (Join-Path $publish "AppxManifest.xml")

# 小组件面板宿主只在启动时枚举提供程序，注册后必须重启它们，否则 + 列表里看不到新小组件。
$hosts = Get-Process | Where-Object { $_.ProcessName -match "^(Widgets|WidgetService|WidgetBoard)$" }
if ($hosts) {
    Write-Host "重启小组件面板宿主进程（$($hosts.ProcessName -join ', ')）..."
    $hosts | Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
}

$pkg = Get-AppxPackage -Name $packageName
Write-Host ""
Write-Host "已注册：$($pkg.PackageFullName)"
Write-Host "1. 开始菜单打开“海尔智家小组件”登录账号（或运行 start haierwidget://login）"
Write-Host "2. Win+W 打开小组件面板 > 右上角 + > 在“海尔智家”下找到“海尔智家设备”> 固定（看不到就再按一次 Win+W）"
Write-Host "3. 卡片会列出全部设备，点某台展开控制；日志在 $env:LOCALAPPDATA\Packages\$($pkg.PackageFamilyName)\LocalState\widget.log"
