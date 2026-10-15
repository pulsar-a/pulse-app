PROJECT := src/Pulse/Pulse.csproj
CONFIG  ?= Debug
EXE     ?= src/Pulse/bin/$(CONFIG)/net10.0-windows/win-x64/Pulse.exe

.PHONY: start build clean

# Build, then launch through the Windows shell so the admin (UAC) prompt appears.
# `dotnet run` can't do that from a non-elevated terminal and fails with error 740.
start: build
	powershell -NoProfile -Command "Start-Process -FilePath '$(EXE)'"

build:
	dotnet build $(PROJECT) -c $(CONFIG) --nologo

clean:
	dotnet clean $(PROJECT) -c $(CONFIG) --nologo
