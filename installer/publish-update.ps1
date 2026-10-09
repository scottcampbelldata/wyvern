# Puts a built setup program on the update server, signed, so that every installed Beacon
# finds it. Run it after installer\build-installer.ps1:
#
#   powershell -ExecutionPolicy Bypass -File installer\publish-update.ps1 -Server my-server
#
# What it does:
#
#   1. Takes dist\BeaconSetup-<version>.exe, the version being the one in installer\version.txt
#      unless -Version names another.
#   2. Writes "latest.json": the version, the file's name, its size and its SHA-256.
#   3. Signs exactly those bytes with this machine's signing key (installer\new-update-key.ps1
#      made it) and checks the signature against the public half that is built into Beacon.
#   4. Copies the setup program to the server first, and only then the two small files, each
#      under another name that is given its real name at the end: a Beacon that looks while
#      this runs sees the old update whole or the new one whole.
#   5. Keeps the three newest setup programs on the server and removes older ones.
#
# The server is reached with ssh, by the name this machine's ssh configuration knows it by.
# Nothing here holds its address.
param(
    [string]$Version,
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$Folder = 'beacon-updates'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $Version = (Get-Content (Join-Path $PSScriptRoot 'version.txt') -Raw).Trim()
}
if ($Version -notmatch '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$') {
    throw "'$Version' is not a version of three numbers."
}
if ($Folder -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'The folder on the server is one plain name.'
}

$name = "BeaconSetup-$Version.exe"
$setup = Join-Path $root "dist\$name"
if (-not (Test-Path $setup)) {
    throw "$setup is not there. Build it first with installer\build-installer.ps1."
}

$channelFile = Join-Path $PSScriptRoot 'update-channel.json'
$keyFile = Join-Path $env:LOCALAPPDATA 'BeaconRelease\update-signing-key.bin'
if (-not (Test-Path $channelFile) -or -not (Test-Path $keyFile)) {
    throw 'This machine is not set up to sign updates. Run installer\new-update-key.ps1 first.'
}

# The manifest, in the one plain form Beacon reads.
$size = (Get-Item $setup).Length
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = '{"version":"' + $Version + '","file":"' + $name + '","size":' + $size + ',"sha256":"' + $hash + '"}'
$manifestBytes = [Text.Encoding]::UTF8.GetBytes($manifest)

# Signed with the key that only this Windows user on this machine can use.
$purpose = [Text.Encoding]::UTF8.GetBytes('Beacon update signing key v1')
$private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile), $purpose, 'CurrentUser')
$key = [Security.Cryptography.CngKey]::Import($private, [Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
[Array]::Clear($private, 0, $private.Length)
$signer = New-Object Security.Cryptography.ECDsaCng($key)
$signer.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
$signature = $signer.SignData($manifestBytes)
if ($signature.Length -ne 64) {
    throw 'The signature does not have the size of a P-256 signature.'
}

# Checked against the public half that the builds carry: a signature that the key in
# update-channel.json does not accept would be refused by every Beacon.
$built = (Get-Content $channelFile -Raw | ConvertFrom-Json).publicKey
$blob = New-Object byte[] 72
[Array]::Copy([byte[]](0x45, 0x43, 0x53, 0x31, 0x20, 0, 0, 0), $blob, 8)
[Array]::Copy([Convert]::FromBase64String($built), 0, $blob, 8, 64)
$checker = New-Object Security.Cryptography.ECDsaCng([Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))
$checker.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
if (-not $checker.VerifyData($manifestBytes, $signature)) {
    throw 'The signing key on this machine is not the one whose public half is in installer\update-channel.json. Nothing was published.'
}

$work = Join-Path $PSScriptRoot 'obj\update'
New-Item -ItemType Directory -Force $work | Out-Null
$plain = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $work 'latest.json'), $manifest, $plain)
[IO.File]::WriteAllText((Join-Path $work 'latest.json.sig'), [Convert]::ToBase64String($signature), $plain)

# To the server: the large file first, then the two small ones, each under a name of its own
# until it is whole.
$ssh = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=20')
& scp @ssh -q $setup "${Server}:$Folder/$name.incoming"
if ($LASTEXITCODE -ne 0) { throw 'The setup program could not be copied to the server.' }
& scp @ssh -q (Join-Path $work 'latest.json') "${Server}:$Folder/latest.json.incoming"
if ($LASTEXITCODE -ne 0) { throw 'latest.json could not be copied to the server.' }
& scp @ssh -q (Join-Path $work 'latest.json.sig') "${Server}:$Folder/latest.json.sig.incoming"
if ($LASTEXITCODE -ne 0) { throw 'latest.json.sig could not be copied to the server.' }

$there = "cd -- '$Folder' && test `$(sha256sum '$name.incoming' | cut -c1-64) = '$hash' && chmod 644 '$name.incoming' latest.json.incoming latest.json.sig.incoming && mv -f '$name.incoming' '$name' && mv -f latest.json.sig.incoming latest.json.sig && mv -f latest.json.incoming latest.json && ls -1t BeaconSetup-*.exe | tail -n +4 | xargs -r rm -f -- && echo published"
$answer = & ssh @ssh $Server $there
if ($LASTEXITCODE -ne 0 -or $answer -ne 'published') {
    throw 'The files reached the server but could not be put in place there. The update that was there before is still the one on offer.'
}

"Published: Beacon $Version ($name, $size bytes, SHA-256 $hash)"
