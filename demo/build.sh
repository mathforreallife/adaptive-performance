#!/usr/bin/env bash
# Builds both demo programs and packages the ready-to-run zip in demo/release.
# Works on Windows (Git Bash), Linux and macOS with the .NET SDK installed; the output
# targets .NET Framework 4.6.2, which is built into Windows 10 and 11.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build src/Sandbox/GAPO.Sandbox.csproj -c Release -nologo -v q
dotnet build src/Optimiser/GAPO.Optimiser.csproj -c Release -nologo -v q
rm -rf release && mkdir -p release/GAPO-Demo
cp src/Sandbox/bin/Release/net462/GAPO-Sandbox.exe release/GAPO-Demo/
cp src/Optimiser/bin/Release/net462/GAPO-Optimiser.exe release/GAPO-Demo/
cp START-HERE.txt release/GAPO-Demo/
(cd release && rm -f GAPO-Demo.zip && zip -q -r -9 GAPO-Demo.zip GAPO-Demo)
ls -la release release/GAPO-Demo
