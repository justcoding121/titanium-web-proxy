# Self-elevate the script if required.
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] 'Administrator')) {
	if ([int](Get-CimInstance -Class Win32_OperatingSystem | Select-Object -ExpandProperty BuildNumber) -ge 6000) {
		$CommandLine = "-File `"" + $MyInvocation.MyCommand.Path + "`" " + $MyInvocation.UnboundArguments
		Start-Process -FilePath PowerShell.exe -Verb Runas -ArgumentList $CommandLine
		exit
	}
}

# Internal SCM name — must match install.ps1 / AddWindowsService(...) in Program.cs.
$ServiceName = "TitaniumWebProxy"

# Make sure the service is stopped.
Stop-Service -Name $ServiceName
# Remove the service, this doesnt always work. (Requires PS 6+)
Remove-Service -Name $ServiceName
# Make sure the service gets unregistered even if the last command failed.
sc.exe delete $ServiceName

Read-Host -Prompt "Press Enter to exit"