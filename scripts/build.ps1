[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../outputs')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputRoot)
$project = Join-Path $root 'work/BottomScreen-AutoCursor/BottomScreen.csproj'
$source = Split-Path -Parent $project
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Unsupported project version: $version"
}
$fixed = Join-Path $output 'BottomScreen'
$versioned = Join-Path $output "BottomScreen-v$version"
$fixedExe = Join-Path $fixed 'BottomScreen.exe'

function Assert-InstalledAppStopped {
    $instances = @(Get-CimInstance Win32_Process -Filter "Name='BottomScreen.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.Equals($fixedExe, [StringComparison]::OrdinalIgnoreCase) })
    if ($instances.Count -gt 0) {
        throw 'BottomScreen is running from the installation directory. Stop capture and close it before updating.'
    }
}

function Get-UserFileHashes {
    $hashes = @{}
    if (Test-Path -LiteralPath $fixed) {
        Get-ChildItem -LiteralPath $fixed -File -Recurse | Where-Object {
            $_.Name -eq 'settings.json' -or $_.Name -eq 'BottomScreen.log' -or
            $_.FullName.StartsWith((Join-Path $fixed 'logs') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        } | ForEach-Object { $hashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    return ,$hashes
}

Assert-InstalledAppStopped
New-Item -ItemType Directory -Path $output -Force | Out-Null
$buildId = [Guid]::NewGuid().ToString('N')
$scratch = Join-Path $output ".build/$buildId"
$publish = Join-Path $scratch 'publish'
$staged = Join-Path $scratch "BottomScreen-v$version"
New-Item -ItemType Directory -Path $publish, $staged -Force | Out-Null

Push-Location $root
try {
    & dotnet restore $project -r win-x64 --locked-mode -p:PublishSingleFile=true -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
    & dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
} finally { Pop-Location }

$builtExe = Join-Path $publish 'BottomScreen.exe'
$builtVersion = (Get-Item -LiteralPath $builtExe).VersionInfo.ProductVersion
if ($builtVersion -ne $version) { throw "Executable version mismatch: $builtVersion / $version" }
$exeHash = (Get-FileHash -LiteralPath $builtExe -Algorithm SHA256).Hash
Copy-Item -LiteralPath $builtExe -Destination $staged
Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination (Join-Path $staged '使用说明.md')
Copy-Item -LiteralPath (Join-Path $source '性能与延迟复核.md'), (Join-Path $source 'THIRD-PARTY-NOTICES.txt') -Destination $staged
Copy-Item -LiteralPath (Join-Path $root 'packaging/恢复系统光标.cmd'), (Join-Path $root 'licenses') -Destination $staged -Recurse
"$exeHash  BottomScreen.exe" | Set-Content -LiteralPath (Join-Path $staged 'SHA256.txt') -Encoding ascii
if ((Test-Path (Join-Path $staged 'settings.json')) -or (Test-Path (Join-Path $staged 'logs'))) {
    throw 'Fresh packages must not contain user settings or logs.'
}

# Existing version archives are immutable. Rebuilding an unchanged executable is allowed.
if (Test-Path -LiteralPath $versioned) {
    $archivedExe = Join-Path $versioned 'BottomScreen.exe'
    if (-not (Test-Path -LiteralPath $archivedExe) -or
        (Get-FileHash -LiteralPath $archivedExe -Algorithm SHA256).Hash -ne $exeHash) {
        throw "Version archive already exists with different contents. Increase the project version: $versioned"
    }
} else {
    Copy-Item -LiteralPath $staged -Destination $versioned -Recurse
}

Assert-InstalledAppStopped
$preserved = Get-UserFileHashes
$backup = $null
if (Test-Path -LiteralPath $fixed) {
    $backup = Join-Path $output ('.backups/BottomScreen-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $buildId.Substring(0, 8))
    New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
    Copy-Item -LiteralPath $fixed -Destination $backup -Recurse
}
Assert-InstalledAppStopped
New-Item -ItemType Directory -Path $fixed -Force | Out-Null
Get-ChildItem -LiteralPath $staged | Copy-Item -Destination $fixed -Recurse -Force
foreach ($path in $preserved.Keys) {
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $preserved[$path]) {
        throw "User file preservation failed: $path"
    }
}
foreach ($directory in @($versioned, $fixed)) {
    $exe = Join-Path $directory 'BottomScreen.exe'
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $exeHash -or
        (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -ne $version) {
        throw "Release verification failed: $directory"
    }
}

# Package the clean stage, never the existing installation with its settings/logs.
$zipName = "BottomScreen-v$version-win-x64.zip"
$temporaryZip = Join-Path $scratch $zipName
Compress-Archive -LiteralPath $staged -DestinationPath $temporaryZip -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
try {
    $entry = $archive.GetEntry("BottomScreen-v$version/BottomScreen.exe")
    if ($null -eq $entry) { throw 'ZIP executable is missing.' }
    $stream = $entry.Open()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $zipExeHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
    finally { $hasher.Dispose(); $stream.Dispose() }
    if ($zipExeHash -ne $exeHash) { throw 'ZIP executable SHA256 mismatch.' }
    foreach ($file in $archive.Entries) {
        if ($file.FullName -match '(^|/)(settings\.json|logs)(/|$)' -or $file.FullName -match 'BottomScreen\.log$') {
            throw 'ZIP unexpectedly includes user data.'
        }
    }
} finally { $archive.Dispose() }
$zipPath = Join-Path $output $zipName
Copy-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash  $zipName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii

$report = [ordered]@{
    version = $version; executableSha256 = $exeHash; zipSha256 = $zipHash
    versionedDirectory = $versioned; fixedDirectory = $fixed; zip = $zipPath
    previousInstallBackup = $backup; preservedUserFileCount = $preserved.Count
    bothExecutablesVerified = $true; zipExecutableVerified = $true
    userFilesPreserved = $true; interactiveGameTestsRun = $false
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-verification.json') -Encoding utf8
if ($env:GITHUB_OUTPUT) {
    "version=$version" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "zip_path=$zipPath" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "exe_sha256=$exeHash" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "zip_sha256=$zipHash" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
}
$report | ConvertTo-Json
