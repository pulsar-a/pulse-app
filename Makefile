PROJECT := src/Pulse/Pulse.csproj
TESTS   := tests/Pulse.Tests/Pulse.Tests.csproj
CONFIG  ?= Debug
EXE     ?= src/Pulse/bin/$(CONFIG)/net10.0-windows/win-x64/Pulse.exe
FIXTURE ?= $(CURDIR)/tests/Pulse.Tests/Fixtures/admin.json

.PHONY: start build test snapshot icon clean

# Build, then launch through the Windows shell so the admin (UAC) prompt appears.
# `dotnet run` can't do that from a non-elevated terminal and fails with error 740.
start: build
	powershell -NoProfile -Command "Start-Process -FilePath '$(EXE)'"

build:
	dotnet build $(PROJECT) -c $(CONFIG) --nologo

# Tests build into artifacts/ so they also run while Pulse is open (it locks src/Pulse/bin).
test:
	dotnet test $(TESTS) --nologo --artifacts-path artifacts/test

# Capture a real snapshot as admin into the test fixtures (asks for admin rights; close Pulse first).
snapshot: build
	powershell -NoProfile -Command "$$p = Start-Process -FilePath '$(EXE)' -ArgumentList '--dump-snapshot','$(FIXTURE)' -Verb RunAs -Wait -PassThru; if ($$p.ExitCode -eq 2) { Write-Error 'Pulse is running. Close it first.'; exit 2 } elseif ($$p.ExitCode -ne 0) { Write-Error 'Snapshot failed; see the .error.txt next to the fixture.'; exit 1 } else { Write-Host 'Saved $(FIXTURE)' }"

# Redraw the app icon (src/Pulse/Resources/Pulse.ico and PNGs) from tools/IconGen.
icon:
	dotnet run --project tools/IconGen -- src/Pulse/Resources

clean:
	dotnet clean $(PROJECT) -c $(CONFIG) --nologo
