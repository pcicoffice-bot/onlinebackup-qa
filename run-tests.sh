#!/bin/sh
# Builds everything (agent for .NET 4.0 + .NET 8, server, tests) and runs the full suite.
# The .NET 4.0 agent is also exercised end-to-end under mono when mono is installed.
set -e
cd "$(dirname "$0")"
dotnet build src/Agent -nologo -v q
dotnet build tests/Tests -nologo -v q
# every test with its time in the log: evidence of what really ran, and the slow tests for a Fast/Deep gate (CI-2)
dotnet test tests/Tests --no-build -nologo --logger "console;verbosity=normal"
