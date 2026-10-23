# ============================================================================
#  ScPad 自检（开发工具）
#
#  编译「替身宿主 + 插件 + 自检台」，然后把插件真的跑起来：
#  采集测试截图、渲染岛上组件与面板（输出到 build\preview\*.png）、
#  模拟单击复制与按住拖出，逐项打印 PASS / FAIL。
#
#  用法：
#      powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\run-selftest.ps1
# ============================================================================

[CmdletBinding()]
param(
    [string]$SkiaSharpPath,
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $PSScriptRoot
$previewDir = Join-Path $PSScriptRoot 'preview'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# ---- 定位运行时 / SkiaSharp ----
$sharedRoot = 'C:\Program Files\dotnet\shared'
function Resolve-NewestRuntime([string]$name) {
    $dir = Join-Path $sharedRoot $name
    if (-not (Test-Path $dir)) { return $null }
    $versions = Get-ChildItem $dir -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending
    if ($versions.Count -eq 0) { return $null }
    return $versions[0].FullName
}
$frameworkDir = Resolve-NewestRuntime 'Microsoft.NETCore.App'
$desktopDir = Resolve-NewestRuntime 'Microsoft.WindowsDesktop.App'
if (-not $frameworkDir -or -not $desktopDir) { throw '找不到 .NET 桌面运行时' }

if (-not $SkiaSharpPath) {
    $candidates = @()
    foreach ($root in @('D:\GoodExecution', 'C:\Program Files', 'C:\Program Files (x86)')) {
        if (-not (Test-Path $root)) { continue }
        $candidates += Get-ChildItem $root -Recurse -Filter 'SkiaSharp.dll' -ErrorAction SilentlyContinue | Select-Object -First 40
    }
    foreach ($candidate in $candidates) {
        try {
            $name = [System.Reflection.AssemblyName]::GetAssemblyName($candidate.FullName)
            if ($name.Version.Major -eq 2 -and $name.Version.Minor -eq 88) { $SkiaSharpPath = $candidate.FullName; break }
        } catch { }
    }
}
if (-not $SkiaSharpPath) { throw '找不到 SkiaSharp 2.88，请用 -SkiaSharpPath 指定' }

$references = New-Object System.Collections.Generic.List[string]
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($dir in @($frameworkDir, $desktopDir)) {
    foreach ($file in Get-ChildItem $dir -Filter '*.dll') {
        if (-not $seen.Add($file.Name)) { continue }
        try { $null = [System.Reflection.AssemblyName]::GetAssemblyName($file.FullName) } catch { continue }
        $references.Add($file.FullName)
    }
}
$references.Add($SkiaSharpPath)

$work = Join-Path $env:TEMP ('ScPad-selftest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $previewDir | Out-Null

function Invoke-Csc([string]$output, [string[]]$sources, [string[]]$extra, [string]$target) {
    $arguments = @('/nologo', '/noconfig', '/nostdlib+', "/target:$target", '/langversion:5',
        '/warn:0', '/nowarn:1701,1702', "/out:$output") + ($references | ForEach-Object { '/r:' + $_ })
    if ($extra) { $arguments += $extra }
    $arguments += $sources

    $text = & $csc @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($text | Out-String) -ForegroundColor Red
        throw "编译失败：$output"
    }
    if ($text) { Write-Host ($text | Out-String) -ForegroundColor DarkYellow }
}

try {
    Write-Host '编译替身宿主…' -ForegroundColor Cyan
    $stub = Join-Path $work 'NotchPeninsula.dll'
    Invoke-Csc $stub @((Join-Path $PSScriptRoot 'PluginApiStub.cs')) @() 'library'

    Write-Host '编译插件…' -ForegroundColor Cyan
    $plugin = Join-Path $work 'ScPad.dll'
    $sources = Get-ChildItem $projectDir -Filter '*.cs' |
    Where-Object { $_.DirectoryName -eq $projectDir } |
        ForEach-Object { $_.FullName }
    Invoke-Csc $plugin $sources @('/r:' + $stub) 'library'

    Write-Host '编译自检台…' -ForegroundColor Cyan
    $test = Join-Path $work 'SelfTest.dll'
    Invoke-Csc $test @((Join-Path $PSScriptRoot 'selftest\SelfTest.cs')) @("/r:$stub", "/r:$plugin") 'exe'

    # 运行时依赖：SkiaSharp 需要原生库
    Copy-Item $SkiaSharpPath (Join-Path $work 'SkiaSharp.dll') -Force
    $native = Join-Path (Split-Path -Parent $SkiaSharpPath) 'libSkiaSharp.dll'
    if (Test-Path $native) { Copy-Item $native (Join-Path $work 'libSkiaSharp.dll') -Force }

    $runtimeConfig = @"
{
  "runtimeOptions": {
    "tfm": "net10.0-windows",
    "framework": { "name": "Microsoft.WindowsDesktop.App", "version": "$(Split-Path -Leaf $desktopDir)" }
  }
}
"@
    [System.IO.File]::WriteAllText((Join-Path $work 'SelfTest.runtimeconfig.json'), $runtimeConfig,
        (New-Object System.Text.UTF8Encoding($false)))

    if ($KeepData) { $env:SCPAD_SELFTEST_KEEP = '1' } else { $env:SCPAD_SELFTEST_KEEP = '0' }

    Write-Host ''
    & 'C:\Program Files\dotnet\dotnet.exe' exec (Join-Path $work 'SelfTest.dll') $previewDir
    $code = $LASTEXITCODE

    Write-Host ''
    Write-Host "预览图目录：$previewDir" -ForegroundColor Cyan
    Write-Host "自检退出码：$code"
    exit $code
}
finally {
    try { Remove-Item $work -Recurse -Force } catch { }
}
