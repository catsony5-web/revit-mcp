#requires -Version 5.1
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem
$depRoot = Join-Path $PSScriptRoot 'deps'
$cache = Join-Path $depRoot 'cache'
$dllRoot = Join-Path $depRoot 'roslyn'
$noticeRoot = Join-Path $dllRoot 'licenses'
New-Item -ItemType Directory -Force -Path $cache,$dllRoot,$noticeRoot | Out-Null
$packages = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.lock.json') -Raw | ConvertFrom-Json
foreach ($package in $packages) {
    if ($package.id -notmatch '^[a-z0-9.]+$' -or $package.version -notmatch '^\d+\.\d+\.\d+$' -or $package.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'Invalid dependency lock entry.'
    }
    $archiveName = $package.id + '.' + $package.version + '.nupkg'
    $archivePath = Join-Path $cache $archiveName
    if (-not (Test-Path -LiteralPath $archivePath)) {
        $uri = 'https://api.nuget.org/v3-flatcontainer/' + $package.id + '/' + $package.version + '/' + $archiveName
        Write-Host ('Downloading ' + $package.id + ' ' + $package.version)
        Invoke-WebRequest -Uri $uri -OutFile $archivePath -UseBasicParsing -TimeoutSec 180
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $package.sha256) {
        throw "Dependency hash mismatch: $archiveName. Remove this file from deps/cache and retry."
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entry = $zip.GetEntry($package.entry)
        if ($null -eq $entry -or $entry.Name -notmatch '^[A-Za-z0-9.]+\.dll$') { throw "Missing DLL entry: $archiveName" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dllRoot $entry.Name), $true)
        foreach ($notice in $zip.Entries) {
            if ($notice.Name -match '^(LICENSE|NOTICE|THIRD.PARTY.NOTICES).*\.(txt|md)$') {
                $noticeName = $package.id + '-' + $notice.Name
                [IO.Compression.ZipFileExtensions]::ExtractToFile($notice, (Join-Path $noticeRoot $noticeName), $true)
            }
        }
    } finally { $zip.Dispose() }
}
Write-Host 'Dependencies restored and SHA-256 verified.'
