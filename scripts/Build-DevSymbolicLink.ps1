#!/usr/bin/env pwsh
#Requires -Version 7.0
# ↑ 下面那个空行别删：注释帮助块紧贴着 shebang / #Requires 时 Get-Help 认不出来。

<#
.SYNOPSIS
摆好插件目录，再用一个目录链接把它挂进 OBS 的插件搜索路径（Windows 用 Junction，
Linux / macOS 用符号链接）。

.DESCRIPTION
开发循环用的脚本，做两件事。

一、在 out/plugin/<Rid> 里摆出 OBS 认的插件目录，跟发布形态一致：

    Windows / Linux   OBSharp/bin/64bit/…   OBSharp/bin/any/…   OBSharp/data/…
    macOS             OBSharp.plugin/Contents/{MacOS,any,Resources}/…

  其中模块目录（bin/64bit 或 Contents/MacOS）是真实文件：OBSharp.Loader 的 publish
  产出，外加一份原生库 —— 原生库 Loader 那边不带（它不引用 OBSharp.Interop.Browser），
  得补进去，AOT 出来的模块找不到「自己旁边」的原生库。

  bin/any 和 data 是目录链接，分别指向 Runtime 的 publish 目录和源码里的 Data 目录：
  只改这两边的代码 / 数据，重启 OBS 就能看到，不用重摆。

二、在 OBS 的插件目录里放一个目录链接指过来。这样不用往 ProgramData / 安装目录里
  拷文件，也就不需要管理员。

链接用哪种：

    Windows       Junction。创建不需要管理员，也不需要开发者模式（符号链接两样都要，
                  所以这里不用它）—— 而且 Junction 只能链目录，正好够用。
    Linux / macOS  符号链接。这两个平台上不需要特权。

卸载用 -Unlink：只删 OBS 那边的链接，out/plugin/<Rid> 留着。

OBS 从哪儿找插件（frontend/widgets/OBSBasic.cpp 的 AddExtraModulePaths；目录名跟
bin/64bit 一样是 OBS 写死的，arm64 也叫 64bit）：

    Windows   %ProgramData%\obs-studio\plugins\OBSharp
    Linux     $XDG_CONFIG_HOME/obs-studio/plugins/OBSharp（默认 ~/.config）
    macOS     ~/Library/Application Support/obs-studio/plugins/OBSharp.plugin

.PARAMETER Rid
目标 RID（win-x64 / win-arm64 / linux-x64 / linux-arm64 / osx-x64 / osx-arm64），
默认按当前系统和进程架构推。

.PARAMETER Configuration
原生库的配置目录名（out/native/<arch>/<Configuration>），默认 Release。

.PARAMETER PluginDir
OBS 的插件目录。默认按上面那张表取；本地试验时可以指到临时目录。

.PARAMETER OutputDir
插件目录摆到哪儿，默认 out/plugin/<Rid>。

.PARAMETER LoaderDir
OBSharp.Loader 的 publish 目录，默认 artifacts/publish/OBSharp.Loader/release_net10.0_<Rid>。

.PARAMETER RuntimeDir
OBSharp.Runtime 的 publish 目录，默认 artifacts/publish/OBSharp.Runtime/release_net10.0
（Runtime 是框架依赖的，不带 -r 发布，所以目录名里没有 RID）。

.PARAMETER NativeDir
原生库所在目录，默认 out/native/<arch>/<Configuration>。

.PARAMETER DataDir
模块数据目录，默认 src/OBSharp.Runtime/Data。

.PARAMETER Legacy
（仅 Windows）改装进 OBS 安装目录的那一套：obs-plugins\{64bit,any} + data\obs-plugins\OBSharp。
要写 Program Files，得用管理员跑；目录用 Junction，两个模块文件用符号链接。

.PARAMETER Unlink
只删链接然后退出（不摆目录、不链接）。

.EXAMPLE
./scripts/Build-DevSymbolicLink.ps1

.EXAMPLE
./scripts/Build-DevSymbolicLink.ps1 -PluginDir D:\tmp\obs-plugins
# 挂到临时目录里试，不动真的 OBS 配置

.EXAMPLE
./scripts/Build-DevSymbolicLink.ps1 -Unlink
#>

[CmdletBinding()]
param (
  [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
  [string]
  $Rid,

  [ValidateSet('Debug', 'Release')]
  [string]
  $Configuration = 'Release',

  [string]
  $PluginDir,

  [string]
  $OutputDir,

  [string]
  $LoaderDir,

  [string]
  $RuntimeDir,

  [string]
  $NativeDir,

  [string]
  $DataDir,

  [switch]
  $Legacy,

  [switch]
  $Unlink
)

$ErrorActionPreference = 'Stop'

# ---- 链接的创建与删除 -------------------------------------------------------

function Remove-Link([string] $Path) {
  $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
  if (-not $item) { return }

  # 先确认「它确实是个链接」：不是就报错，别把别人装在那儿的真东西删了。
  if (-not ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
    throw "$Path 已经存在，而且不是链接（真实的目录 / 文件），不敢动它。确认之后自己处理。"
  }

  # pwsh 7 的 Remove-Item 碰到重解析点只删链接本身。老版本（5.1）会顺着链接把目标
  # 目录里的东西一起删掉，所以这里非 7 不可（见文件头的 #Requires）。
  Remove-Item -LiteralPath $Path -Recurse -Force

  if (Test-Path -LiteralPath $Path) { throw "删不掉：$Path" }
  Write-Host "  删掉链接 $Path" -ForegroundColor DarkGray
}

function New-DirLink([string] $Path, [string] $Target) {
  if (-not (Test-Path -LiteralPath $Target -PathType Container)) { throw "链接目标不存在：$Target" }

  Remove-Link $Path

  # Windows 上 Junction：创建不需要特权。符号链接留给 Linux / macOS。
  $type = $IsWindows ? 'Junction' : 'SymbolicLink'
  New-Item -ItemType $type -Path $Path -Target $Target | Out-Null

  Write-Host "  $Path" -ForegroundColor Green
  Write-Host "    -> $Target  （$type 型目录链接）" -ForegroundColor DarkGray
}

# ---- 平台、RID 与默认路径 ---------------------------------------------------

if (-not $Rid) {
  $os = $IsWindows ? 'win' : $IsLinux ? 'linux' : 'osx'
  $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    default { throw "认不出这个架构（$_），请显式给 -Rid。" }
  }
  $Rid = "$os-$arch"
}

# 原生库的目录名是 CMake 那边的架构标签，跟 OBSharp.Interop.Browser.csproj 里的
# OBSharpNativeArch 是同一套（x64 / arm64）。
$NativeArch = $Rid -like '*-arm64' ? 'arm64' : 'x64'

# 平台决定两件事：OBS 加载的模块叫什么、原生库叫什么。
$OsName = $Rid -replace '-.*$', ''
$LoaderName, $NativeName = switch ($OsName) {
  'win' { 'OBSharp.dll', 'OBSharp.Interop.Browser.Native.dll' }
  'linux' { 'OBSharp.so', 'libOBSharp.Interop.Browser.Native.so' }
  'osx' { 'OBSharp.dylib', 'libOBSharp.Interop.Browser.Native.dylib' }
  default { throw "不认识的 RID：$Rid" }
}

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path -Path $RepoRoot -ChildPath "out/plugin/$Rid" }
if (-not $LoaderDir) {
  $LoaderDir = Join-Path -Path $RepoRoot -ChildPath "artifacts/publish/OBSharp.Loader/release_net10.0_$Rid"
}
if (-not $RuntimeDir) {
  $RuntimeDir = Join-Path -Path $RepoRoot -ChildPath 'artifacts/publish/OBSharp.Runtime/release_net10.0'
}
if (-not $NativeDir) { $NativeDir = Join-Path -Path $RepoRoot -ChildPath "out/native/$NativeArch/$Configuration" }
if (-not $DataDir) { $DataDir = Join-Path -Path $RepoRoot -ChildPath 'src/OBSharp.Runtime/Data' }

# macOS 上是 <模块名>.plugin 这种 bundle（obs-cocoa.m 拼的是 "%module%.plugin/Contents/MacOS"）。
$BundleName = $OsName -eq 'osx' ? 'OBSharp.plugin' : 'OBSharp'
$BundleDir = Join-Path -Path $OutputDir -ChildPath $BundleName

if (-not $PluginDir) {
  if ($IsWindows) {
    $PluginDir = Join-Path -Path $env:ProgramData -ChildPath 'obs-studio/plugins'
  }
  elseif ($IsLinux) {
    # OBS 走 XDG（GetAppConfigPath），XDG_CONFIG_HOME 设了就跟它走。
    # 三目的分支只能是表达式，Join-Path 这种命令得用 $( ) 包起来。
    $configHome = $env:XDG_CONFIG_HOME ? $env:XDG_CONFIG_HOME : $(Join-Path -Path $env:HOME -ChildPath '.config')
    $PluginDir = Join-Path -Path $configHome -ChildPath 'obs-studio/plugins'
  }
  else {
    $PluginDir = Join-Path -Path $env:HOME -ChildPath 'Library/Application Support/obs-studio/plugins'
  }
}

Write-Host "RID：$Rid    插件目录：$PluginDir" -ForegroundColor Cyan

# -Legacy 是 Windows 专用，而且要往 Program Files 里写：早点判掉，别等摆完插件目录才报。
if ($Legacy) {
  if (-not $IsWindows) { throw '-Legacy 只有 Windows 有（那是 OBS 安装目录里那一套布局）。' }
  if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
      ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '-Legacy 要往 OBS 安装目录里写（Program Files），得用管理员权限跑。'
  }
  $obsRoot = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\OBS Studio\' -Name '(default)'
}

# ---- -Unlink：只拆链接 ------------------------------------------------------

if ($Unlink) {
  Write-Host '拆链接：' -ForegroundColor Cyan

  if ($Legacy) {
    $modDir = Join-Path -Path $obsRoot -ChildPath 'obs-plugins/64bit'
    foreach ($name in @('OBSharp.dll', $NativeName)) { Remove-Link (Join-Path -Path $modDir -ChildPath $name) }
    Remove-Link (Join-Path -Path $obsRoot -ChildPath 'obs-plugins/any')
    Remove-Link (Join-Path -Path $obsRoot -ChildPath 'data/obs-plugins/OBSharp')
  }
  else {
    Remove-Link (Join-Path -Path $PluginDir -ChildPath $BundleName)
  }

  Write-Host '完成。' -ForegroundColor Green
  return
}

# ---- 1. 摆插件目录 ----------------------------------------------------------

$LoaderFile = Join-Path -Path $LoaderDir -ChildPath $LoaderName
$NativeFile = Join-Path -Path $NativeDir -ChildPath $NativeName
$RuntimeFile = Join-Path -Path $RuntimeDir -ChildPath 'OBSharp.Runtime.dll'

# 先看输入齐不齐，省得摆到一半才炸。
foreach ($pair in @(
    @($LoaderFile, "在 $LoaderDir 里找不到 $LoaderName（先 dotnet publish src/OBSharp.Loader -c Release -r $Rid）"),
    @($NativeFile, "在 $NativeDir 里找不到 $NativeName（先 cmake --build --preset $Rid-release）"),
    @($RuntimeFile, "在 $RuntimeDir 里找不到 OBSharp.Runtime.dll（先 dotnet publish src/OBSharp.Runtime）"),
    @($DataDir, "找不到模块数据目录：$DataDir"))) {
  if (-not (Test-Path -LiteralPath $pair[0])) { throw "$($pair[1])`n`n　$($pair[0])" }
}

function Copy-Into([string] $From, [string] $To) {
  New-Item -Path $To -ItemType Directory -Force | Out-Null
  # 用 Get-ChildItem -Force 而不是 -Path "$From/*"：后者会漏掉隐藏文件。
  Get-ChildItem -LiteralPath $From -Force | Copy-Item -Destination $To -Recurse -Force
}

Write-Host "摆插件目录：$BundleDir" -ForegroundColor Cyan
# 上一轮的产物是真实目录，直接清掉重摆；链接（any / data）也一起清，反正下面会重建。
if (Test-Path -LiteralPath $BundleDir) { Remove-Item -LiteralPath $BundleDir -Recurse -Force }

if ($OsName -eq 'osx') {
  $moduleDir = Join-Path -Path $BundleDir -ChildPath 'Contents/MacOS'
  $anyDir = Join-Path -Path $BundleDir -ChildPath 'Contents/any'
  $dataOutDir = Join-Path -Path $BundleDir -ChildPath 'Contents/Resources'

  Copy-Into $LoaderDir $moduleDir
  # NativeAOT 在 macOS 上出的是 OBSharp.dylib，而 OBS 那边模块扩展名是空的
  # （obs-cocoa.m 的 get_module_extension()），目录里的文件名得跟着去掉扩展名。
  Move-Item -LiteralPath (Join-Path -Path $moduleDir -ChildPath $LoaderName) `
    -Destination (Join-Path -Path $moduleDir -ChildPath 'OBSharp') -Force
  Copy-Item -LiteralPath $NativeFile -Destination $moduleDir -Force

  New-DirLink -Path $anyDir -Target $RuntimeDir
  New-DirLink -Path $dataOutDir -Target $DataDir

  $plist = @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleExecutable</key>
  <string>OBSharp</string>
  <key>CFBundleIdentifier</key>
  <string>com.obsharp.loader</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>OBSharp</string>
  <key>CFBundlePackageType</key>
  <string>BNDL</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0.0</string>
  <key>CFBundleVersion</key>
  <string>1</string>
</dict>
</plist>
'@
  # 不用 Set-Content -Encoding utf8：Windows PowerShell 5.1 会带 BOM，plist 就脏了。
  [System.IO.File]::WriteAllText(
    (Join-Path -Path $BundleDir -ChildPath 'Contents/Info.plist'),
    $plist,
    [System.Text.UTF8Encoding]::new($false))
}
else {
  # 目录名是 OBS 写死的 64bit，不是按架构来的。
  $moduleDir = Join-Path -Path $BundleDir -ChildPath 'bin/64bit'
  $anyDir = Join-Path -Path $BundleDir -ChildPath 'bin/any'
  $dataOutDir = Join-Path -Path $BundleDir -ChildPath 'data'

  Copy-Into $LoaderDir $moduleDir
  Copy-Item -LiteralPath $NativeFile -Destination $moduleDir -Force

  New-DirLink -Path $anyDir -Target $RuntimeDir
  New-DirLink -Path $dataOutDir -Target $DataDir
}

# 摆完再看一眼：托管侧和原生库都得在，缺了说明前面某步的产出不对。
foreach ($required in @(
    (Join-Path -Path $anyDir -ChildPath 'OBSharp.Runtime.dll'),
    (Join-Path -Path $anyDir -ChildPath $NativeName))) {
  if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "摆完之后还是缺：$required" }
}

# ---- 2. 挂链接 --------------------------------------------------------------

Write-Host '挂链接：' -ForegroundColor Cyan

if ($Legacy) {
  # 权限和 $obsRoot 在上面判过了。
  Write-Host "OBS 安装目录：$obsRoot" -ForegroundColor DarkGray

  # 模块文件得链单个文件：obs-plugins\64bit 是跟别的插件共用的目录，不能整目录链。
  $modDir = Join-Path -Path $obsRoot -ChildPath 'obs-plugins/64bit'
  foreach ($name in @('OBSharp.dll', $NativeName)) {
    $src = Join-Path -Path $moduleDir -ChildPath $name
    $dst = Join-Path -Path $modDir -ChildPath $name
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw "缺文件：$src" }
    Remove-Link $dst
    New-Item -ItemType SymbolicLink -Path $dst -Target $src | Out-Null
    Write-Host "  $dst" -ForegroundColor Green
    Write-Host "    -> $src  （文件符号链接）" -ForegroundColor DarkGray
  }

  New-DirLink -Path (Join-Path -Path $obsRoot -ChildPath 'obs-plugins/any') -Target $anyDir
  New-DirLink -Path (Join-Path -Path $obsRoot -ChildPath 'data/obs-plugins/OBSharp') -Target $dataOutDir
}
else {
  New-DirLink -Path (Join-Path -Path $PluginDir -ChildPath $BundleName) -Target $BundleDir
}

Write-Host @"

好了。之后改完代码：重启 OBS。
  * 只改了托管侧 / data：不用重跑（bin/any、data 是指向产物的链接）。
  * 改了 Loader 或原生库：重新 publish / 编一次，再跑一次这个脚本（模块目录是真文件）。

拆掉：./scripts/Build-DevSymbolicLink.ps1 -Unlink
"@ -ForegroundColor Cyan
