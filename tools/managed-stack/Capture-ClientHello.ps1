# Captures the first TLS ClientHello (TCP) or the first QUIC datagrams from a browser.
# Only the chrome launch command is filled in. Another browser is a new entry in $launchers.
param(
    [ValidateSet('chrome')]
    [string] $Browser = 'chrome',
    [ValidateSet('tcp', 'udp')]
    [string] $Transport = 'tcp',
    [int] $Count = 50,
    [string] $OutDir = ''
)

$ErrorActionPreference = 'Stop'
$chrome = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "$env:LocalAppData\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw 'Chrome is not installed.' }

$versionLine = (& $chrome --version | Out-String).Trim()
if ($versionLine -notmatch '(\d+\.\d+\.\d+\.\d+)') { throw "Could not read a Chrome version from: $versionLine" }
$version = $Matches[1]

if (-not $OutDir) {
    $OutDir = Join-Path $PSScriptRoot "..\..\tests\Titanium.Tls.Tests\Fixtures\captures\$Browser\$version"
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$bucket = Join-Path $OutDir $Transport
New-Item -ItemType Directory -Force -Path $bucket | Out-Null

function Read-Exact([System.IO.Stream] $stream, [byte[]] $buffer) {
    $got = 0
    while ($got -lt $buffer.Length) {
        $n = $stream.Read($buffer, $got, $buffer.Length - $got)
        if ($n -le 0) { throw 'The browser closed before the record was complete.' }
        $got += $n
    }
}

function Save-TcpHello([int] $index) {
    $saved = $false
    for ($attempt = 1; $attempt -le 3 -and -not $saved; $attempt++) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $profile = Join-Path $env:TEMP ("twp-chrome-" + [guid]::NewGuid().ToString('n'))
        $proc = $null
        try {
            $accept = $listener.AcceptTcpClientAsync()
            $args = @(
                "--user-data-dir=$profile",
                '--no-first-run',
                '--no-default-browser-check',
                '--disable-sync',
                '--disable-background-networking',
                '--ignore-certificate-errors',
                "https://localhost:$port/"
            )
            $proc = Start-Process -FilePath $chrome -ArgumentList $args -PassThru -WindowStyle Minimized
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while ([DateTime]::UtcNow -lt $deadline -and -not $saved) {
                $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
                if ($left -le 0 -or -not $accept.Wait($left)) { break }
                $tcp = $accept.Result
                try {
                    $stream = $tcp.GetStream()
                    $stream.ReadTimeout = 3000
                    $header = New-Object byte[] 5
                    Read-Exact $stream $header
                    $length = ([int]$header[3] -shl 8) -bor [int]$header[4]
                    if ($header[0] -eq 22 -and $length -gt 0 -and $length -le 16384) {
                        $body = New-Object byte[] $length
                        Read-Exact $stream $body
                        $record = New-Object byte[] (5 + $length)
                        [Array]::Copy($header, $record, 5)
                        [Array]::Copy($body, 0, $record, 5, $length)
                        $path = Join-Path $bucket ("hello-{0:d3}.bin" -f $index)
                        [System.IO.File]::WriteAllBytes($path, $record)
                        $saved = $true
                    }
                }
                catch { }
                finally { $tcp.Dispose() }
                if (-not $saved) { $accept = $listener.AcceptTcpClientAsync() }
            }
        }
        finally {
            $listener.Stop()
            if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
            Get-Process chrome -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-2) } | Out-Null
            if (Test-Path $profile) { Remove-Item -Recurse -Force $profile -ErrorAction SilentlyContinue }
        }
    }

    if (-not $saved) { throw "Chrome did not send a ClientHello for capture $index." }
}

function Save-UdpDatagrams([int] $index) {
    $udp = [System.Net.Sockets.UdpClient]::new([System.Net.IPAddress]::Loopback, 0)
    $port = ([System.Net.IPEndPoint]$udp.Client.LocalEndPoint).Port
    $profile = Join-Path $env:TEMP ("twp-chrome-quic-" + [guid]::NewGuid().ToString('n'))
    $proc = $null
    try {
        $udp.Client.ReceiveTimeout = 12000
        $args = @(
            "--user-data-dir=$profile",
            '--no-first-run',
            '--no-default-browser-check',
            '--disable-sync',
            '--enable-quic',
            '--host-resolver-rules=MAP localhost 127.0.0.1',
            "--origin-to-force-quic-on=localhost:$port",
            '--ignore-certificate-errors',
            "https://localhost:$port/"
        )
        $proc = Start-Process -FilePath $chrome -ArgumentList $args -PassThru -WindowStyle Minimized
        $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $packets = New-Object System.Collections.Generic.List[byte[]]
        $deadline = [DateTime]::UtcNow.AddSeconds(8)
        while ([DateTime]::UtcNow -lt $deadline -and $packets.Count -lt 4) {
            try { $packets.Add($udp.Receive([ref] $remote)) } catch { break }
        }
        if ($packets.Count -eq 0) { throw "Chrome sent no QUIC datagram for capture $index." }
        $path = Join-Path $bucket ("initial-{0:d3}.bin" -f $index)
        $ms = New-Object System.IO.MemoryStream
        $writer = New-Object System.IO.BinaryWriter($ms)
        foreach ($packet in $packets) {
            $writer.Write([int]$packet.Length)
            $writer.Write($packet)
        }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    }
    finally {
        $udp.Dispose()
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        if (Test-Path $profile) { Remove-Item -Recurse -Force $profile -ErrorAction SilentlyContinue }
    }
}

for ($i = 1; $i -le $Count; $i++) {
    Write-Host "$Transport $i/$Count"
    if ($Transport -eq 'tcp') { Save-TcpHello $i } else { Save-UdpDatagrams $i }
}

Write-Host "Wrote $Count $Transport captures for $versionLine to $bucket"
