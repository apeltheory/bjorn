#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_HOME="$PWD/.tools"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
exec .tools/dotnet/dotnet build plugin -c Release --ignore-failed-sources "$@"
