param([switch]$ReleaseConfiguration, [switch]$OnlyPack)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "true"
$env:DOTNET_DISABLE_WORKLOAD_UPDATES = "true"

# Set configuration to Release if the switch is provided
$configuration = "Debug"
if( $ReleaseConfiguration -eq $True )
{
    $configuration = "Release"
}

# Navigate to the repository root
Set-Location $PSScriptRoot > $null
Set-Location -Path ../.. -PassThru > $null

# Generate build version with timestamp
$version = Get-Content .\.version
$currentTime = Get-Date -UFormat "%y%m%d%H%M%S"
$buildNumber = "$version"

Write-Host "build project with version: $version"

# Find all .csproj files (excluding those in /obj folders) and pack them
$projects = Get-ChildItem -Recurse -Filter *.csproj | Where-Object { $_.FullName -notmatch '\\obj\\'-and $_.Name -notmatch '\.Test(s)?\.csproj$' }

foreach ($proj in $projects) {
    Write-Host "build: $proj"
    dotnet pack $proj.FullName `
        -c $configuration `
        -v minimal `
        -o ./temp `
        -p:PackageVersion=$buildNumber `
        -p:Version=$buildNumber `
        -p:IncludeSymbols=false
}

# Get global NuGet cache path
$nugetGlobalPath = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*',''

# If not OnlyPack, copy the packages to the local development feed
if( $OnlyPack -eq $False )
{
    $localFeedPath = $env:LOCAL_DEV_NUGET_FEED_PATH
    if (-not (Test-Path $localFeedPath)) {
        Write-Error "LOCAL_DEV_NUGET_FEED_PATH is not set or does not exist."
        exit 1
    }

    # Copy .nupkg files to local NuGet feed directory
    $packages = Get-ChildItem -Path ./temp -Filter "*.nupkg"

    # Delete old versions of these packages from LocalDevFeed and Global
    foreach ($pkg in $packages) {
        # packageId is everything before the first version number separator
        $packageId = $packageId = $pkg.BaseName -replace '\.\d+(\.\d+)*(-[A-Za-z0-9\-\.]+)?$', ''
        Write-Host "$packageId"

        # find all packages with same prefix in the feed and delete them
        $oldPackages = Get-ChildItem -Path $localFeedPath -Filter "$packageId*.nupkg"
        foreach ($old in $oldPackages) {
            Write-Host "`tRemove from local feed: $old"
            Remove-Item $old.FullName -Force
        }

        # Delete package from global cache
        $pkgCachePath = Join-Path $nugetGlobalPath $packageId
        Write-Host "`tRemove nuget cache: $pkgCachePath"
        if (Test-Path $pkgCachePath) {
            Remove-Item $pkgCachePath -Recurse -Force
        }
        Write-Host ""
    }

    Write-Host "Publishing package to local dev feed: $localFeedPath"
    foreach ($pkg in $packages) {
        Write-Host "`t$pkg"
        dotnet nuget add source $localFeedPath -n LocalDev -p -c --store-password-in-clear-text -NonInteractive -Force | Out-Null
        Copy-Item $pkg.FullName -Destination $localFeedPath > $null
    }

    # Clean up temp folder
    Remove-Item ./temp -Recurse -Force > $null
}

# Navigate back to the repository root
Set-Location $PSScriptRoot > $null

