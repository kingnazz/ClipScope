#!/usr/bin/env bash
# Linux cloud development prerequisites; the WinUI application runs on Windows.
set -euo pipefail
export DOTNET_ROOT=/workspace/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/workspace/.cache/dotnet-cli
export NUGET_PACKAGES=/workspace/.cache/nuget
export NUGET_SCRATCH=/workspace/.cache/nuget-scratch
export TMPDIR=/workspace/.cache/tmp
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
mkdir -p "$TMPDIR" "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"
if [ ! -x "$DOTNET_ROOT/dotnet" ] || ! dotnet --list-sdks | rg -q '^10\.0\.402 '; then
    install_file=$(mktemp "$TMPDIR/clipscope-dotnet-install.XXXXXX")
    trap 'rm -f "$install_file"' EXIT
    curl --fail --silent --show-error --location --retry 2 https://dot.net/v1/dotnet-install.sh -o "$install_file"
    bash "$install_file" --version 10.0.402 --azure-feed https://builds.dotnet.microsoft.com/dotnet --install-dir "$DOTNET_ROOT" --no-path
fi
cd /workspace/ClipScope
dotnet restore tests/ClipScope.Core.Tests --locked-mode
dotnet restore tests/ClipScope.Infrastructure.Tests --locked-mode
dotnet restore src/ClipScope.Windows --locked-mode
dotnet restore src/ClipScope.App --locked-mode -p:Platform=x64
dotnet test --project tests/ClipScope.Core.Tests
dotnet test --project tests/ClipScope.Infrastructure.Tests
dotnet build src/ClipScope.Windows --no-restore -warnaserror
dotnet build src/ClipScope.App --no-restore -p:Platform=x64 -p:WindowsAppSDKSelfContained=false -p:AppxGeneratePriEnabled=false -warnaserror
