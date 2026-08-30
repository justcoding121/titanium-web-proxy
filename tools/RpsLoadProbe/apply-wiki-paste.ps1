# Splice paste-compare-product-wiki.ps1 output into wiki/Performance.md product tables.
param(
    [string] $PasteFile = 'tools/RpsLoadProbe/results/wiki-paste-out.txt',
    [string] $WikiFile = 'wiki/Performance.md',
    [string] $HeadSha = 'df172718',
    [string] $PrimaryRunId = '33041445371'
)

$ErrorActionPreference = 'Stop'

$em = [char]0x2014
$mul = [char]0x00D7
$div = [char]0x00F7
$ge = [char]0x2265
$rarr = [char]0x2192
$endash = [char]0x2013

function Test-Utf8Mojibake([string]$text) {
    return $text -match ([string][char]0x00C2 + [char]0x00B7) -or
           $text -match ([string][char]0x00C3 + [char]0x2014) -or
           $text -match ([string][char]0x00C3 + [char]0x00B7) -or
           $text -match ([string][char]0x00F0 + [char]0x0178 + [char]0x00A5 + [char]0x2021)
}

$raw = [System.IO.File]::ReadAllText((Resolve-Path $PasteFile), [System.Text.Encoding]::UTF8)
if (Test-Utf8Mojibake $raw) {
    & "$PSScriptRoot/repair-wiki-mojibake.ps1" -WikiFile $PasteFile
    $raw = [System.IO.File]::ReadAllText((Resolve-Path $PasteFile), [System.Text.Encoding]::UTF8)
}

function Get-Section([string]$name) {
    $marker = "---$name---"
    $start = $raw.IndexOf($marker)
    if ($start -lt 0) { throw "Missing section $name" }
    $start += $marker.Length
    while ($start -lt $raw.Length -and ($raw[$start] -eq "`r" -or $raw[$start] -eq "`n")) { $start++ }
    $next = [regex]::Match($raw.Substring($start), '\r?\n---[A-Z_]+---')
    $end = if ($next.Success) { $start + $next.Index } else { $raw.Length }
    return $raw.Substring($start, $end - $start).TrimEnd()
}

$winRev = Get-Section 'WIN_REVERSE'
$winMitm = Get-Section 'WIN_MITM'
$linRev = Get-Section 'LIN_REVERSE'
$linMitm = Get-Section 'LIN_MITM'

$wiki = [System.IO.File]::ReadAllText((Resolve-Path $WikiFile), [System.Text.Encoding]::UTF8)

$runUrl = "https://github.com/justcoding121/titanium-web-proxy/actions/runs/$PrimaryRunId"
$productLine = "- Product refresh: ``compare-product`` @ ``$HeadSha`` $em [$PrimaryRunId]($runUrl). Heavier/saturation/tls:"
$wiki = [regex]::Replace($wiki, '- Product refresh:.*', [System.Text.RegularExpressions.MatchEvaluator]{ $productLine }, 1)

$winRevHeader = "Median of **3 repeats** on ``windows-latest`` (4 vCPU / 16 GiB). Bare reverse 5${mul}5 @ ``$HeadSha`` $em ``compare-product`` [$PrimaryRunId]($runUrl). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. Prefer TWP${div}peer ratios over absolute RPS. **RPS cells** include median RSS / CPU at the peak-RPS step as ``<br><sub>(MiB / CPU%)</sub>``. nginx terminate peers use ``keepalive 256`` + streaming buffers. Laptop High-perf / cool-paired numbers stay on the [local lab](Performance-Local-Lab)."

$linRevHeader = "Median of **3 repeats** on ``ubuntu-latest`` (4 vCPU / 16 GiB). Bare reverse 5${mul}5 @ ``$HeadSha`` $em ``compare-product`` [$PrimaryRunId]($runUrl). Warmup 2s / measure 8s; concurrency 8, 16, 32, 64. **Linux nginx is the authoritative nginx baseline.** nginx terminate peers use ``keepalive 256`` + streaming buffers. The RPS workflow installs nginx.org mainline (``http_v3_module``) and ``libmsquic``. Prefer ratios over absolute RPS."

$mitmNote = @(
    "Same Client${mul}Origin wires with interception on (``compare-product`` [$PrimaryRunId]($runUrl)). **Lite** = no-op handlers (unchanged-lite finish). **Full** = append-only header mutation (harness: one probe header each way; product: generic append-only relay via ``MitmCompressedRelayHelper``). nginx/YARP cannot MITM. **Lite${div}Reverse** / **Full${div}Reverse** vs bare reverse (same job). Completion gate: Lite and Full ${ge} **0.70${mul}** reverse sustain @ c=64 (median of 3 GHA runs)."
    ""
    "**v1 append-only relay (2026-08-27):** Pre-fix H2${rarr}H2 Full${div}Reverse was **0.13${endash}0.16${mul}** ([32960766249](https://github.com/justcoding121/titanium-web-proxy/actions/runs/32960766249)). Post-fix @ ``df172718``: H2 plain${rarr}H2 plain Full **0.77${endash}0.79${mul}**, H3${rarr}H1 Full **0.91${endash}0.93${mul}**, all MITM arms ${ge} **0.70${mul}** on median of [33041445371](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33041445371), [33055267086](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055267086), [33055272140](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33055272140)."
    ""
    "**v2 drop-only + non-unique append (2026-08-27):** ``MitmStaticRebuildHelper`` rebuilds static HPACK/QPACK after 1${endash}4 unique header drops; trailing non-unique appends stay on compressed relay. @ ``$HeadSha``: all MITM arms ${ge} **0.70${mul}** on GHA median ([33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622), [33105885748](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33105885748) Linux; [33087085235](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087085235), [33087088466](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087088466), [33087091622](https://github.com/justcoding121/titanium-web-proxy/actions/runs/33087091622) Windows). H2 plain${rarr}H2 plain Full **0.77${endash}0.79${mul}** (Win) / **0.78${mul}** (Lin)."
) -join "`n"

$winHdr = "## Windows $em Titanium vs nginx vs YARP"
$linHdr = "## Linux $em Titanium vs nginx vs YARP"

# Allow optional intro lines between section heading and ### Reverse (Windows has a Client/Origin blurb).
$wiki = [regex]::Replace($wiki,
    "(?s)($([regex]::Escape($winHdr))\r?\n(?:.*?\r?\n)?### Reverse\r?\n\r?\n).*?(?=\r?\n### MITM)",
    [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        $loadGen = "**Load generators:** Reverse inbound H3 arms use **``dotnet-httpclient``** (``http_version=3.0``, ``RequestVersionExact``). nginx/Windows is same-OS only (no QUIC)."
        $m.Groups[1].Value + $winRevHeader + "`n`n" + $loadGen + "`n`n" + $winRev + "`n"
    },
    1)

$wiki = [regex]::Replace($wiki,
    '(?s)(### MITM \(TWP only\)\r?\n\r?\n).*?(?=\r?\n## Linux)',
    [System.Text.RegularExpressions.MatchEvaluator]{
        param($m) $m.Groups[1].Value + $mitmNote + "`n`n" + $winMitm + "`n"
    },
    1)

$wiki = [regex]::Replace($wiki,
    "(?s)($([regex]::Escape($linHdr))\r?\n(?:.*?\r?\n)?### Reverse\r?\n\r?\n).*?(?=\r?\n### MITM)",
    [System.Text.RegularExpressions.MatchEvaluator]{
        param($m) $m.Groups[1].Value + $linRevHeader + "`n`n" + $linRev + "`n"
    },
    1)

$idx = $wiki.IndexOf($linHdr)
$tail = $wiki.Substring($idx)
$tail = [regex]::Replace($tail,
    '(?s)(### MITM \(TWP only\)\r?\n\r?\n).*?(?=\r?\n## Heavier)',
    [System.Text.RegularExpressions.MatchEvaluator]{
        param($m) $m.Groups[1].Value + $mitmNote + "`n`n" + $linMitm + "`n"
    },
    1)
$wiki = $wiki.Substring(0, $idx) + $tail

$utf8 = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Resolve-Path $WikiFile), $wiki, $utf8)
Write-Host "Updated $WikiFile"
