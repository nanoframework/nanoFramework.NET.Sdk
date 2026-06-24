[CmdletBinding()]
param(
    [string]$Project = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-MSBuild {
    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw "Unable to locate msbuild.exe. Install Visual Studio Build Tools, or run from a Developer PowerShell where msbuild.exe is on PATH."
    }

    $msbuildPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($msbuildPath)) {
        throw "Unable to locate msbuild.exe with vswhere. Install Visual Studio Build Tools, or run from a Developer PowerShell where msbuild.exe is on PATH."
    }

    return $msbuildPath
}

if ([string]::IsNullOrWhiteSpace($Project)) {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw 'Unable to resolve script path. Pass -Project explicitly.'
    }

    $scriptDirectory = Split-Path -Parent $scriptPath
    $Project = Join-Path $scriptDirectory 'SmokeTest.csproj'
}

if (-not (Test-Path -LiteralPath $Project)) {
    throw "Project not found: $Project"
}

function Build-And-CaptureNanoResources {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('dotnet', 'msbuild')]
        [string]$Toolchain,
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,
        [Parameter(Mandatory = $true)]
        [string]$BuildConfiguration,
        [Parameter(Mandatory = $true)]
        [string]$CaptureDirectory,
        [string]$MSBuildPath
    )

    $projectDirectory = Split-Path -Parent $ProjectPath
    $resourceDirectory = Join-Path $projectDirectory "obj\$BuildConfiguration\netnano1.0"

    if (Test-Path -LiteralPath $resourceDirectory) {
        Get-ChildItem -LiteralPath $resourceDirectory -Filter '*.nanoresources' -File | Remove-Item -Force
    }

    if ($Toolchain -eq 'dotnet') {
        & dotnet build $ProjectPath -c $BuildConfiguration /nologo /v:minimal
        $exitCode = $LASTEXITCODE
    }
    else {
        & $MSBuildPath $ProjectPath /t:Build /p:Configuration=$BuildConfiguration /nologo /verbosity:minimal
        $exitCode = $LASTEXITCODE
    }

    if (-not (Test-Path -LiteralPath $resourceDirectory)) {
        throw "Expected intermediate output directory was not produced: $resourceDirectory"
    }

    $resourceFiles = @(Get-ChildItem -LiteralPath $resourceDirectory -Filter '*.nanoresources' -File)
    if ($exitCode -ne 0 -and $resourceFiles.Count -gt 0) {
        Write-Warning "$Toolchain build returned exit code $exitCode, but .nanoresources files were generated. Continuing parity check."
    }
    elseif ($exitCode -ne 0) {
        throw "$Toolchain build failed with exit code $exitCode."
    }

    if ($resourceFiles.Count -eq 0) {
        throw "No .nanoresources files were produced under: $resourceDirectory"
    }

    foreach ($file in $resourceFiles) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $CaptureDirectory $file.Name) -Force
    }
}

$resolvedProject = (Resolve-Path -LiteralPath $Project).Path
$msbuild = Find-MSBuild
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("nf-smoketest-parity-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $tempRoot -Force

try {
    $coreCapture = Join-Path $tempRoot 'net8.0'
    $frameworkCapture = Join-Path $tempRoot 'net472'
    $null = New-Item -ItemType Directory -Path $coreCapture -Force
    $null = New-Item -ItemType Directory -Path $frameworkCapture -Force

    Build-And-CaptureNanoResources -Toolchain dotnet -ProjectPath $resolvedProject -BuildConfiguration $Configuration -CaptureDirectory $coreCapture
    Build-And-CaptureNanoResources -Toolchain msbuild -ProjectPath $resolvedProject -BuildConfiguration $Configuration -CaptureDirectory $frameworkCapture -MSBuildPath $msbuild

    $coreFiles = Get-ChildItem -LiteralPath $coreCapture -Filter '*.nanoresources' -File | Sort-Object -Property Name
    $frameworkFiles = Get-ChildItem -LiteralPath $frameworkCapture -Filter '*.nanoresources' -File | Sort-Object -Property Name

    $coreNames = @($coreFiles.Name)
    $frameworkNames = @($frameworkFiles.Name)
    if (@(Compare-Object -ReferenceObject $coreNames -DifferenceObject $frameworkNames).Count -ne 0) {
        throw "Parity check failed: generated .nanoresources file sets differ.`nnet8.0 : $($coreNames -join ', ')`nnet472 : $($frameworkNames -join ', ')"
    }

    $mismatches = @()
    foreach ($name in $coreNames) {
        $corePath = Join-Path $coreCapture $name
        $frameworkPath = Join-Path $frameworkCapture $name

        $coreHash = Get-FileHash -LiteralPath $corePath -Algorithm SHA256
        $frameworkHash = Get-FileHash -LiteralPath $frameworkPath -Algorithm SHA256

        Write-Host "$name"
        Write-Host "  net8.0:  $($coreHash.Hash)"
        Write-Host "  net472:  $($frameworkHash.Hash)"

        if ($coreHash.Hash -ne $frameworkHash.Hash) {
            $mismatches += $name
        }
    }

    if ($mismatches.Count -gt 0) {
        throw "Parity check failed: hash mismatch in .nanoresources files: $($mismatches -join ', ')"
    }

    Write-Host 'Parity check passed: all .nanoresources files are byte-identical for net8.0 and net472.'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
