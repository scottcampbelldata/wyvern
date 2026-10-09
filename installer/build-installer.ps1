# Builds dist\WyvernSetup-<version>.exe: publishes Wyvern (Release, not packed with .NET) and
# packs the result with Inno Setup 6. Run from anywhere:
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#
# Every build gets a version of its own. installer\version.txt holds the version of the last
# setup program that was built; this script counts its last number up by one (1.1.4 becomes
# 1.1.5), or with -Minor its middle number (1.1.4 becomes 1.2.0), writes the new version back,
# and stamps it on Wyvern.exe, on the setup program and on its file name. Commit version.txt
# with the build.
#
# -Test builds installer\obj\test\WyvernSetupTest.exe instead: the same setup under another
# identity, for trying an install into a throwaway folder without touching a real one. It
# takes the version as it stands and does not count it up.
param([switch]$Test, [switch]$Minor)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $PSScriptRoot 'obj\publish'
$versionFile = Join-Path $PSScriptRoot 'version.txt'

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. It is at https://jrsoftware.org/isinfo.php'
}

$last = [version](Get-Content $versionFile -Raw).Trim()
if ($Test) {
    $version = '{0}.{1}.{2}' -f $last.Major, $last.Minor, $last.Build
} elseif ($Minor) {
    $version = '{0}.{1}.0' -f $last.Major, ($last.Minor + 1)
} else {
    $version = '{0}.{1}.{2}' -f $last.Major, $last.Minor, ($last.Build + 1)
}

if (Test-Path $publish) {
    Remove-Item -Recurse -Force $publish
}

dotnet publish (Join-Path $root 'src\Wyvern.App') -c Release -o $publish "-p:Version=$version"
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

if ($Test) {
    $out = Join-Path $PSScriptRoot 'obj\test'
    $defines = @('/DTestBuild', '/DOutputName=WyvernSetupTest')
} else {
    $out = Join-Path $root 'dist'
    $defines = @("/DOutputName=WyvernSetup-$version")
}

& $iscc "/DSourceDir=$publish" "/DAppVersion=$version" "/DOutputDir=$out" @defines (Join-Path $PSScriptRoot 'Wyvern.iss')
if ($LASTEXITCODE -ne 0) {
    throw 'The Inno Setup compiler failed.'
}

if (-not $Test) {
    # Only a build that worked counts.
    Set-Content -Path $versionFile -Value $version -NoNewline -Encoding ascii
}

$built = (Get-Item (Join-Path $publish 'Wyvern.dll')).VersionInfo.ProductVersion
Get-ChildItem $out -Filter '*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object {
    '{0}  ({1:N1} MB, version {2}, built from {3})' -f $_.FullName, ($_.Length / 1MB), $version, $built
}
