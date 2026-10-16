# Captures one reading of every sensor, as administrator, into a test fixture (`make snapshot`).
# A script rather than inline in the Makefile, so it behaves the same from cmd, PowerShell and Git Bash.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Fixture
)

$process = Start-Process -FilePath $Exe -ArgumentList '--dump-snapshot', $Fixture -Verb RunAs -Wait -PassThru
switch ($process.ExitCode) {
    0 { Write-Host "Saved $Fixture" }
    2 { Write-Error 'Pulse is running. Close it first.'; exit 2 }
    default { Write-Error 'Snapshot failed; see the .error.txt next to the fixture.'; exit 1 }
}
