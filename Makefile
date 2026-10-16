PROJECT := src/Pulse/Pulse.csproj
TESTS   := tests/Pulse.Tests/Pulse.Tests.csproj
CONFIG  ?= Debug
EXE     ?= src/Pulse/bin/$(CONFIG)/net10.0-windows10.0.19041.0/win-x64/Pulse.exe
FIXTURE ?= $(CURDIR)/tests/Pulse.Tests/Fixtures/admin.json

.PHONY: start build test snapshot icon publish package clean probe probe-read

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
	powershell -NoProfile -ExecutionPolicy Bypass -File tools/snapshot.ps1 -Exe "$(EXE)" -Fixture "$(FIXTURE)"

# Release build with ReadyToRun (precompiled, so startup does less work) in artifacts/publish.
# Needs the .NET 10 runtime on the target machine.
publish:
	dotnet publish $(PROJECT) -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o artifacts/publish --nologo

# The installer and update packages, as a release builds them (artifacts/releases). Needs vpk:
# dotnet tool install -g vpk --version 1.2.161. VERSION=0.2.0 to pick the version.
VERSION ?= 0.0.1-local
package: publish
	vpk pack --packId PulseApp --packVersion $(VERSION) --packDir artifacts/publish --mainExe Pulse.exe --packTitle Pulse --icon src/Pulse/Resources/Pulse.ico --framework net10-x64-desktop -o artifacts/releases

# Redraw the app icon (src/Pulse/Resources/Pulse.ico and PNGs) from tools/IconGen.
icon:
	dotnet run --project tools/IconGen -- src/Pulse/Resources

# USB coolers: list supported controllers and running cooler software (sends nothing), or do one read.
# Close Pulse first. probe-read refuses while iCUE or similar is running; pass ARGS=--force to read anyway.
probe:
	dotnet run --project tools/CorsairProbe --artifacts-path artifacts/probe

probe-read:
	dotnet run --project tools/CorsairProbe --artifacts-path artifacts/probe -- --read --out artifacts/probe/exchanges.txt $(ARGS)

clean:
	dotnet clean $(PROJECT) -c $(CONFIG) --nologo
