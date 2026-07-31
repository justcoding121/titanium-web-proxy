# 
# There are several ways to install a service on windows, this methods uses PowerShell.
#
# Publish the service first, e.g. from this directory:
#   dotnet publish -c Release -r win-x64 --self-contained false -o publish
# then run this script from the publish output directory (or update $ServiceExePath below).
#

# Self-elevate the script if required.
if (-Not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] 'Administrator')) {
	if ([int](Get-CimInstance -Class Win32_OperatingSystem | Select-Object -ExpandProperty BuildNumber) -ge 6000) {
		$CommandLine = "-File `"" + $MyInvocation.MyCommand.Path + "`" " + $MyInvocation.UnboundArguments
		Start-Process -FilePath PowerShell.exe -Verb Runas -ArgumentList $CommandLine
		Exit
	}
}

# Internal SCM name — must match AddWindowsService(...) in Program.cs.
[String] $ServiceName = "TitaniumWebProxy"
# Friendly name shown in services.msc.
[String] $ServiceDisplayName = "Titanium Web Proxy"
# This is the name of the executable of the service (from `dotnet publish`).
[String] $ServiceExeName = "Titanium.Web.Proxy.Examples.WindowsService.exe"
# Use the directory of the running script and the service executable name to create a full path.
[String] $ServiceExePath = [string]($PSScriptRoot) + "\" + $ServiceExeName
# Get the information for the executable file.
[IO.FileInfo] $ExeFileInfo = $ServiceExePath

# Check if the executable file exists.
if(!$ExeFileInfo.Exists) {
	# OH NO the executable was not found.
	Write-host "Service executable not found $ServiceExePath"
	Write-Host "Publish the project first (dotnet publish -c Release -r win-x64 --self-contained false -o publish) and run this script from the publish output."

}else{
	# Lets install the service.
	Write-host "Installing service $ServiceExePath"
	New-Service -Name $ServiceName -DisplayName $ServiceDisplayName -BinaryPathName $ServiceExePath -Description "Titanium Web Proxy HTTP(S) reverse/explicit proxy example service" -StartupType "Automatic"
	# Service installed, lets start it.
	Start-Service -Name $ServiceName
}

Read-Host -Prompt "Press Enter to exit"
