#!/usr/bin/env pwsh
<#
  Packs the SimulaSharp framework NuGet packages (+ .snupkg symbols) at the single version declared in
  Directory.Build.props, into an output folder. Point a consuming project's nuget.config at that folder
  (see nuget.config.sample) and reference the `SimulaSharp` meta-package.

  Usage:  pwsh -File pack.ps1 [-Output artifacts] [-Configuration Release]
#>
[CmdletBinding()]
param([string]$Output = "artifacts", [string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out  = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# Kernel/CodeGen/Hosting first, then the meta (its ProjectReferences become package dependencies).
$projects = @(
  "Kernel/Kernel.csproj",
  "CodeGen/SimulaSharp.CodeGen.csproj",
  "Hosting/SimulaSharp.Hosting.csproj",
  "SimulaSharp/SimulaSharp.csproj"
)

foreach ($p in $projects) {
  Write-Host "packing $p" -ForegroundColor DarkCyan
  dotnet pack (Join-Path $root $p) -c $Configuration -o $out --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "pack failed for $p" }
}

Write-Host "`nPacked to ${out}:" -ForegroundColor Cyan
Get-ChildItem "$out/*.nupkg" | Sort-Object Name | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Green }
