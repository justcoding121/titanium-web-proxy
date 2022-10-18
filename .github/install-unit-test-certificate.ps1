$Here = "$(Split-Path -parent $MyInvocation.MyCommand.Definition)"
$certPath = "$Here\lib\rootCert.pfx"
$pfx = New-Object -TypeName System.Security.Cryptography.X509Certificates.X509Certificate2($certPath,"","Exportable,PersistKeySet")
$store = new-object System.Security.Cryptography.X509Certificates.X509Store([System.Security.Cryptography.X509Certificates.StoreName]::Root, "localmachine")
$store.open("MaxAllowed") 
$store.add($pfx) 
$store.close()