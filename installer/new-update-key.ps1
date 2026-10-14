# Keeps Wyvern's P-256 signing key in a private directory on the Ubuntu release server.
# Reuses an existing key. Only the public key is copied into this checkout.
#   powershell -ExecutionPolicy Bypass -File installer\new-update-key.ps1 -Server ubuntu -Url http://100.110.71.108:8795/wyvern/
param(
    [Parameter(Mandatory = $true)][string]$Url,
    [Parameter(Mandatory = $true)][string]$Server
)
$ErrorActionPreference = 'Stop'
if ($Url -notmatch '^http://100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.[0-9]{1,3}\.[0-9]{1,3}(:[0-9]{1,5})?(/[A-Za-z0-9._/-]*)?$') {
    throw 'The update URL must be inside the private network (100.64.0.0/10).'
}
if (-not $Url.EndsWith('/')) { $Url += '/' }
$ssh = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=20')
& ssh @ssh $Server 'umask 077; mkdir -p .local/share/wyvern-release; chmod 700 .local/share/wyvern-release'
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the private signing directory.' }
& scp @ssh -q (Join-Path $PSScriptRoot 'server-signing.py') "${Server}:.local/share/wyvern-release/server-signing.py"
if ($LASTEXITCODE -ne 0) { throw 'Could not copy the signing helper.' }
$publicKey = & ssh @ssh $Server 'python3 .local/share/wyvern-release/server-signing.py init'
if ($LASTEXITCODE -ne 0 -or [Convert]::FromBase64String($publicKey).Length -ne 64) {
    throw 'Could not initialize the server signing key.'
}
$channel = Join-Path $PSScriptRoot 'update-channel.json'
$json = @{ url = $Url; publicKey = $publicKey } | ConvertTo-Json
[IO.File]::WriteAllText($channel, $json + "`n", (New-Object Text.UTF8Encoding($false)))
"Private key remains on ${Server}:~/.local/share/wyvern-release/update-signing-key.pem"
"Public channel: $channel"
