# Summarizes captured ClientHello records: cipher suites, extension order, and which slots stay fixed.
param(
    [Parameter(Mandatory = $true)]
    [string] $Dir
)

$ErrorActionPreference = 'Stop'
$names = @{
    0 = 'server_name'; 5 = 'status_request'; 10 = 'supported_groups'; 11 = 'ec_point_formats'
    13 = 'signature_algorithms'; 16 = 'application_layer_protocol_negotiation'; 18 = 'signed_certificate_timestamp'
    21 = 'padding'; 23 = 'extended_master_secret'; 27 = 'compress_certificate'; 34 = 'delegated_credential'
    35 = 'session_ticket'; 41 = 'pre_shared_key'; 42 = 'early_data'; 43 = 'supported_versions'
    44 = 'cookie'; 45 = 'psk_key_exchange_modes'; 47 = 'certificate_authorities'; 49 = 'post_handshake_auth'
    50 = 'signature_algorithms_cert'; 51 = 'key_share'; 57 = 'quic_transport_parameters'
    17513 = 'application_settings'; 17613 = 'application_settings_new'; 65281 = 'renegotiation_info'
    65037 = 'encrypted_client_hello'
}

function Read-U16([byte[]] $b, [int] $o) { return ([int]$b[$o] -shl 8) -bor [int]$b[$o + 1] }
function Read-U24([byte[]] $b, [int] $o) { return ([int]$b[$o] -shl 16) -bor ([int]$b[$o + 1] -shl 8) -bor [int]$b[$o + 2] }

function Parse-Hello([byte[]] $record) {
    if ($record[0] -ne 22 -or $record[5] -ne 1) { throw 'Not a ClientHello record.' }
    $o = 9
    $legacy = Read-U16 $record $o; $o += 2
    $o += 32
    $sidLen = $record[$o]; $o += 1 + $sidLen
    $cipherLen = Read-U16 $record $o; $o += 2
    $ciphers = @()
    for ($i = 0; $i -lt $cipherLen; $i += 2) { $ciphers += Read-U16 $record ($o + $i) }
    $o += $cipherLen
    $compLen = $record[$o]; $o += 1 + $compLen
    $extLen = Read-U16 $record $o; $o += 2
    $end = $o + $extLen
    $exts = @()
    while ($o + 4 -le $end) {
        $type = Read-U16 $record $o
        $len = Read-U16 $record ($o + 2)
        $body = New-Object byte[] $len
        [Array]::Copy($record, $o + 4, $body, 0, $len)
        $exts += [pscustomobject]@{ Type = $type; Body = $body }
        $o += 4 + $len
    }
    [pscustomobject]@{ Legacy = $legacy; SessionIdLength = $sidLen; Ciphers = $ciphers; Extensions = $exts }
}

function Name-Of([int] $type) {
    if ($names.ContainsKey($type)) { return $names[$type] }
    if (($type -band 0x0f0f) -eq 0x0a0a) { return 'grease' }
    return ('0x{0:x4}' -f $type)
}

$files = Get-ChildItem -Path $Dir -Filter 'hello-*.bin' | Sort-Object Name
if ($files.Count -eq 0) { throw "No hello-*.bin files in $Dir" }
$parsed = foreach ($file in $files) { Parse-Hello ([System.IO.File]::ReadAllBytes($file.FullName)) }
$first = $parsed[0]
$extCount = $first.Extensions.Count
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("files=$($files.Count) legacy=0x$('{0:x4}' -f $first.Legacy) sessionId=$($first.SessionIdLength) extensions=$extCount")
$lines.Add('ciphers=' + (($first.Ciphers | ForEach-Object { '0x{0:x4}' -f $_ }) -join ','))
for ($slot = 0; $slot -lt $extCount; $slot++) {
    $at = @{}
    foreach ($hello in $parsed) {
        $name = Name-Of $hello.Extensions[$slot].Type
        if (-not $at.ContainsKey($name)) { $at[$name] = 0 }
        $at[$name]++
    }
    $dist = ($at.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
    $fixed = if ($at.Count -eq 1) { 'fixed' } else { 'moves' }
    $lines.Add(("slot {0:d2} {1} {2}" -f $slot, $fixed, $dist))
}

$groups = $first.Extensions | Where-Object { $_.Type -eq 10 } | Select-Object -First 1
if ($groups) {
    $g = @()
    $n = Read-U16 $groups.Body 0
    for ($i = 2; $i -lt 2 + $n; $i += 2) { $g += '0x{0:x4}' -f (Read-U16 $groups.Body $i) }
    $lines.Add('groups=' + ($g -join ','))
}
$shares = $first.Extensions | Where-Object { $_.Type -eq 51 } | Select-Object -First 1
if ($shares) {
    $s = @()
    $n = Read-U16 $shares.Body 0
    $i = 2
    while ($i + 4 -le $n + 2) {
        $group = Read-U16 $shares.Body $i
        $klen = Read-U16 $shares.Body ($i + 2)
        $s += ('0x{0:x4}:{1}' -f $group, $klen)
        $i += 4 + $klen
    }
    $lines.Add('key_shares=' + ($s -join ','))
}
$lines.Add('alpn-present=' + [bool]($first.Extensions | Where-Object { $_.Type -eq 16 }))
$lines.Add('ech-present=' + [bool]($first.Extensions | Where-Object { $_.Type -eq 65037 }))
$lines.Add('cert-compression-present=' + [bool]($first.Extensions | Where-Object { $_.Type -eq 27 }))
$lines.Add('alps-17513=' + [bool]($first.Extensions | Where-Object { $_.Type -eq 17513 }))
$lines.Add('alps-17613=' + [bool]($first.Extensions | Where-Object { $_.Type -eq 17613 }))
$sameGrease = 0
$hybridMatchesX = 0
foreach ($hello in $parsed) {
    $grease = @($hello.Extensions | Where-Object { (($_.Type -band 0x0f0f) -eq 0x0a0a) } | ForEach-Object { $_.Type })
    if ($grease.Count -ge 2 -and $grease[0] -ne $grease[1]) { $sameGrease++ }
    $share = $hello.Extensions | Where-Object { $_.Type -eq 51 } | Select-Object -First 1
    if (-not $share) { continue }
    $n = Read-U16 $share.Body 0
    $i = 2
    $xInHybrid = $null
    $xAlone = $null
    while ($i + 4 -le $n + 2) {
        $group = Read-U16 $share.Body $i
        $klen = Read-U16 $share.Body ($i + 2)
        if ($group -eq 0x11ec -and $klen -ge 32) {
            $xInHybrid = [Convert]::ToHexString($share.Body, $i + 4, 32)
        }
        if ($group -eq 0x001d -and $klen -eq 32) {
            $xAlone = [Convert]::ToHexString($share.Body, $i + 4, 32)
        }
        $i += 4 + $klen
    }
    if ($xInHybrid -and $xAlone -and $xInHybrid -eq $xAlone) { $hybridMatchesX++ }
}
$lines.Add("first-and-last-grease-differ=$sameGrease/$($parsed.Count)")
$lines.Add("hybrid-x25519-equals-separate-share=$hybridMatchesX/$($parsed.Count)")
$lines -join "`n"
