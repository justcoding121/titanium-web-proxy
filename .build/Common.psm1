### 
### Common Profile functions for all users
###

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptPath = Split-Path -Parent $MyInvocation.MyCommand.Definition

$SolutionRoot = Split-Path -Parent $ScriptPath

$ToolsPath = Join-Path -Path $SolutionRoot -ChildPath "lib"

Export-ModuleMember -Variable @('ScriptPath', 'SolutionRoot', 'ToolsPath')

function Install-Chocolatey()
{
	if(-not $env:ChocolateyInstall -or -not (Test-Path "$env:ChocolateyInstall"))
	{
		Write-Output "Chocolatey Not Found, Installing..."
		iex ((new-object net.webclient).DownloadString('http://chocolatey.org/install.ps1')) 
	}
}

function Install-Psake()
{
	if(!(Test-Path $env:ChocolateyInstall\lib\Psake*)) 
	{ 
		choco install psake -y
	}
}

Export-ModuleMember -Function *-*