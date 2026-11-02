# Shared matchers for Titanium / TWP certificates that tests and the product
# leave in the Windows certificate stores. Dot-source this file.
# A certificate is a TWP certificate when its Subject or Issuer matches an
# anchored pattern below, with one extra rule for the Acme fixture issuer.

Set-StrictMode -Version 2.0

$script:TwpSubjectPatterns = @(
    '^CN=Titanium Root Certificate Authority$'
    '^CN=Titanium Integration Test Root CA$'
    '^CN=Titanium Integration Custom Root CA [0-9a-f]{32}$'
    '^CN=Titanium UnitTest Root CA [0-9a-f]{32}$'
    '^CN=Titanium Sonar Gate Cov CA$'
    '^CN=twp-(root|leaf|unit|dn)-[0-9a-f]{32}\.local$'
)

$script:TwpAcmeIssuerPattern = '^C=AU, ST=Victoria, L=Melbourne, O=Acme Corp, OU=Proxy, CN=Acme Root CA$'

$script:TwpStoreNames = @(
    'Root', 'My', 'CA', 'TrustedPeople', 'AuthRoot', 'Disallowed', 'TrustedPublisher'
)

function Test-TwpCertificateSubject {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    foreach ($pattern in $script:TwpSubjectPatterns) {
        if ($Value -match $pattern) { return $true }
    }
    if ($Value -match $script:TwpAcmeIssuerPattern) { return $true }
    return $false
}

function Test-TwpCertificate {
    param(
        [string]$Subject,
        [string]$Issuer
    )
    if (Test-TwpCertificateSubject $Subject) { return $true }
    # Acme-issued leaves are removed only when the subject is also a twp-* name,
    # or when the certificate is the fixture root itself (handled above).
    if ($Issuer -match $script:TwpAcmeIssuerPattern -and $Subject -match '^CN=twp-(root|leaf|unit|dn)-[0-9a-f]{32}\.local$') {
        return $true
    }
    if ($Issuer -match '^CN=Titanium Root Certificate Authority$' -and (Test-TwpCertificateSubject $Subject)) {
        return $true
    }
    if ($Issuer -match '^CN=Titanium Integration Test Root CA$' -and (Test-TwpCertificateSubject $Subject)) {
        return $true
    }
    if ($Issuer -match '^CN=Titanium UnitTest Root CA [0-9a-f]{32}$' -and (Test-TwpCertificateSubject $Subject)) {
        return $true
    }
    # Leaves issued by a product or test root whose own subject is a TWP name.
    if ($Issuer -match '^CN=Titanium Root Certificate Authority$' -or
        $Issuer -match '^CN=Titanium Integration Test Root CA$' -or
        $Issuer -match '^CN=Titanium Integration Custom Root CA [0-9a-f]{32}$' -or
        $Issuer -match '^CN=Titanium UnitTest Root CA [0-9a-f]{32}$' -or
        $Issuer -match '^CN=Titanium Sonar Gate Cov CA$' -or
        $Issuer -match '^CN=twp-root-[0-9a-f]{32}\.local$') {
        return $true
    }
    return $false
}

function Get-TwpCertificateScopes {
    param([ValidateSet('CurrentUser', 'LocalMachine', 'Both')][string]$Scope)
    if ($Scope -eq 'Both') { return @('CurrentUser', 'LocalMachine') }
    return @($Scope)
}
