# Shared reverse-peer gate: TWP ÷ closest peer.
# Candidates are YARP, nginx, HAProxy and Envoy arms that share the YARP suffix
# (yarp-reverse-http2 -> nginx-reverse-http2, and the same for haproxy/envoy).
# Closest means the smallest |peer − TWP| among SLO-pass sustains > 0.
# A distance tie uses the higher peer sustain, so the ratio is the stricter one.
# Absent peers (no Windows HAProxy/Envoy, no H2 upstream for nginx) are skipped.

function Get-PeerArmCandidates([string] $YarpArm) {
    if ([string]::IsNullOrWhiteSpace($YarpArm)) { return @() }
    if (-not $YarpArm.StartsWith('yarp-')) { return @($YarpArm) }
    $suffix = $YarpArm.Substring(5)
    return @($YarpArm, "nginx-$suffix", "haproxy-$suffix", "envoy-$suffix")
}

function Get-ClosestPeer([hashtable] $Sustain, [double] $TwpSustain, [string] $YarpArm) {
    $best = $null
    foreach ($arm in (Get-PeerArmCandidates $YarpArm)) {
        if (-not $Sustain.ContainsKey($arm)) { continue }
        $value = [double]$Sustain[$arm]
        if ($value -le 0) { continue }
        $dist = [math]::Abs($value - $TwpSustain)
        $closer = $null -eq $best -or $dist -lt ($best.Dist - 0.0001)
        $tiedStricter = $null -ne $best -and [math]::Abs($dist - $best.Dist) -le 0.0001 -and $value -gt $best.Sustain
        if ($closer -or $tiedStricter) {
            $best = [pscustomobject]@{
                Arm     = $arm
                Sustain = $value
                Dist    = $dist
                Ratio   = $TwpSustain / $value
            }
        }
    }
    return $best
}
