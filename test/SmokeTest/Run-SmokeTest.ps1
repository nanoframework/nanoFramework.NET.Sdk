[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$PackageDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryDirectory = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$smokeTestProject = Join-Path $PSScriptRoot 'SmokeTest.csproj'
$sdkProject = Join-Path $repositoryDirectory 'nanoFramework.NET.Sdk\nanoFramework.NET.Sdk.csproj'
$globalJsonPath = Join-Path $PSScriptRoot 'global.json'
$sdkResolverConfigPath = Join-Path $PSScriptRoot 'NuGet.Config'
$nugetConfigPath = Join-Path $PSScriptRoot 'NuGet.Azdo.Config'
$globalJsonExisted = Test-Path -LiteralPath $globalJsonPath
$sdkResolverConfigExisted = Test-Path -LiteralPath $sdkResolverConfigPath
$originalNuGetPackages = $env:NUGET_PACKAGES
$isolatedNuGetPackages = Join-Path ([System.IO.Path]::GetTempPath()) ('nf-smoketest-nuget-' + [Guid]::NewGuid().ToString('N'))
$globalJsonBackup = if ($globalJsonExisted) {
    [System.IO.File]::ReadAllBytes($globalJsonPath)
}
else {
    $null
}
$sdkResolverConfigBackup = if ($sdkResolverConfigExisted) {
    [System.IO.File]::ReadAllBytes($sdkResolverConfigPath)
}
else {
    $null
}

$null = New-Item -ItemType Directory -Path $PackageDirectory -Force
$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path

Push-Location $PSScriptRoot
try {
    & dotnet pack $sdkProject --configuration $Configuration --output $resolvedPackageDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Packing the SDK failed with exit code $LASTEXITCODE."
    }

    & (Join-Path $PSScriptRoot 'Create-NuGetConfig.ps1') `
        -PackageDirectory $resolvedPackageDirectory `
        -SmokeTestDirectory $PSScriptRoot `
        -UsePackageVersion `
        -ForceGlobalJson `
        -ForceNuGetConfig

    $env:NUGET_PACKAGES = $isolatedNuGetPackages

    & dotnet restore $smokeTestProject --configfile $nugetConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw "Restoring SmokeTest failed with exit code $LASTEXITCODE."
    }

    $sdkPackage = Get-ChildItem -LiteralPath $resolvedPackageDirectory -Filter 'nanoFramework.NET.Sdk.*.nupkg' -File |
        Where-Object { $_.Name -notlike '*.snupkg' -and $_.Name -notlike '*.symbols.nupkg' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    $sdkVersionMatch = [regex]::Match($sdkPackage.BaseName, '^nanoFramework\.NET\.Sdk\.(.+)$')
    if (-not $sdkVersionMatch.Success) {
        throw "Couldn't infer SDK version from package '$($sdkPackage.Name)'."
    }

    $sdkVersion = $sdkVersionMatch.Groups[1].Value
    $resolvedSdkPackageDirectory = Join-Path $isolatedNuGetPackages "nanoframework.net.sdk\$sdkVersion"
    $resolvedSdkPackage = Join-Path $resolvedSdkPackageDirectory "nanoframework.net.sdk.$sdkVersion.nupkg"
    if (-not (Test-Path -LiteralPath $resolvedSdkPackage -PathType Leaf)) {
        throw "The freshly packed SDK wasn't restored into the isolated NuGet cache: $resolvedSdkPackage"
    }

    $packedHash = (Get-FileHash -LiteralPath $sdkPackage.FullName -Algorithm SHA256).Hash
    $restoredHash = (Get-FileHash -LiteralPath $resolvedSdkPackage -Algorithm SHA256).Hash
    if ($packedHash -ne $restoredHash) {
        throw "The SDK package in the isolated NuGet cache doesn't match the freshly packed package '$($sdkPackage.Name)'."
    }

    Write-Host "Verified SmokeTest uses freshly packed SDK package: $($sdkPackage.Name) (SHA256 $packedHash)"

    & dotnet build $smokeTestProject --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Building SmokeTest failed with exit code $LASTEXITCODE."
    }

    & (Join-Path $PSScriptRoot 'Verify-Parity.ps1') `
        -Project $smokeTestProject `
        -Configuration $Configuration
}
finally {
    try {
        if ($null -eq $originalNuGetPackages) {
            Remove-Item Env:\NUGET_PACKAGES -ErrorAction SilentlyContinue
        }
        else {
            $env:NUGET_PACKAGES = $originalNuGetPackages
        }
    }
    finally {
        try {
            if (Test-Path -LiteralPath $isolatedNuGetPackages) {
                Remove-Item -LiteralPath $isolatedNuGetPackages -Recurse -Force
            }
        }
        finally {
            try {
                if ($globalJsonExisted) {
                    [System.IO.File]::WriteAllBytes($globalJsonPath, $globalJsonBackup)
                }
                elseif (Test-Path -LiteralPath $globalJsonPath) {
                    Remove-Item -LiteralPath $globalJsonPath -Force
                }
            }
            finally {
                try {
                    if ($sdkResolverConfigExisted) {
                        [System.IO.File]::WriteAllBytes($sdkResolverConfigPath, $sdkResolverConfigBackup)
                    }
                    elseif (Test-Path -LiteralPath $sdkResolverConfigPath) {
                        Remove-Item -LiteralPath $sdkResolverConfigPath -Force
                    }
                }
                finally {
                    Pop-Location
                }
            }
        }
    }
}
