#!/usr/bin/env pwsh
#Requires -Version 7.0
# ↑ 下面那个空行别删：注释帮助块紧贴着 shebang / #Requires 时 Get-Help 认不出来。

<#
.SYNOPSIS
把整个 OB# 项目构建一遍，摆成 OBS 认的插件目录，输出到 dist/。

.DESCRIPTION
CI（.github/workflows/build.yml）那条流水线，本地一把跑完：

  1. 原生库：cmake --preset <Rid>，再编 OBSharp.Interop.Native
  2. Interop 绑定生成器：会重写 src/OBSharp.Interop/Generated/（手改过生成代码就别跑，用
     -SkipGenerator）
  3. 托管侧：dotnet restore / build，再 publish Runtime、Loader（NativeAOT）、OBSharp.PluginManager
  4. 摆插件目录，写 data/plugins.ini

摆出来的目录跟发布形态一致，都是真实文件（不是 dev 脚本那种链接）：
Windows / Linux 是 <OutputDir>/OBSharp/{bin/64bit, bin/any, data}，macOS 是
<OutputDir>/OBSharp.plugin/Contents/{MacOS, any, Resources}。bin/64bit 是模块目录
（Loader 的 publish + 原生库），bin/any 是宿主（Runtime 的 publish），data 是模块数据目录
（locale、插件、plugins.ini）。

一份产物可以直接拷到别处，也可以整份挂进 OBS：

  Windows   %ProgramData%\obs-studio\plugins\OBSharp（目录链接不需要管理员）
  Linux     ~/.config/obs-studio/plugins/OBSharp
  macOS     ~/Library/Application Support/obs-studio/plugins/OBSharp.plugin

之后只改了托管侧代码：重跑本脚本、重启 OBS 就行，挂的还是同一份 dist。

.PARAMETER Rid
目标 RID（win-x64 / win-arm64 / linux-x64 / linux-arm64 / osx-x64 / osx-arm64），默认 win-x64。
交叉编译只能在本地编得出对应平台的原生库时才有意义（原生库是 CMake 按主机平台编的）。

.PARAMETER Configuration
构建配置，默认 Release。托管侧和原生库都用它（原生库落在
out/native/<arch>/<Configuration>）。

.PARAMETER OutputDir
产物摆到哪儿，默认 <仓库根>/dist。

.PARAMETER Framework
宿主（Runtime / Loader）的 TFM，默认 net11.0。插件管理器不跟这个走：它的 TFM 写在自己
csproj 里，脚本按 publish 目录名认。

.PARAMETER CMake
cmake 可执行文件路径。默认先找 PATH，找不到就问 vswhere（带 -prerelease，认 VS 2026
Insiders 这种预览版）。

.PARAMETER SkipNative
不编原生库，直接用 out/native/<arch>/<Configuration> 里现成的那份。

.PARAMETER SkipGenerator
不跑 Interop 绑定生成器，直接用 src/OBSharp.Interop/Generated/ 里现在这份。

.PARAMETER ResetPluginList
把 data/plugins.ini 重置成默认内容（只有插件管理器那一行）。不加的话，已经存在的那份原样
留着 —— 面板里启停过插件，那份状态不会因为重新生成就没。

.EXAMPLE
./scripts/Build-Dist.ps1

.EXAMPLE
./scripts/Build-Dist.ps1 -SkipNative -SkipGenerator
# 原生库和生成代码都没动，只重编托管侧再摆一遍

.EXAMPLE
./scripts/Build-Dist.ps1 -OutputDir D:\tmp\obsharp-dist
#>

[CmdletBinding()]
param (
  [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
  [string]
  $Rid = 'win-x64',

  [ValidateSet('Debug', 'Release')]
  [string]
  $Configuration = 'Release',

  [string]
  $OutputDir,

  [string]
  $Framework = 'net11.0',

  [string]
  $CMake,

  [switch]
  $SkipNative,

  [switch]
  $SkipGenerator,

  [switch]
  $ResetPluginList
)

$ErrorActionPreference = 'Stop'

# ---- 路径 -------------------------------------------------------------------

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'dist' }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

# 原生库的架构标签跟 CMake 那边用同一套（x64 / arm64）。
$NativeArch = $Rid -like '*-arm64' ? 'arm64' : 'x64'

# 平台决定模块叫什么、原生库叫什么、摆成什么形状。
$OsName = $Rid -replace '-.*$', ''
$LoaderName, $NativeName = switch ($OsName) {
  'win' { 'OBSharp.dll', 'OBSharp.Interop.Native.dll' }
  'linux' { 'OBSharp.so', 'libOBSharp.Interop.Native.so' }
  'osx' { 'OBSharp.dylib', 'libOBSharp.Interop.Native.dylib' }
  default { throw "不认识的 RID：$Rid" }
}

$ArtifactsPath = Join-Path $RepoRoot 'artifacts'
$ConfigName = $Configuration.ToLower()
$BuildDir = Join-Path $RepoRoot "out/build/$Rid"
$NativeDir = Join-Path $RepoRoot "out/native/$NativeArch/$Configuration"
$DataSrcDir = Join-Path $RepoRoot 'src/OBSharp.Runtime/Data'

# publish 目录用 --artifacts-path artifacts 那一套命名（跟 CI 一致）。
$RuntimePublish = Join-Path $ArtifactsPath "publish/OBSharp.Runtime/${ConfigName}_$Framework"
$LoaderPublish = Join-Path $ArtifactsPath "publish/OBSharp.Loader/${ConfigName}_${Framework}_$Rid"

# macOS 上模块是个 bundle（obs-cocoa.m 拼的是 "%module%.plugin/Contents/MacOS"）。
$BundleName = $OsName -eq 'osx' ? 'OBSharp.plugin' : 'OBSharp'
$BundleDir = Join-Path $OutputDir $BundleName

# ---- 小工具 -----------------------------------------------------------------

function Invoke-DotNet([string[]] $Arguments) {
  Write-Host "  dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
  & dotnet @Arguments
  if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) 失败（退出码 $LASTEXITCODE）" }
}

function Copy-Into([string] $From, [string] $To) {
  New-Item -Path $To -ItemType Directory -Force | Out-Null
  # 用 Get-ChildItem -Force 而不是 -Path "$From/*"：后者会漏掉隐藏文件。
  Get-ChildItem -LiteralPath $From -Force | Copy-Item -Destination $To -Recurse -Force
}

function Resolve-CMakeExe {
  # PATH 里有就直接用。
  $command = Get-Command cmake -ErrorAction SilentlyContinue
  if ($command) { return $command.Source }

  # VS 自带的那份：-prerelease 才认预览版（VS 2026 Insiders 那种）。
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
  if (Test-Path -LiteralPath $vswhere) {
    $found = & $vswhere -latest -prerelease -products * -find '**\CMake\bin\cmake.exe'
    if ($LASTEXITCODE -eq 0 -and $found) { return ($found | Select-Object -First 1) }
  }

  throw '找不到 cmake：PATH 里没有，vswhere 也没问出来。用 -CMake <路径> 指一个，或者用 -SkipNative 跳过原生库。'
}

# 下面的 dotnet / cmake 都按仓库根的相对路径写，所以先把工作目录挪过去（跑完再挪回来）。
Push-Location $RepoRoot
try {

  # ---- 1. 原生库 ------------------------------------------------------------

  if ($SkipNative) {
    Write-Host '① 原生库：跳过（-SkipNative）' -ForegroundColor Yellow
  }
  else {
    $cmakeExe = $CMake ? $CMake : $(Resolve-CMakeExe)

    Write-Host "① 原生库（$cmakeExe）" -ForegroundColor Cyan
    & $cmakeExe --preset $Rid
    if ($LASTEXITCODE -ne 0) { throw "cmake --preset $Rid 失败（退出码 $LASTEXITCODE）" }

    # 不用 --build --preset <rid>-<config>：Debug 在有些平台没有对应的构建预设，
    # 直接指 binaryDir + --config 更稳。
    & $cmakeExe --build $BuildDir --config $Configuration --target OBSharp.Interop.Native
    if ($LASTEXITCODE -ne 0) { throw "原生库编译失败（退出码 $LASTEXITCODE）" }
  }

  # ---- 2. Interop 绑定 ------------------------------------------------------

  if ($SkipGenerator) {
    Write-Host '② Interop 绑定：跳过（-SkipGenerator），用 src/OBSharp.Interop/Generated/ 里现在这份' -ForegroundColor Yellow
  }
  else {
    Write-Host '② Interop 绑定（会重写 src/OBSharp.Interop/Generated/）' -ForegroundColor Cyan
    Invoke-DotNet @('tool', 'restore')
    Invoke-DotNet @('build', 'src/OBSharp.Interop.Generator', '--graph', '--artifacts-path', 'artifacts', '-c', $Configuration, '-f', $Framework, '--no-restore')
    Invoke-DotNet @('run', '--project', 'src/OBSharp.Interop.Generator', '--artifacts-path', 'artifacts', '-c', $Configuration, '-f', $Framework, '--no-restore', '--no-build')
  }

  # ---- 3. 托管侧 ------------------------------------------------------------

  Write-Host '③ 托管侧' -ForegroundColor Cyan

  # 整个解决方案两套 TFM 都编：插件管理器只有 net10.0，跟着宿主那份 -f 走会报错。
  Invoke-DotNet @('restore', 'OBSharp.slnx', '--graph', '--artifacts-path', 'artifacts')
  # 插件管理器单独再还原一次：它是不是在解决方案里由 OBSharp.slnx 决定，这里不靠那个。
  Invoke-DotNet @('restore', 'src/OBSharp.PluginManager', '--graph', '--artifacts-path', 'artifacts')
  Invoke-DotNet @('build', 'OBSharp.slnx', '--graph', '--artifacts-path', 'artifacts', '-c', $Configuration, '--no-restore')

  Invoke-DotNet @('publish', 'src/OBSharp.Runtime', '--graph', '--artifacts-path', 'artifacts', '-c', $Configuration, '-f', $Framework, '--no-restore')
  Invoke-DotNet @('publish', 'src/OBSharp.PluginManager', '--graph', '--artifacts-path', 'artifacts', '-c', $Configuration, '--no-restore')

  # Loader 是 NativeAOT：先按 RID 还原，再发布。
  Invoke-DotNet @('restore', 'src/OBSharp.Loader', '--runtime', $Rid, '--graph', '--artifacts-path', 'artifacts')
  Invoke-DotNet @('publish', 'src/OBSharp.Loader', '--runtime', $Rid, '--graph', '--artifacts-path', 'artifacts', '-c', $Configuration, '-f', $Framework, '--no-restore')

  # 插件管理器只有单一 TFM（写在它 csproj 里），按 publish 目录名认，省得脚本里再抄一遍。
  $pluginPublish = @(Get-ChildItem -Path (Join-Path $ArtifactsPath "publish/OBSharp.PluginManager/${ConfigName}_*") -Directory -ErrorAction SilentlyContinue) |
    Sort-Object -Property LastWriteTime -Descending | Select-Object -First 1

  # ---- 4. 摆插件目录 --------------------------------------------------------

  Write-Host '④ 摆插件目录' -ForegroundColor Cyan

  $moduleOutDir = $OsName -eq 'osx' ? (Join-Path $BundleDir 'Contents/MacOS') : (Join-Path $BundleDir 'bin/64bit')
  $anyOutDir = $OsName -eq 'osx' ? (Join-Path $BundleDir 'Contents/any') : (Join-Path $BundleDir 'bin/any')
  $dataOutDir = $OsName -eq 'osx' ? (Join-Path $BundleDir 'Contents/Resources') : (Join-Path $BundleDir 'data')

  # 输入齐不齐先看一遍，省得摆到一半才炸。
  $inputs = [ordered]@{
    "Loader（$LoaderPublish）" = Join-Path $LoaderPublish $LoaderName
    "宿主（$RuntimePublish）" = Join-Path $RuntimePublish 'OBSharp.Runtime.dll'
    "宿主 runtimeconfig" = Join-Path $RuntimePublish 'OBSharp.Runtime.runtimeconfig.json'
    "原生库（$NativeDir）" = Join-Path $NativeDir $NativeName
    "模块数据目录（$DataSrcDir）" = $DataSrcDir
  }
  if (-not $pluginPublish) { throw "找不到插件管理器的 publish 目录：artifacts/publish/OBSharp.PluginManager/${ConfigName}_*" }
  $inputs["插件管理器（$($pluginPublish.FullName)）"] = Join-Path $pluginPublish.FullName 'OBSharp.PluginManager.dll'

  foreach ($key in $inputs.Keys) {
    if (-not (Test-Path -LiteralPath $inputs[$key])) { throw "缺 $key`n`n　$($inputs[$key])" }
  }

  # 旧的 plugins.ini 先读出来：里面是面板里启停过的状态。下面整份重建会把它冲掉。
  $listPath = Join-Path $dataOutDir 'plugins.ini'
  $oldList = (-not $ResetPluginList -and (Test-Path -LiteralPath $listPath)) ?
    [System.IO.File]::ReadAllText($listPath) : $null

  if (Test-Path -LiteralPath $BundleDir) { Remove-Item -LiteralPath $BundleDir -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $moduleOutDir, $anyOutDir, $dataOutDir | Out-Null

  # 模块目录：Loader 的 publish + 原生库。原生库不放这儿的话，AOT 出来的模块找不到
  # 「自己旁边」的那份（Loader 只引用 OBSharp.Interop，不含 UI 那部分）。
  Copy-Into $LoaderPublish $moduleOutDir
  Copy-Item -LiteralPath (Join-Path $NativeDir $NativeName) -Destination $moduleOutDir -Force

  # 宿主：Runtime 的 publish + 原生库（托管侧是按「程序集旁边」找它的）。
  Copy-Into $RuntimePublish $anyOutDir
  Copy-Item -LiteralPath (Join-Path $NativeDir $NativeName) -Destination $anyOutDir -Force

  # 数据目录：locale 这些，加上插件管理器插件本身。
  Copy-Into $DataSrcDir $dataOutDir
  Copy-Into $pluginPublish.FullName (Join-Path $dataOutDir 'OBSharp.PluginManager')

  if ($OsName -eq 'osx') {
    # NativeAOT 在 macOS 上出的是 OBSharp.dylib，而 OBS 那边模块扩展名是空的
    # （obs-cocoa.m 的 get_module_extension()），目录里的文件名得跟着去掉扩展名。
    Move-Item -LiteralPath (Join-Path $moduleOutDir $LoaderName) `
      -Destination (Join-Path $moduleOutDir 'OBSharp') -Force

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
      (Join-Path $BundleDir 'Contents/Info.plist'),
      $plist,
      [System.Text.UTF8Encoding]::new($false))
  }

  # 插件列表：插件管理器得先在这儿，否则没人能把它启起来（宿主读列表才加载插件）。
  # 注释行会被宿主的写回覆盖，这里不写中文，免得编码惹麻烦。
  if ($oldList) {
    [System.IO.File]::WriteAllText($listPath, $oldList, [System.Text.UTF8Encoding]::new($true))
    Write-Host '  plugins.ini：保留原有那份（要重置用 -ResetPluginList）' -ForegroundColor DarkGray
  }
  else {
    [System.IO.File]::WriteAllLines(
      $listPath,
      @(
        '; OB# plugin list: <assembly path> = <true|false>'
        'OBSharp.PluginManager/OBSharp.PluginManager.dll = true'
      ),
      [System.Text.UTF8Encoding]::new($true))
  }

  # ---- 5. 收尾 --------------------------------------------------------------

  $files = Get-ChildItem -LiteralPath $BundleDir -Recurse -Force -File
  $total = ($files | Measure-Object -Property Length -Sum).Sum

  Write-Host "`n好了。" -ForegroundColor Green
  Write-Host "  插件目录：$BundleDir"
  Write-Host "  模块：    $moduleOutDir\$LoaderName"
  Write-Host "  宿主：    $anyOutDir\OBSharp.Runtime.dll"
  Write-Host "  数据：    $dataOutDir（含 plugins.ini 与 OBSharp.PluginManager/）"
  Write-Host ('  文件 {0} 个，共 {1:N2} MB' -f $files.Count, ($total / 1MB))

  Write-Host @"

挂进 OBS（目录链接，不需要管理员）：
  Windows   New-Item -ItemType Junction -Path "`$env:ProgramData\obs-studio\plugins\$BundleName" -Target "$BundleDir"
  Linux     ln -s "$BundleDir" "`$HOME/.config/obs-studio/plugins/$BundleName"
  macOS     ln -s "$BundleDir" "`$HOME/Library/Application Support/obs-studio/plugins/$BundleName"

之后改代码：重跑本脚本，重启 OBS。
"@ -ForegroundColor Cyan
}
finally {
  Pop-Location
}
