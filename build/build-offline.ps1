# ============================================================================
#  ScPad —— 离线编译脚本（机器上没有 .NET SDK 时用这条）
#
#  原理：用 Windows 自带的 C# 编译器（%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe）
#        直接对着本机 .NET 运行时里的程序集编译，再用 build\PluginApiStub.cs 编译出一个
#        名为 NotchPeninsula.dll 的「签名替身」当引用。整个过程不需要 SDK、不需要联网、
#        不需要 NuGet。
#
#  用法（在任意目录）：
#      powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build\build-offline.ps1
#
#  产物：
#      ..\ScPad-Plugin\ScPad.dll    ← 放进 <灵动岛目录>\plugins\ 即可
#      ..\ScPad-Plugin\plugin.json
#
#  有 .NET SDK 的机器请用 dotnet build ScPad.csproj（那条路走真正的宿主工程
#  引用，不依赖这里的签名替身）。
# ============================================================================

[CmdletBinding()]
param(
    [string]$Csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe',
    [string]$SkiaSharpPath,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $PSScriptRoot
$stubSource = Join-Path $PSScriptRoot 'PluginApiStub.cs'
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = Join-Path (Split-Path -Parent $projectDir) 'ScPad-Plugin' }

function Write-Step($text) { Write-Host "  $text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  [OK] $text" -ForegroundColor Green }

Write-Host ""
Write-Host "ScPad · 离线编译" -ForegroundColor White
Write-Host "----------------------------------------"

# ---- 1. 定位编译器 ----
if (-not (Test-Path $Csc)) { throw "找不到 C# 编译器：$Csc" }
Write-Ok "编译器：$Csc"

# ---- 2. 定位 .NET 运行时程序集 ----
$dotnetRoot = 'C:\Program Files\dotnet'
$sharedRoot = Join-Path $dotnetRoot 'shared'
if (-not (Test-Path $sharedRoot)) { throw "找不到 .NET 运行时目录：$sharedRoot" }

function Resolve-NewestRuntime([string]$name) {
    $dir = Join-Path $sharedRoot $name
    if (-not (Test-Path $dir)) { return $null }
    $versions = Get-ChildItem $dir -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending
    if ($versions.Count -eq 0) { return $null }
    return $versions[0].FullName
}

$frameworkDir = Resolve-NewestRuntime 'Microsoft.NETCore.App'
$desktopDir = Resolve-NewestRuntime 'Microsoft.WindowsDesktop.App'
if (-not $frameworkDir) { throw "找不到 Microsoft.NETCore.App 运行时" }
if (-not $desktopDir) { throw "找不到 Microsoft.WindowsDesktop.App 运行时（需要 .NET 桌面运行时）" }

Write-Ok "运行时：$(Split-Path -Leaf $frameworkDir) / $(Split-Path -Leaf $desktopDir)"

# ---- 3. 定位 SkiaSharp 2.88 ----
function Resolve-SkiaSharp {
    if ($SkiaSharpPath) {
        if (-not (Test-Path $SkiaSharpPath)) { throw "指定的 SkiaSharp 不存在：$SkiaSharpPath" }
        return $SkiaSharpPath
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    # a) NuGet 全局缓存里的 2.88.x
    foreach ($root in @($env:NUGET_PACKAGES, (Join-Path $env:USERPROFILE '.nuget\packages'))) {
        if ([string]::IsNullOrEmpty($root) -or -not (Test-Path $root)) { continue }
        $pkg = Join-Path $root 'skiasharp'
        if (-not (Test-Path $pkg)) { continue }
        Get-ChildItem $pkg -Directory | Where-Object { $_.Name -like '2.88.*' } | ForEach-Object {
            foreach ($rel in @('lib\netstandard2.0\SkiaSharp.dll', 'lib\netstandard2.1\SkiaSharp.dll', 'lib\net6.0\SkiaSharp.dll')) {
                $path = Join-Path $_.FullName $rel
                if (Test-Path $path) { $candidates.Add($path) }
            }
        }
    }

    # b) 本机其它位置的 2.88 版（比如宿主目录、其它软件自带的副本）
    foreach ($root in @('D:\GoodExecution', 'C:\Program Files', 'C:\Program Files (x86)')) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem $root -Recurse -Filter 'SkiaSharp.dll' -ErrorAction SilentlyContinue |
            Select-Object -First 40 |
            ForEach-Object { $candidates.Add($_.FullName) }
    }

    foreach ($candidate in $candidates) {
        try {
            $name = [System.Reflection.AssemblyName]::GetAssemblyName($candidate)
            if ($name.Version.Major -eq 2 -and $name.Version.Minor -eq 88) { return $candidate }
        } catch { }
    }

    throw "找不到 SkiaSharp 2.88 的托管 dll。请用 -SkiaSharpPath 指定（灵动岛宿主用的是 2.88.x）。"
}

$skia = Resolve-SkiaSharp
$skiaVersion = [System.Reflection.AssemblyName]::GetAssemblyName($skia).Version
Write-Ok "SkiaSharp：$skia（$skiaVersion）"

# ---- 4. 组装引用列表 ----
Write-Step "收集引用程序集…"
$references = New-Object System.Collections.Generic.List[string]
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($dir in @($frameworkDir, $desktopDir)) {
    foreach ($file in Get-ChildItem $dir -Filter '*.dll') {
        if (-not $seen.Add($file.Name)) { continue }
        try { $null = [System.Reflection.AssemblyName]::GetAssemblyName($file.FullName) }
        catch { continue }   # 原生 dll（clretwrc / msvcp140 之类）跳过
        $references.Add($file.FullName)
    }
}
$references.Add($skia)
Write-Ok "$($references.Count) 个引用"

$scratch = Join-Path $env:TEMP ('ScPad-build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

function Invoke-Csc([string]$output, [string[]]$sources, [string[]]$extra) {
    $arguments = @('/nologo', '/noconfig', '/nostdlib+', '/target:library', '/langversion:5',
        '/optimize+', '/warn:0', '/nowarn:1701,1702', "/out:$output") +
        ($references | ForEach-Object { '/r:' + $_ })

    if ($extra) { $arguments += $extra }
    $arguments += $sources

    $outputText = & $Csc @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($outputText | Out-String) -ForegroundColor Red
        throw "编译失败：$output"
    }
    if ($outputText) { Write-Host ($outputText | Out-String) -ForegroundColor DarkYellow }
}

try {
    # ---- 5. 先编「签名替身」NotchPeninsula.dll ----
    Write-Step "编译插件 API 替身（NotchPeninsula.dll）…"
    $stub = Join-Path $scratch 'NotchPeninsula.dll'
    Invoke-Csc $stub @($stubSource) @()
    Write-Ok $stub

    # ---- 6. 编译插件本体 ----
    Write-Step "编译 ScPad.dll…"
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $plugin = Join-Path $OutDir 'ScPad.dll'

    $sources = Get-ChildItem $projectDir -Filter '*.cs' |
    Where-Object { $_.DirectoryName -eq $projectDir } |
    ForEach-Object { $_.FullName }
    if (-not $sources) { throw "在 $projectDir 下找不到任何 .cs 源码" }

    Invoke-Csc $plugin $sources @('/r:' + $stub)
    Write-Ok $plugin

    # ---- 7. 插件清单 ----
    $manifest = Join-Path $OutDir 'plugin.json'
    $json = "{`r`n  `"dll`": `"ScPad.dll`"`r`n}"
    [System.IO.File]::WriteAllText($manifest, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok $manifest

    Write-Host ""
    Write-Host "完成。" -ForegroundColor Green
    Write-Host "把整个文件夹复制到  <灵动岛目录>\plugins\  下，然后在「我的插件」里启用即可："
    Write-Host "  $OutDir"
    Write-Host ""
}
finally {
    try { Remove-Item $scratch -Recurse -Force } catch { }
}
