#Requires -Version 7.0

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeProject = Join-Path $repoRoot 'AutomaTable\AutomaTable.csproj'
$toolProject = Join-Path $repoRoot 'AutomaTable.Tool\AutomaTable.Tool.csproj'
$generatorProject = Join-Path $repoRoot 'AutomaTable.Generator\AutomaTable.Generator.csproj'
$upmRoot = Join-Path $repoRoot 'AutomaTable.Unity\Packages\com.jinwooyang.automatable'
$packageOutput = Join-Path $repoRoot 'artifacts\packages'
$upmOutput = Join-Path $repoRoot 'artifacts\upm'

function Get-ProjectVersion([string]$projectPath) {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    return [string]$project.Project.PropertyGroup.Version
}

function Get-NuGetPackageRoot([hashtable]$assets, [string]$packageId) {
    $library = $assets.libraries.GetEnumerator() |
        Where-Object { $_.Key.Split('/')[0] -ieq $packageId } |
        Select-Object -First 1

    if ($null -eq $library) {
        throw "Package '$packageId' was not found in project.assets.json."
    }

    $packageFolder = $assets.packageFolders.Keys | Select-Object -First 1
    return Join-Path $packageFolder $library.Value.path
}

function Copy-IfChanged([string]$source, [string]$destination) {
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($sourceHash -eq $destinationHash) {
            return
        }
    }

    try {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    catch {
        throw "Could not refresh '$destination'. Close Unity Editor if it has the native plugin loaded, then retry. $($_.Exception.Message)"
    }
}

function Copy-PackageFile(
    [hashtable]$assets,
    [string]$packageId,
    [string]$relativePath,
    [string]$destination
) {
    $packageRoot = Get-NuGetPackageRoot $assets $packageId
    $source = Join-Path $packageRoot $relativePath
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Release input was not found: $source"
    }

    Copy-IfChanged $source $destination
}

$runtimeVersion = Get-ProjectVersion $runtimeProject
$toolVersion = Get-ProjectVersion $toolProject
$upmManifest = Get-Content -LiteralPath (Join-Path $upmRoot 'package.json') -Raw |
    ConvertFrom-Json

if ($runtimeVersion -ne $toolVersion -or $runtimeVersion -ne $upmManifest.version) {
    throw "Release versions do not match: runtime=$runtimeVersion, tool=$toolVersion, upm=$($upmManifest.version)."
}

Push-Location $repoRoot
try {
    # The test host launches nested dotnet builds. Reused MSBuild processes can
    # retain their redirected pipes and leave those tests waiting indefinitely.
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

    dotnet restore .\AutomaTable.sln
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    if (-not $SkipTests) {
        dotnet test .\AutomaTable.Tests\AutomaTable.Tests.csproj `
            --configuration $Configuration `
            --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
    }

    New-Item -ItemType Directory -Force -Path $packageOutput, $upmOutput | Out-Null

    dotnet pack $runtimeProject `
        --configuration $Configuration `
        --no-restore `
        --output $packageOutput
    if ($LASTEXITCODE -ne 0) { throw 'Packing AutomaTable failed.' }

    dotnet pack $toolProject `
        --configuration $Configuration `
        --no-restore `
        --output $packageOutput
    if ($LASTEXITCODE -ne 0) { throw 'Packing AutomaTable.Tool failed.' }

    dotnet build $generatorProject --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Building AutomaTable.Generator failed.' }

    $pluginRoot = Join-Path $upmRoot 'Runtime\Plugins'
    Copy-IfChanged `
        (Join-Path $repoRoot "AutomaTable\bin\$Configuration\netstandard2.1\AutomaTable.dll") `
        (Join-Path $pluginRoot 'AutomaTable.dll')
    Copy-IfChanged `
        (Join-Path $repoRoot "AutomaTable.Generator\bin\$Configuration\netstandard2.0\AutomaTable.Generator.dll") `
        (Join-Path $upmRoot 'Editor\Analyzers\AutomaTable.Generator.dll')

    $assetsPath = Join-Path $repoRoot 'AutomaTable\obj\project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable

    Copy-PackageFile $assets 'sqlite-net-pcl' 'lib\netstandard2.0\SQLite-net.dll' `
        (Join-Path $pluginRoot 'SQLite-net.dll')
    Copy-PackageFile $assets 'SQLitePCLRaw.core' 'lib\netstandard2.0\SQLitePCLRaw.core.dll' `
        (Join-Path $pluginRoot 'SQLitePCLRaw.core.dll')
    Copy-PackageFile $assets 'SQLitePCLRaw.provider.e_sqlite3' 'lib\netstandard2.0\SQLitePCLRaw.provider.e_sqlite3.dll' `
        (Join-Path $pluginRoot 'SQLitePCLRaw.provider.e_sqlite3.dll')
    Copy-PackageFile $assets 'SourceGear.sqlite3' 'runtimes\win-x64\native\e_sqlite3.dll' `
        (Join-Path $pluginRoot 'x86_64\e_sqlite3.dll')
    Copy-PackageFile $assets 'SourceGear.sqlite3' 'runtimes\android-arm64\native\libe_sqlite3.so' `
        (Join-Path $pluginRoot 'Android\arm64-v8a\libe_sqlite3.so')

    npm pack $upmRoot --pack-destination $upmOutput
    if ($LASTEXITCODE -ne 0) { throw 'Packing the UPM package failed.' }

    Write-Host "Prepared AutomaTable $runtimeVersion release artifacts:"
    Get-ChildItem -LiteralPath $packageOutput, $upmOutput -File |
        Where-Object { $_.Name -match [regex]::Escape($runtimeVersion) } |
        Get-FileHash -Algorithm SHA256 |
        Select-Object Path, Hash |
        Format-Table -AutoSize
}
finally {
    Pop-Location
}
