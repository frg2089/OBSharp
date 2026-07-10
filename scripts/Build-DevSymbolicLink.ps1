[CmdletBinding()]
param (
  [Parameter()]
  [switch]
  $Legacy
)

$ErrorActionPreference = 'Stop'

if ($IsMacOS) {
  throw 'This script is not supported on macOS. but the plugin can work on macOS.'
}

$Private:TFM = 'net10.0'
$Private:Configuration = 'Debug'
$Private:ProjectRoot = Split-Path -Path $PSScriptRoot -Parent

if ($Legacy -and $IsWindows) {
  $Private:OBSStudioPath = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\OBS Studio\' -Name '(default)'

  $Private:PluginDir = "$Private:OBSStudioPath\obs-plugins\"
  $Private:PluginBinDir = $Private:PluginDir

  $Private:PluginBinDir64 = Join-Path -Path $Private:PluginBinDir -ChildPath '64bit'
  $Private:PluginBinDir32 = Join-Path -Path $Private:PluginBinDir -ChildPath '32bit'
  $Private:PluginBinDirAny = Join-Path -Path $Private:PluginBinDir -ChildPath 'any'

  $Private:PluginDataDir = Join-Path -Path $Private:PluginDir -ChildPath 'data', 'obs-plugins', 'OBSharp'

  if (Test-Path -Path "$Private:PluginBinDir64\OBSharp.dll") {
    Remove-Item -Path "$Private:PluginBinDir64\OBSharp.dll" -Recurse -Force
  }
  if (Test-Path -Path "$Private:PluginBinDir32\OBSharp.dll") {
    Remove-Item -Path "$Private:PluginBinDir32\OBSharp.dll" -Recurse -Force
  }
  if (Test-Path -Path $Private:PluginBinDirAny) {
    Remove-Item -Path $Private:PluginBinDirAny -Recurse -Force
  }
  if (Test-Path -Path $Private:PluginDataDir) {
    Remove-Item -Path $Private:PluginDataDir -Recurse -Force
  }
  
  New-Item -ItemType 'SymbolicLink' -Path "$Private:PluginBinDir64\OBSharp.dll" -Value "$Private:ProjectRoot\src\OBSharp\bin\Release\$Private:TFM\publish\win-x64\OBSharp.dll"
  New-Item -ItemType 'SymbolicLink' -Path "$Private:PluginBinDir32\OBSharp.dll" -Value "$Private:ProjectRoot\src\OBSharp\bin\Release\$Private:TFM\publish\win-x86\OBSharp.dll"
  New-Item -ItemType 'SymbolicLink' -Path $Private:PluginBinDirAny -Value "$Private:ProjectRoot\src\OBSharp.Runtime\bin\$Private:Configuration\$Private:TFM\"
  New-Item -ItemType 'SymbolicLink' -Path $Private:PluginDataDir -Value "$Private:ProjectRoot\src\OBSharp.Runtime\Data\"
}
else {
  if ($IsWindows) {
    $Private:PluginDir = "$env:ProgramData\obs-studio\plugins\OBSharp"
  }
  elseif ($IsLinux) {
    $Private:PluginDir = "$env:HOME/.config/obs-studio/plugins/OBSharp"
  }
  elseif ($IsMacOS) {
    $Private:PluginDir = "$env:HOME/Library/Application Support/obs-studio/plugins/OBSharp"
  }
  else {
    throw 'Platform not supported.'
  }

  $Private:PluginBinDir = Join-Path -Path $Private:PluginDir -ChildPath 'bin'
  $Private:PluginBinDir64 = Join-Path -Path $Private:PluginBinDir -ChildPath '64bit'
  $Private:PluginBinDir32 = Join-Path -Path $Private:PluginBinDir -ChildPath '32bit'
  $Private:PluginBinDirAny = Join-Path -Path $Private:PluginBinDir -ChildPath 'any'
  $Private:PluginDataDir = Join-Path -Path $Private:PluginDir -ChildPath 'data'
  
  if (Test-Path -Path $Private:PluginDir) {
    Remove-Item -Path $Private:PluginDir -Recurse -Force
  }
  
  New-Item -ItemType 'Directory' -Path $Private:PluginBinDir
  New-Item -ItemType 'SymbolicLink' -Path $Private:PluginBinDir64 -Value "$Private:ProjectRoot\src\OBSharp\bin\Release\$Private:TFM\publish\win-x64\"
  New-Item -ItemType 'SymbolicLink' -Path $Private:PluginBinDir32 -Value "$Private:ProjectRoot\src\OBSharp\bin\Release\$Private:TFM\publish\win-x86\"
  New-Item -ItemType 'SymbolicLink' -Path $Private:PluginBinDirAny -Value "$Private:ProjectRoot\src\OBSharp.Runtime\bin\$Private:Configuration\$Private:TFM\"
  New-Item -ItemType 'SymbolicLink' -Path $Private:PluginDataDir -Value "$Private:ProjectRoot\src\OBSharp.Runtime\Data\"
}

Write-Host 'Building the plugin...' -ForegroundColor Cyan
dotnet publish "$Private:ProjectRoot\src\OBSharp" --framework $Private:TFM -property:PublishProfile='win-x64'
dotnet publish "$Private:ProjectRoot\src\OBSharp" --framework $Private:TFM -property:PublishProfile='win-x86'
dotnet build "$Private:ProjectRoot\src\OBSharp.Runtime" --configuration $Private:Configuration