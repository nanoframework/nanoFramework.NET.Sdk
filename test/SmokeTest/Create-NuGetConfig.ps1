<#
.SYNOPSIS
Creates the SmokeTest SDK pin and NuGet configuration.

.DESCRIPTION
Finds the nanoFramework.NET.Sdk package in PackageDirectory and writes global.json,
NuGet.Azdo.Config, and the NuGet.Config used by the MSBuild SDK resolver to
SmokeTestDirectory. The local package source is restricted to the SDK package; all
other packages use nuget.org. Use Run-SmokeTest.ps1 for a local run that restores
the tracked global.json and NuGet.Config afterward.

.EXAMPLE
.\Create-NuGetConfig.ps1 -PackageDirectory .\artifacts -UsePackageVersion -ForceGlobalJson -ForceNuGetConfig

dotnet restore .\SmokeTest.csproj --configfile .\NuGet.Azdo.Config
dotnet build .\SmokeTest.csproj --configuration Release --no-restore
.\Verify-Parity.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [string]$SmokeTestDirectory = $PSScriptRoot,

    [string]$SdkVersion = '',

    [switch]$UsePackageVersion,

    [switch]$ForceGlobalJson,

    [switch]$ForceNuGetConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Package directory not found: $PackageDirectory"
}

if (-not (Test-Path -LiteralPath $SmokeTestDirectory -PathType Container)) {
    throw "SmokeTest directory not found: $SmokeTestDirectory"
}

$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$resolvedSmokeTestDirectory = (Resolve-Path -LiteralPath $SmokeTestDirectory).Path
$sdkPackage = Get-ChildItem -LiteralPath $resolvedPackageDirectory -Filter 'nanoFramework.NET.Sdk.*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.snupkg' -and $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $sdkPackage) {
    throw "Couldn't find nanoFramework.NET.Sdk package in $resolvedPackageDirectory."
}

$match = [regex]::Match($sdkPackage.BaseName, '^nanoFramework\.NET\.Sdk\.(.+)$')
if (-not $match.Success -or [string]::IsNullOrWhiteSpace($match.Groups[1].Value)) {
    throw "Couldn't infer SDK version from package '$($sdkPackage.Name)'."
}

if ($UsePackageVersion -and -not [string]::IsNullOrWhiteSpace($SdkVersion)) {
    throw 'Specify either -SdkVersion or -UsePackageVersion, not both.'
}

if ($UsePackageVersion) {
    $sdkVersion = $match.Groups[1].Value
}
elseif ([string]::IsNullOrWhiteSpace($SdkVersion)) {
    $sdkVersion = (& nbgv get-version --public-release -v NuGetPackageVersion).Trim()

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sdkVersion)) {
        throw "Couldn't determine SDK version via 'nbgv get-version'. Pass -SdkVersion or use -UsePackageVersion for local package testing."
    }
}
else {
    $sdkVersion = $SdkVersion
}

$escapedPackageDirectory = [System.Security.SecurityElement]::Escape($resolvedPackageDirectory)

$smokeGlobalJson = @(
    '{'
    '  "msbuild-sdks": {'
    "    ""nanoFramework.NET.Sdk"": ""$sdkVersion"""
    '  }'
    '}'
)

$nugetConfig = @(
    '<?xml version="1.0" encoding="utf-8"?>'
    '<configuration>'
    '  <packageSources>'
    '    <clear />'
    "    <add key=""local-sdk"" value=""$escapedPackageDirectory"" />"
    '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
    '  </packageSources>'
    '  <packageSourceMapping>'
    '    <packageSource key="local-sdk">'
    '      <package pattern="nanoFramework.NET.Sdk" />'
    '    </packageSource>'
    '    <packageSource key="nuget.org">'
    '      <package pattern="*" />'
    '    </packageSource>'
    '  </packageSourceMapping>'
    '</configuration>'
)

$globalJsonPath = Join-Path $resolvedSmokeTestDirectory 'global.json'
$existingSdkVersion = $null

if (Test-Path -LiteralPath $globalJsonPath) {
    $existingGlobalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $existingSdkVersion = $existingGlobalJson.'msbuild-sdks'.'nanoFramework.NET.Sdk'

    if ($existingSdkVersion -ne $sdkVersion -and -not $ForceGlobalJson) {
        throw "global.json already pins nanoFramework.NET.Sdk to '$existingSdkVersion'. Pass -ForceGlobalJson to replace it with '$sdkVersion'."
    }
}

$sdkResolverConfigPath = Join-Path $resolvedSmokeTestDirectory 'NuGet.Config'
if ((Test-Path -LiteralPath $sdkResolverConfigPath) -and -not $ForceNuGetConfig) {
    throw "NuGet.Config already exists. Pass -ForceNuGetConfig to replace it with the local SDK package source."
}

if (-not (Test-Path -LiteralPath $globalJsonPath) -or $existingSdkVersion -ne $sdkVersion -or $ForceGlobalJson) {
    $smokeGlobalJson | Set-Content -LiteralPath $globalJsonPath -Encoding utf8
}

$nugetConfig | Set-Content -LiteralPath (Join-Path $resolvedSmokeTestDirectory 'NuGet.Azdo.Config') -Encoding utf8
$nugetConfig | Set-Content -LiteralPath $sdkResolverConfigPath -Encoding utf8

Write-Host "Using SmokeTest SDK package: $($sdkPackage.Name)"
Write-Host "Using SmokeTest SDK version: $sdkVersion"
Write-Host "Created SmokeTest NuGet configurations in: $resolvedSmokeTestDirectory"
