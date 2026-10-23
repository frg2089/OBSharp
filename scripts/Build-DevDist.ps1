#!/usr/bin/env pwsh
#Requires -Version 7.0
# ↑ 下面那个空行别删：注释帮助块紧贴着 shebang / #Requires 时 Get-Help 认不出来。

<#
.SYNOPSIS
按 .github/workflows/build.yml 的流程，在本地把 OB# 构建并摆成 OBS 认的插件目录。

.DESCRIPTION
这个脚本是 CI 的本地镜像，**步骤顺序与 build.yml 的 job 一一对应**：

  ① dotnet  job：生成版本文件 → 还原工具 → 还原 → 生成绑定（生成器随用随编）
                → 编译托管侧 → 发布 Runtime / PluginManager
  ② native  job：cmake --preset <rid> → cmake --build --preset <rid>-release
                → 还原/编译/发布 Loader（NativeAOT）
  ③ runtime job：（CI 上是把 6 个 RID 的原生库拼进 runtimes/，本脚本只做当前 RID）
  ④ bundle  job：摆成 OBSharp/{bin/64bit, bin/any, data}（macOS 是
                OBSharp.plugin/Contents/{MacOS, any, Resources}），写 plugins.ini，
                macOS 额外写 Info.plist

跟 CI 的差别只有两处，都是为了本地好用：

  * 不下载/上传 artifact，直接用当前 worktree 的产物；
  * 默认只处理一个 RID（CI 的矩阵是 6 个跑在 6 台机器上）。

绑定生成（第④步）是本脚本的内部函数，也是本地与 CI 共用的唯一一份生成逻辑：
build.yml 的 dotnet job 用 -GeneratorOnly 只跑这一步，命令不存在第二份拷贝。

产物是**真实文件**，可以直接拷走或整份挂进 OBS：

  Windows   %ProgramData%\obs-studio\plugins\OBSharp
  Linux     ~/.config/obs-studio/plugins/OBSharp
  macOS     ~/Library/Application Support/obs-studio/plugins/OBSharp.plugin

之后只改了托管侧代码：重跑本脚本、重启 OBS 即可，挂的还是同一份 dist。

.PARAMETER Rid
目标 RID，默认按当前机器推断（Windows→win-<arch>，Linux→linux-<arch>，
macOS→osx-<arch>）。交叉编译只有在本地能编出对应平台原生库时才有意义。

.PARAMETER Configuration
构建配置，默认 Release。托管侧和原生库都用它（原生库落在
out/native/<arch>/<Configuration>）。

.PARAMETER OutputDir
产物放哪儿，默认 <仓库根>/dist。

.PARAMETER Framework
宿主（Runtime / Loader）的 TFM，默认 net11.0。插件管理器不跟这个走：它的 TFM
写在自己 csproj 里，脚本按 publish 目录名认。

.PARAMETER CMake
cmake 可执行文件路径。默认先找 PATH，再找 VS 自带的（Windows），最后找常见
系统路径。

.PARAMETER SkipNative
不编原生库，直接用 out/native/<arch>/<Configuration> 里现成的那份。

.PARAMETER SkipGenerator
不跑 Interop 绑定生成器，直接用 src/OBSharp.Interop/Generated/ 里现在这份。

.PARAMETER GeneratorOnly
只跑第④步的 Interop 绑定生成（写 src/OBSharp.Interop/Generated/{native,libobs}）
就结束，其余步骤全跳过。CI 的 dotnet job 用它；与 -SkipGenerator 互斥。

.PARAMETER MsvcInclude
MSVC STL 的 include 目录。绑定生成用它钉 MSVC 14.44（libclang 21 会被 MSVC
14.52 的 yvals_core.h 以 STL1000 拒绝，见 src/OBSharp.Interop.Generator/README.md）。
默认自动在各 VS 安装里找 14.44.* 工具集；机器上没装 14.44 时用它手动指定。

.PARAMETER SkipVersion
不生成 Directory.Version.props / Directory.Version.cmake，用仓库里现有的。
（CI 上这两份是 GitVersion 产出的；本地缺它们时脚本会自动写占位版本。）

.PARAMETER ResetPluginList
把 data/plugins.ini 重置成默认内容（只有插件管理器那一行）。不加的话，已经
存在的那份原样保留 —— 面板里启停过插件，那份状态不会因为重新生成就没。

.EXAMPLE
./scripts/Build-DevDist.ps1

.EXAMPLE
./scripts/Build-DevDist.ps1 -Configuration Debug -Rid win-x64

.EXAMPLE
./scripts/Build-DevDist.ps1 -SkipNative -SkipGenerator
# 原生库和生成代码都没动，只重编托管侧再摆一遍

.EXAMPLE
./scripts/Build-DevDist.ps1 -GeneratorOnly
# 只重新生成绑定（CI 的 dotnet job 就是这个形态）

.EXAMPLE
./scripts/Build-DevDist.ps1 -OutputDir /tmp/obsharp-dist
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
  $GeneratorOnly,

  [string]
  $MsvcInclude,

  [switch]
  $SkipVersion,

  [switch]
  $ResetPluginList
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---- 环境判定（跨平台：Windows / Linux / macOS 都能跑） ----------------------

function Get-HostOsName {
  if ($IsWindows) { return 'win' }
  if ($IsLinux) { return 'linux' }
  if ($IsMacOS) { return 'osx' }
  throw "不支持的操作系统：$([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)"
}

function Get-HostArch {
  $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
  switch ($arch) {
    'X64' { return 'x64' }
    'Arm64' { return 'arm64' }
    'X86' { return 'x86' }
    default { throw "不支持的 CPU 架构：$arch" }
  }
}

$HostOs = Get-HostOsName
if (-not $Rid) { $Rid = "$HostOs-$(Get-HostArch)" }

# 脚本只能为「本机能编出来的原生库」打包。交叉编译托管侧可以，但原生库不行
# （CMake 用的是本机工具链），所以早失败比编到一半失败好。
$RidOs = $Rid -replace '-.*$', ''
if ($RidOs -ne $HostOs -and -not $SkipNative -and -not $GeneratorOnly) {
  throw "RID $Rid 属于 $RidOs，而当前机器是 $HostOs。跨平台的原生库要在对应平台上编（或用 -SkipNative 复用已有产物）。"
}

if ($GeneratorOnly -and $SkipGenerator) {
  throw '-GeneratorOnly 与 -SkipGenerator 互斥：一个只跑绑定生成，一个跳过绑定生成。'
}

# ---- 路径 -------------------------------------------------------------------

$RepoRoot = Split-Path -Path $PSScriptRoot -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'dist' }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

$NativeArch = $Rid -like '*-arm64' ? 'arm64' : 'x64'
$OsName = $Rid -replace '-.*$', ''

# 平台决定模块叫什么、原生库叫什么、摆成什么形状。
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

function Write-Step([string] $Text) {
  Write-Host ""
  Write-Host "=== $Text " -ForegroundColor Cyan -NoNewline
  Write-Host ('=' * [Math]::Max(0, 60 - $Text.Length)) -ForegroundColor Cyan
}

function Write-Detail([string] $Text) {
  Write-Host "    $Text" -ForegroundColor DarkGray
}

function Invoke-DotNet([string[]] $Arguments) {
  Write-Detail "dotnet $($Arguments -join ' ')"
  # stdio 交给控制台（不从管道里读）：在沙箱 / 受限环境里捕获子进程输出会失败。
  & dotnet @Arguments
  if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) 失败（退出码 $LASTEXITCODE）" }
}

function Copy-Into([string] $From, [string] $To) {
  New-Item -Path $To -ItemType Directory -Force | Out-Null
  # 用 Get-ChildItem -Force 而不是 -Path "$From/*"：后者会漏掉隐藏文件。
  Get-ChildItem -LiteralPath $From -Force | Copy-Item -Destination $To -Recurse -Force
}

function Resolve-CMakeExe {
  if ($CMake) {
    if (-not (Test-Path -LiteralPath $CMake)) { throw "-CMake 指的文件不存在：$CMake" }
    return $CMake
  }

  # PATH 里有就直接用。
  $command = Get-Command cmake -ErrorAction SilentlyContinue
  if ($command) { return $command.Source }

  # Windows：VS 自带的那份。-prerelease 才认预览版（VS 2026 Insiders 那种）。
  if ($IsWindows) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
      $found = & $vswhere -latest -prerelease -products * -find '**\CMake\bin\cmake.exe'
      if ($LASTEXITCODE -eq 0 -and $found) { return ($found | Select-Object -First 1) }
    }
  }

  # macOS / Linux：常见的安装位置兜一下（Homebrew / /usr/local / Xcode）。
  foreach ($candidate in @(
      '/opt/homebrew/bin/cmake',
      '/usr/local/bin/cmake',
      '/usr/bin/cmake',
      '/Applications/CMake.app/Contents/bin/cmake')) {
    if (Test-Path -LiteralPath $candidate) { return $candidate }
  }

  throw '找不到 cmake：PATH 里没有，常见安装位置也没有。用 -CMake <路径> 指一个，或者用 -SkipNative 跳过原生库。'
}

function Resolve-MsvcStlInclude {
  # libclang 21 会被 MSVC 14.52 的 yvals_core.h（STL1000）拒绝，钉 14.44 绕开。
  if ($MsvcInclude) {
    if (-not (Test-Path -LiteralPath $MsvcInclude)) { throw "-MsvcInclude 指的目录不存在：$MsvcInclude" }
    return $MsvcInclude
  }

  $msvcRoots = [System.Collections.Generic.List[string]]::new()

  # vswhere 按注册表找 VS，含预发布版（VS 2026 Insiders）和装在非系统盘的那些。
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
  if (Test-Path -LiteralPath $vswhere) {
    foreach ($install in & $vswhere -products * -all -prerelease -property installationPath) {
      if ($install) { $msvcRoots.Add((Join-Path $install 'VC/Tools/MSVC')) }
    }
  }

  # vswhere 不在时兜一下常见安装根（<根>\Microsoft Visual Studio\<大版本>\<发行版>\VC\...）。
  foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    if (-not $base) { continue }
    $vsRoot = Join-Path $base 'Microsoft Visual Studio'
    if (-not (Test-Path -LiteralPath $vsRoot)) { continue }
    foreach ($major in Get-ChildItem -Path $vsRoot -Directory -ErrorAction SilentlyContinue) {
      foreach ($edition in Get-ChildItem -Path $major.FullName -Directory -ErrorAction SilentlyContinue) {
        $msvcRoots.Add((Join-Path $edition.FullName 'VC/Tools/MSVC'))
      }
    }
  }

  foreach ($root in $msvcRoots) {
    $toolset = Get-ChildItem -Path $root -Directory -Filter '14.44.*' -ErrorAction SilentlyContinue |
      Sort-Object -Property Name -Descending | Select-Object -First 1
    if ($toolset) {
      $include = Join-Path $toolset.FullName 'include'
      if (Test-Path -LiteralPath $include) { return $include }
    }
  }

  throw @'
找不到 MSVC 14.44 工具集的 include 目录。libclang 21 会被新版 MSVC STL 拒绝
（yvals_core.h 的 STL1000），必须钉 14.44：在 Visual Studio Installer 里装
「MSVC v143 - C++ x64/x86 生成工具 (v14.44)」组件，或用 -MsvcInclude <目录> 手动指定。
'@
}

# ---- Interop 绑定生成（本地与 CI 共用的唯一一份生成逻辑） --------------------
#
# 产物两套（--namespace 都是 OBSharp.Interop，生成器随用随编，dotnet run）：
#   src/OBSharp.Interop/Generated/native   --lib OBSharp.Interop.Native（+ --traverse obsharp_）
#   src/OBSharp.Interop/Generated/libobs   --lib obs --lib-prefix obs_frontend_=obs-frontend-api
# Generated/ 在 .gitignore 里，不入库，每次构建前重新生成（-SkipGenerator 跳过）。
# build.yml 的 dotnet job 用本脚本的 -GeneratorOnly 跑的就是这里，不存在第二份命令。
#
# 输入头文件是显式列表（生成器的 --input 语义上支持通配符，但解析环节不展开，这里一律
# 传具体文件）：
#   * native：src/OBSharp.Interop.Native/src 下的 C ABI 导出面 —— obsharp.h、host.h、
#     logger.h、QT 的五个、CEF 的两个。QT/qt.h 是 QString/std::string 的 C++ inline
#     辅助、CEF/widgetsignals.h 是 Q_OBJECT 信号适配器，都没有 OBSHARP_API 导出，刻意
#     不进输入（它们仍通过 --include src/OBSharp.Interop.Native/src 参与解析）；再加
#     --traverse obsharp_ 双保险。logger.h 里的 LogCore / Log 是 namespace OBSharp 里的
#     普通 C++ 函数（没有 OBSHARP_API/OBSHARP_CALL，Log 还是变参），不在 DLL 导出面，
#     被滤掉是正确行为 —— 哪天真要从 C# 调它，正确做法是在 native 侧加 obsharp_ 前缀的
#     C ABI 包装再重新生成，而不是放宽 --traverse。
#   * libobs：libobs 的 obs*.h（去掉 internal / nix / ffmpeg-compat / obsversion 这些
#     非导出头）+ util / graphics / media-io / callback 四个目录的全部头 +
#     obs-frontend-api + w32-pthreads 三件套（pthread / semaphore / sched）。此目标
#     不带 --traverse，靠 --input 全量收敛。
#
# 解析需要 include：Qt6 / CEF / obs-browser panel / libobs（native 目标里
# browser-panel.hpp 的解析闭包）+ Native 自己的 src。CEF / Qt6 头文件来自 lib/deps 的
# 预编译包（cmake/ObSharpDeps.cmake 在 cmake 配置期下载解压）；绑定只吃头文件，包属于
# 哪个架构无所谓，优先挑目标架构那份。
function Invoke-BindingGeneration {
  # 子模块 / 依赖缺了就早失败，别等生成器报一堆 include 找不到。
  $required = [ordered]@{
    'libobs（git submodule update --init）' = 'lib/obs-studio/libobs/obs.h'
    'obs-frontend-api 头文件（git submodule update --init）' = 'lib/obs-studio/frontend/api/obs-frontend-api.h'
    'w32-pthreads 头文件（git submodule update --init）' = 'lib/obs-studio/deps/w32-pthreads/pthread.h'
    'obs-browser panel 头文件（git submodule update --init）' = 'lib/obs-browser/panel/browser-panel.hpp'
  }
  foreach ($name in $required.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $required[$name]))) {
      throw "缺 $name`n`n　$(Join-Path $RepoRoot $required[$name])"
    }
  }

  # CEF / Qt6 头文件（lib/deps 里的预编译包）。
  $depsDir = Join-Path $RepoRoot 'lib/deps'

  $qtDirs = @(Get-ChildItem -Path $depsDir -Directory -Filter 'obs-deps-qt6-*' -ErrorAction SilentlyContinue)
  if ($qtDirs.Count -eq 0) {
    throw 'lib/deps 下没有 Qt6（obs-deps-qt6-*）。先跑一次 cmake --preset <rid> 把依赖拉下来（见 cmake/ObSharpDeps.cmake）。'
  }
  $qtDir = $qtDirs | Where-Object { $_.Name -like "*-$NativeArch" } | Select-Object -First 1
  if (-not $qtDir) { $qtDir = $qtDirs | Select-Object -First 1 }
  Write-Detail "Qt6: $($qtDir.FullName)"

  $cefDirs = @(Get-ChildItem -Path $depsDir -Directory -Filter 'cef_binary_*' -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'include/cef_base.h') })
  if ($cefDirs.Count -eq 0) {
    throw 'lib/deps 下没有 CEF（cef_binary_*）。先跑一次 cmake --preset <rid> 把依赖拉下来（见 cmake/ObSharpDeps.cmake）。'
  }
  $cefDir = $cefDirs | Where-Object { $_.Name -like "*$NativeArch*" } | Select-Object -First 1
  if (-not $cefDir) { $cefDir = $cefDirs | Select-Object -First 1 }
  Write-Detail "CEF: $($cefDir.FullName)"

  $msvcStl = $null
  if ($IsWindows) {
    $msvcStl = Resolve-MsvcStlInclude
    Write-Detail "MSVC STL: $msvcStl"
  }

  # 两个目标共用一个 include 集：多给的 include 不影响产物（只收集声明位置落在
  # --input 头里的符号），所以统一给全。
  $includes = [System.Collections.Generic.List[string]]::new()
  $includes.Add('src/OBSharp.Interop.Native/src')
  $includes.Add('lib/obs-studio/libobs')
  $includes.Add('lib/obs-studio/deps/w32-pthreads')
  $includes.Add('lib/obs-studio')
  $includes.Add('lib/obs-browser/panel')
  $includes.Add($cefDir.FullName)
  $includes.Add((Join-Path $qtDir.FullName 'include'))
  $includes.Add((Join-Path $qtDir.FullName 'include/QtCore'))
  $includes.Add((Join-Path $qtDir.FullName 'include/QtGui'))
  $includes.Add((Join-Path $qtDir.FullName 'include/QtWidgets'))

  # dotnet run：生成器随用随编。生成器是多目标项目，必须带 --framework 挑一个 TFM。
  function Invoke-Generator([string[]] $GeneratorArgs) {
    Invoke-DotNet @(@('run', '--project', 'src/OBSharp.Interop.Generator',
        '--configuration', $Configuration, '--framework', $Framework, '--property:NeedRuntimeIdentifier=True', '--') + $GeneratorArgs)
  }

  # ---- native ----------------------------------------------------------------

  Write-Step '生成 Interop 绑定 → src/OBSharp.Interop/Generated/native'

  $nativeArgs = [System.Collections.Generic.List[string]]::new()
  foreach ($header in @(
      'src/OBSharp.Interop.Native/src/obsharp.h',
      'src/OBSharp.Interop.Native/src/host.h',
      'src/OBSharp.Interop.Native/src/logger.h',
      'src/OBSharp.Interop.Native/src/QT/thread.h',
      'src/OBSharp.Interop.Native/src/QT/application.h',
      'src/OBSharp.Interop.Native/src/QT/widget.h',
      'src/OBSharp.Interop.Native/src/QT/window.h',
      'src/OBSharp.Interop.Native/src/QT/filepicker.h',
      'src/OBSharp.Interop.Native/src/CEF/qcef.h',
      'src/OBSharp.Interop.Native/src/CEF/scheme.h')) {
    $nativeArgs.Add('--input')
    $nativeArgs.Add($header)
  }
  foreach ($include in $includes) {
    $nativeArgs.Add('--include')
    $nativeArgs.Add($include)
  }
  if ($msvcStl) {
    $nativeArgs.Add('--msvc-include')
    $nativeArgs.Add($msvcStl)
  }
  $nativeArgs.Add('--traverse')
  $nativeArgs.Add('obsharp_')
  $nativeArgs.Add('--lib')
  $nativeArgs.Add('OBSharp.Interop.Native')
  $nativeArgs.Add('--namespace')
  $nativeArgs.Add('OBSharp.Interop')
  $nativeArgs.Add('--output')
  $nativeArgs.Add('src/OBSharp.Interop/Generated/native')

  Invoke-Generator $nativeArgs.ToArray()

  # ---- libobs ----------------------------------------------------------------

  Write-Step '生成 Interop 绑定 → src/OBSharp.Interop/Generated/libobs'

  $libobsArgs = [System.Collections.Generic.List[string]]::new()
  foreach ($header in @(
      @(Get-ChildItem -Path (Join-Path $RepoRoot 'lib/obs-studio/libobs') -Filter 'obs*.h' |
          Where-Object { $_.Name -notmatch 'internal|nix|ffmpeg-compat|obsversion' } |
          ForEach-Object { "lib/obs-studio/libobs/$($_.Name)" }) +
      @(@('util', 'graphics', 'media-io', 'callback') | ForEach-Object {
          $dir = $_
          Get-ChildItem -Path (Join-Path $RepoRoot "lib/obs-studio/libobs/$dir") -Filter '*.h' |
            ForEach-Object { "lib/obs-studio/libobs/$dir/$($_.Name)" }
        }) +
      @('lib/obs-studio/frontend/api/obs-frontend-api.h',
        'lib/obs-studio/deps/w32-pthreads/pthread.h',
        'lib/obs-studio/deps/w32-pthreads/semaphore.h',
        'lib/obs-studio/deps/w32-pthreads/sched.h'))) {
    $libobsArgs.Add('--input')
    $libobsArgs.Add($header)
  }
  foreach ($include in $includes) {
    $libobsArgs.Add('--include')
    $libobsArgs.Add($include)
  }
  if ($msvcStl) {
    $libobsArgs.Add('--msvc-include')
    $libobsArgs.Add($msvcStl)
  }
  $libobsArgs.Add('--lib')
  $libobsArgs.Add('obs')
  $libobsArgs.Add('--lib-prefix')
  $libobsArgs.Add('obs_frontend_=obs-frontend-api')
  $libobsArgs.Add('--namespace')
  $libobsArgs.Add('OBSharp.Interop')
  $libobsArgs.Add('--output')
  $libobsArgs.Add('src/OBSharp.Interop/Generated/libobs')

  Invoke-Generator $libobsArgs.ToArray()
}

# dotnet 必须能跑。这里不检查版本：版本钉在 global.json 里，rollForward 会处理。
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw '找不到 dotnet SDK。先装 .NET SDK（版本见 global.json）。'
}

# 下面的 dotnet / cmake 都按仓库根的相对路径写，所以先把工作目录挪过去（跑完再挪回来）。
Push-Location $RepoRoot
try {

  # ---- 只跑绑定生成（-GeneratorOnly）：CI 的 dotnet job 用的就是这个形态 -------

  if ($GeneratorOnly) {
    Write-Step '只生成 Interop 绑定（-GeneratorOnly）'
    Invoke-BindingGeneration
    Write-Step 'Interop 绑定生成完成'
    return
  }

  # ---- ① dotnet job：版本文件 -------------------------------------------------

  Write-Step '① 版本文件'

  if ($SkipVersion) {
    Write-Detail '跳过（-SkipVersion），用仓库里现有的版本文件'
  }
  else {
    # CI 上这两份是 GitVersion 产出的。本地没有 GitVersion 就用占位版本 ——
    # 原生库和托管侧都能接受，编出来的东西只是版本号是 0.0.0-local。
    $versionProps = Join-Path $RepoRoot 'Directory.Version.props'
    $versionCMake = Join-Path $RepoRoot 'Directory.Version.cmake'

    if (Test-Path -LiteralPath $versionProps) {
      Write-Detail "已存在：$versionProps（保留）"
    }
    else {
      @'
<Project>
  <PropertyGroup>
    <Version>0.0.0-local</Version>
    <VersionPrefix>0.0.0</VersionPrefix>
    <AssemblyVersion>0.0.0.0</AssemblyVersion>
    <FileVersion>0.0.0.0</FileVersion>
    <InformationalVersion>0.0.0-local</InformationalVersion>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath $versionProps -Encoding utf8
      Write-Detail "写入占位版本：$versionProps"
    }

    if (Test-Path -LiteralPath $versionCMake) {
      Write-Detail "已存在：$versionCMake（保留）"
    }
    else {
      @'
# 本地构建用的占位版本；CI 会用 GitVersion 的产出覆盖它。
set(OBSHARP_VERSION "0.0.0-local")
set(OBSHARP_VERSION_NUMERIC "0.0.0")
set(OBSHARP_VERSION_FILE "0.0.0.0")
'@ | Set-Content -LiteralPath $versionCMake -Encoding utf8
      Write-Detail "写入占位版本：$versionCMake"
    }
  }

  # ---- ② native job：配置 + 编译 ----------------------------------------------

  if ($SkipNative) {
    Write-Step '② 原生库：跳过（-SkipNative）'
  }
  else {
    Write-Step "② 原生库（$Rid）"
    $cmakeExe = Resolve-CMakeExe
    Write-Detail "cmake: $cmakeExe"

    # configure：预设已经写死了生成器与架构（见 CMakePresets.json）。
    & $cmakeExe --preset $Rid
    if ($LASTEXITCODE -ne 0) { throw "cmake --preset $Rid 失败（退出码 $LASTEXITCODE）" }

    # 不用 --build --preset <rid>-<config>：Debug 在有些平台没有对应的构建预设，
    # 直接指 binaryDir + --config 更稳。
    & $cmakeExe --build $BuildDir --config $Configuration --target OBSharp.Interop.Native
    if ($LASTEXITCODE -ne 0) { throw "原生库编译失败（退出码 $LASTEXITCODE）" }
  }

  # ---- ③ dotnet job：还原 -----------------------------------------------------

  Write-Step '③ 还原 .NET 项目'
  Invoke-DotNet @('restore',  '--graph', '--artifacts-path', $ArtifactsPath)

  # ---- ④ dotnet job：Interop 绑定生成器 --------------------------------------

  if ($SkipGenerator) {
    Write-Step '④ Interop 绑定：跳过（-SkipGenerator），用 src/OBSharp.Interop/Generated/ 里现在这份'
  }
  else {
    Write-Step '④ Interop 绑定（会重写 src/OBSharp.Interop/Generated/）'
    # 生成逻辑就在本脚本的 Invoke-BindingGeneration 里；CI 的 build.yml 用
    # -GeneratorOnly 跑同一步，命令不存在第二份拷贝。生成器随用随编（dotnet run）。
    Invoke-BindingGeneration
  }

  # ---- ⑤ dotnet job：编译 + 发布 ----------------------------------------------

  Write-Step '⑤ 编译并发布托管侧'

  Invoke-DotNet @('build', '--graph', '--artifacts-path', $ArtifactsPath,
    '-c', $Configuration, '--no-restore')

  Invoke-DotNet @('publish', 'src/OBSharp.Runtime', '--graph', '--artifacts-path', $ArtifactsPath,
    '-c', $Configuration, '-f', $Framework, '--no-restore')
  Invoke-DotNet @('publish', 'src/OBSharp.PluginManager', '--graph', '--artifacts-path', $ArtifactsPath,
    '-c', $Configuration, '-f', $Framework, '--no-restore')

  # ---- ⑥ native job：Loader（NativeAOT） -------------------------------------

  Write-Step "⑥ 发布 Loader（NativeAOT, $Rid）"
  Invoke-DotNet @('restore', 'src/OBSharp.Loader', '--runtime', $Rid, '--graph',
    '--artifacts-path', $ArtifactsPath)
  Invoke-DotNet @('publish', 'src/OBSharp.Loader', '--runtime', $Rid, '--graph',
    '--artifacts-path', $ArtifactsPath, '-c', $Configuration, '-f', $Framework, '--no-restore')

  # ---- ⑦ bundle job：摆插件目录 ----------------------------------------------

  Write-Step '⑦ 摆插件目录'

  $moduleOutDir = $OsName -eq 'osx' ? (Join-Path $BundleDir 'Contents/MacOS') : (Join-Path $BundleDir 'bin/64bit')
  $anyOutDir = $OsName -eq 'osx' ? (Join-Path $BundleDir 'Contents/any') : (Join-Path $BundleDir 'bin/any')
  $dataOutDir = $OsName -eq 'osx' ? (Join-Path $BundleDir 'Contents/Resources') : (Join-Path $BundleDir 'data')

  # 插件管理器只有单一 TFM（写在它 csproj 里），按 publish 目录名认，省得脚本里再抄一遍。
  $pluginPublish = @(Get-ChildItem -Path (Join-Path $ArtifactsPath "publish/OBSharp.PluginManager/${ConfigName}_*") -Directory -ErrorAction SilentlyContinue) |
    Sort-Object -Property LastWriteTime -Descending | Select-Object -First 1

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

  # 插件管理器：放 bin/any/OBSharp.PluginManager/（托管程序集，跟宿主同一个目录树）。
  #
  # 为什么不再放 data/：它是个托管插件，运行时要被 AssemblyLoadContext 加载，跟
  # 宿主（OBSharp.Runtime）和它的依赖待一起最自然，也符合 plugins.ini 里
  # "$bin/any/..." 的写法。data/ 留给真正的数据（locale、插件自己的内容）。
  Copy-Into $pluginPublish.FullName (Join-Path $anyOutDir 'OBSharp.PluginManager')

  # 数据目录：locale 这些。
  Copy-Into $DataSrcDir $dataOutDir

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
  <key>CFBundleDisplayName</key>
  <string>OB♯</string>
  <key>CFBundleExecutable</key>
  <string>OBSharp</string>
  <key>CFBundleIdentifier</key>
  <string>dev.shimakaze.obsharp</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>OBSharp</string>
  <key>CFBundlePackageType</key>
  <string>BNDL</string>
  <key>CFBundleShortVersionString</key>
  <string>0.0.0-local</string>
  <key>CFBundleSupportedPlatforms</key>
  <array>
    <string>MacOSX</string>
  </array>
  <key>CFBundleVersion</key>
  <string>0.0.0</string>
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
    Write-Detail 'plugins.ini：保留原有那份（要重置用 -ResetPluginList）'
  }
  else {
    [System.IO.File]::WriteAllLines(
      $listPath,
      @(
        '; OB# plugin list: <assembly path> = <true|false>'
        '$bin/any/OBSharp.PluginManager/OBSharp.PluginManager.dll = true'
      ),
      [System.Text.UTF8Encoding]::new($true))
  }

  # ---- 收尾 -------------------------------------------------------------------

  Write-Step '完成'
  $files = Get-ChildItem -LiteralPath $BundleDir -Recurse -Force -File
  $total = ($files | Measure-Object -Property Length -Sum).Sum

  Write-Host "  插件目录：$BundleDir" -ForegroundColor Green
  Write-Host "  模块：    $moduleOutDir/$LoaderName"
  Write-Host "  宿主：    $anyOutDir/OBSharp.Runtime.dll"
  Write-Host "  插件管理器：$anyOutDir/OBSharp.PluginManager/OBSharp.PluginManager.dll"
  Write-Host "  数据：    $dataOutDir（含 plugins.ini）"
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
