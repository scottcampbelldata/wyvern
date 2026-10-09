# Sets this machine up as the one that signs Beacon's updates, and writes the file that tells
# a build where to look for them. Run it once, on the machine Beacon is built on:
#
#   powershell -ExecutionPolicy Bypass -File installer\new-update-key.ps1 -Url http://100.64.0.10:8795/
#
# What it makes:
#
#   %LOCALAPPDATA%\BeaconRelease\update-signing-key.bin
#       The signing key (ECDSA, P-256), protected by Windows so that only this Windows user on
#       this machine can use it. It is never shown and never leaves this machine. A key that is
#       there already is kept: this script never writes over one.
#
#   installer\update-channel.json
#       The address of the update server and the public half of the key. The build puts this
#       file into Beacon, which then looks for updates at that address and installs only what
#       is signed with that key. The file is not in the repository.
#
# The address has to be plain http to a machine inside the private network, written as numbers
# (100.64.0.0 to 100.127.255.255): Beacon refuses any other.
#
# If the key file is ever lost, updates signed with a new key are refused by every Beacon built
# with the old one: each PC then needs one install by hand of a build made with the new key.
param([Parameter(Mandatory = $true)][string]$Url)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

if ($Url -notmatch '^http://100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.[0-9]{1,3}\.[0-9]{1,3}(:[0-9]{1,5})?(/[A-Za-z0-9._/-]*)?$') {
    throw 'The address must be plain http to a machine written as numbers inside 100.64.0.0 to 100.127.255.255, such as http://100.64.0.10:8795/'
}
if (-not $Url.EndsWith('/')) {
    $Url = $Url + '/'
}

$folder = Join-Path $env:LOCALAPPDATA 'BeaconRelease'
$keyFile = Join-Path $folder 'update-signing-key.bin'
$purpose = [Text.Encoding]::UTF8.GetBytes('Beacon update signing key v1')

if (Test-Path $keyFile) {
    $private = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile), $purpose, 'CurrentUser')
    $key = [Security.Cryptography.CngKey]::Import($private, [Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    'The signing key that is there was kept.'
} else {
    $creation = New-Object Security.Cryptography.CngKeyCreationParameters
    $creation.ExportPolicy = [Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $key = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $null, $creation)
    $private = $key.Export([Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    New-Item -ItemType Directory -Force $folder | Out-Null
    [IO.File]::WriteAllBytes($keyFile, [Security.Cryptography.ProtectedData]::Protect($private, $purpose, 'CurrentUser'))
    'A new signing key was made.'
}
[Array]::Clear($private, 0, $private.Length)

# The public half as Windows gives it: eight bytes that say what it is, then the two
# coordinates of the point, 32 bytes each. Beacon wants the coordinates alone.
$blob = $key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
if ($blob.Length -ne 72) {
    throw 'The public half of the key does not have the size of a P-256 key.'
}
$publicKey = [Convert]::ToBase64String($blob, 8, 64)

$channel = Join-Path $PSScriptRoot 'update-channel.json'
$json = '{ "url": "' + $Url + '", "publicKey": "' + $publicKey + '" }'
[IO.File]::WriteAllText($channel, $json + "`n", (New-Object Text.UTF8Encoding($false)))

"Key file:     $keyFile"
"Channel file: $channel"
"Build Beacon again (installer\build-installer.ps1) for the channel to be in it."
