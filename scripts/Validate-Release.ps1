[CmdletBinding()]
param(
    [ValidateSet('Stable', 'Testing')]
    [string]$Channel = 'Stable',
    [string]$ReleaseTag,
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'GlamSpector.csproj'
$sourceManifestPath = Join-Path $repositoryRoot 'GlamSpector.json'
$repositoryManifestPath = Join-Path $repositoryRoot 'repo.json'

function Assert-Equal {
    param(
        [string]$Label,
        [AllowNull()][object]$Actual,
        [AllowNull()][object]$Expected
    )

    if ([string]$Actual -cne [string]$Expected) {
        throw "$Label mismatch: expected '$Expected', got '$Actual'."
    }
}

function Assert-ReleaseAssetUrl {
    param(
        [string]$Label,
        [string]$Url,
        [string]$Version
    )

    $expected = "https://github.com/Totyh/GlamSpector/releases/download/v$Version/latest.zip"
    Assert-Equal $Label $Url $expected
    if (-not $Url.EndsWith('.zip', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must point to a .zip release asset."
    }
    if ($Url -match '/archive/|source|\.dll(?:$|\?)|/releases/latest/') {
        throw "$Label points to an unsupported moving, source, or DLL download."
    }
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]$project.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($projectVersion)) {
    throw 'GlamSpector.csproj does not define Version.'
}

$sdkName = [string]$project.Project.Sdk
if ($sdkName -notmatch '^Dalamud\.NET\.Sdk/(?<level>\d+)\.') {
    throw "Cannot derive the Dalamud API level from project SDK '$sdkName'."
}
$expectedApiLevel = [int]$Matches.level

$sourceManifest = Get-Content -LiteralPath $sourceManifestPath -Raw | ConvertFrom-Json
$repositoryEntries = @(Get-Content -LiteralPath $repositoryManifestPath -Raw | ConvertFrom-Json)
if ($repositoryEntries.Count -ne 1) {
    throw "repo.json must contain exactly one plugin entry; found $($repositoryEntries.Count)."
}
$entry = $repositoryEntries[0]

Assert-Equal 'Source manifest InternalName' $sourceManifest.InternalName 'GlamSpector'
Assert-Equal 'Source manifest DalamudApiLevel' $sourceManifest.DalamudApiLevel $expectedApiLevel
Assert-Equal 'Repository InternalName' $entry.InternalName 'GlamSpector'
Assert-Equal 'Repository DalamudApiLevel' $entry.DalamudApiLevel $expectedApiLevel
Assert-Equal 'Repository IsTestingExclusive' $entry.IsTestingExclusive $false
Assert-ReleaseAssetUrl 'DownloadLinkInstall' $entry.DownloadLinkInstall $entry.AssemblyVersion
Assert-ReleaseAssetUrl 'DownloadLinkUpdate' $entry.DownloadLinkUpdate $entry.AssemblyVersion

$hasTestingChannel = -not [string]::IsNullOrWhiteSpace([string]$entry.TestingAssemblyVersion)
if ($hasTestingChannel) {
    Assert-Equal 'Repository TestingDalamudApiLevel' $entry.TestingDalamudApiLevel $expectedApiLevel
    Assert-ReleaseAssetUrl 'DownloadLinkTesting' $entry.DownloadLinkTesting $entry.TestingAssemblyVersion
    if ([string]::IsNullOrWhiteSpace([string]$entry.TestingChangelog)) {
        throw 'TestingChangelog must describe the opt-in testing build.'
    }

    try {
        $stableVersion = [version]$entry.AssemblyVersion
        $testingVersion = [version]$entry.TestingAssemblyVersion
    }
    catch {
        throw "Repository stable/testing versions must be valid System.Version values: $($_.Exception.Message)"
    }
    if ($testingVersion -le $stableVersion) {
        throw "TestingAssemblyVersion '$testingVersion' must be newer than stable AssemblyVersion '$stableVersion'."
    }
}
elseif ($Channel -eq 'Testing') {
    throw 'Testing validation requires TestingAssemblyVersion, TestingDalamudApiLevel, and DownloadLinkTesting.'
}

$advertisedVersion = if ($Channel -eq 'Testing') {
    [string]$entry.TestingAssemblyVersion
}
else {
    [string]$entry.AssemblyVersion
}
Assert-Equal "$Channel repository version" $advertisedVersion $projectVersion

if (-not [string]::IsNullOrWhiteSpace($ReleaseTag)) {
    Assert-Equal 'Release tag' $ReleaseTag "v$projectVersion"
}

if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    $resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
    $packageInfo = Get-Item -LiteralPath $resolvedPackage
    if ($packageInfo.Length -le 0) {
        throw "Package '$resolvedPackage' is empty."
    }

    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("GlamSpector-release-validation-" + [guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $temporaryDirectory
        $expectedEntries = @(
            'GlamSpector.deps.json',
            'GlamSpector.dll',
            'GlamSpector.json',
            'licenses/NotoSans-OFL.txt',
            'Microsoft.Data.Sqlite.dll',
            'runtimes/win-x64/native/e_sqlite3.dll',
            'SixLabors.Fonts.dll',
            'SixLabors.ImageSharp.dll',
            'SixLabors.ImageSharp.Drawing.dll',
            'SQLitePCLRaw.batteries_v2.dll',
            'SQLitePCLRaw.core.dll',
            'SQLitePCLRaw.provider.e_sqlite3.dll'
        )
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage)
        try {
            $actualEntries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') } | Sort-Object)
        }
        finally {
            $archive.Dispose()
        }
        $unexpectedEntries = @($actualEntries | Where-Object { $_ -notin $expectedEntries })
        $missingEntries = @($expectedEntries | Where-Object { $_ -notin $actualEntries })
        if ($unexpectedEntries.Count -gt 0 -or $missingEntries.Count -gt 0) {
            throw "Package entry mismatch. Missing: [$($missingEntries -join ', ')]. Unexpected: [$($unexpectedEntries -join ', ')]."
        }

        $packagedManifestPath = Join-Path $temporaryDirectory 'GlamSpector.json'
        $packagedAssemblyPath = Join-Path $temporaryDirectory 'GlamSpector.dll'
        if (-not (Test-Path -LiteralPath $packagedManifestPath -PathType Leaf)) {
            throw 'Package does not contain GlamSpector.json.'
        }
        if (-not (Test-Path -LiteralPath $packagedAssemblyPath -PathType Leaf)) {
            throw 'Package does not contain GlamSpector.dll.'
        }

        $packagedManifest = Get-Content -LiteralPath $packagedManifestPath -Raw | ConvertFrom-Json
        $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($packagedAssemblyPath).Version.ToString()
        Assert-Equal 'Packaged manifest InternalName' $packagedManifest.InternalName 'GlamSpector'
        Assert-Equal 'Packaged manifest AssemblyVersion' $packagedManifest.AssemblyVersion $projectVersion
        Assert-Equal 'Packaged manifest DalamudApiLevel' $packagedManifest.DalamudApiLevel $expectedApiLevel
        Assert-Equal 'Packaged assembly version' $assemblyVersion $projectVersion

        $fontLicensePath = Join-Path $temporaryDirectory 'licenses\NotoSans-OFL.txt'
        $fontLicense = Get-Content -LiteralPath $fontLicensePath -Raw
        if ($fontLicense -notmatch 'SIL OPEN FONT LICENSE Version 1\.1' -or
            $fontLicense -notmatch 'The Noto Project Authors') {
            throw 'Packaged Noto Sans license notice is missing or invalid.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
        }
    }
}

Write-Host "Release metadata is consistent for the $Channel channel: GlamSpector $projectVersion, Dalamud API $expectedApiLevel."
