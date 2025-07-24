param([switch]$ReleaseConfiguration, [switch]$OnlyPack)

# Set configuration to Release if the switch is provided
$configuration = "Debug"
if( $ReleaseConfiguration -eq $True )
{
    $configuration = "Release"
}

# Navigate to the repository root
Set-Location $PSScriptRoot
Set-Location -Path ../.. -PassThru

# Generate build version with timestamp
$version = Get-Content .\.version
$currentTime = Get-Date -UFormat "%y%m%d%H%M%S"
$buildNumber = "$version"

# Find all .csproj files (excluding those in /obj folders) and pack them
$projects = Get-ChildItem -Recurse -Filter *.csproj | Where-Object { $_.FullName -notmatch '\\obj\\' }

foreach ($proj in $projects) {
    dotnet pack $proj.FullName `
        -c $configuration `
        -o ./temp `
        -p:PackageVersion=$buildNumber `
        -p:Version=$buildNumber `
        -p:IncludeSymbols=false
}

# If not OnlyPack, copy the packages to the local development feed
if( $OnlyPack -eq $False )
{
    Write-Host "Publishing package to local dev feed"

    $localFeedPath = $env:LOCAL_DEV_NUGET_FEED_PATH
    if (-not (Test-Path $localFeedPath)) {
        Write-Error "LOCAL_DEV_NUGET_FEED_PATH is not set or does not exist."
        exit 1
    }

    # Copy .nupkg files to local NuGet feed directory
    $packages = Get-ChildItem -Path ./temp -Filter "*.nupkg"
    foreach ($pkg in $packages) {
        dotnet nuget add source $localFeedPath -n LocalDev -p -c --store-password-in-clear-text -NonInteractive -Force | Out-Null
        Copy-Item $pkg.FullName -Destination $localFeedPath
    }

    # Clean up temp folder
    Remove-Item ./temp -Recurse -Force
}
