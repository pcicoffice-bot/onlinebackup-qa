#!/bin/sh
# Builds everything (agent for .NET 4.0 + .NET 8, server, tests) and runs the full suite.
# The .NET 4.0 agent is also exercised end-to-end under mono when mono is installed.
set -e
cd "$(dirname "$0")"
dotnet build src/Agent -nologo -v q
dotnet build tests/Tests -nologo -v q
dotnet test tests/Tests --no-build -nologo
