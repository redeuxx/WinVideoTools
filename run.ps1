<#
.SYNOPSIS
    Builds and launches WinVideoTools.
.EXAMPLE
    .\run.ps1
    .\run.ps1 -Configuration Release
    .\run.ps1 -NoBuild
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\WinVideoTools\WinVideoTools.csproj'

if (-not $NoBuild) {
    dotnet build $project -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }
}

# Ask MSBuild for the output path instead of hardcoding the TFM/RID folder layout.
$dll = dotnet msbuild $project -getProperty:TargetPath -p:Configuration=$Configuration
$exe = [IO.Path]::ChangeExtension($dll.Trim(), '.exe')
if (-not (Test-Path $exe)) { throw "App not found at $exe. Run without -NoBuild." }

Start-Process -FilePath $exe
