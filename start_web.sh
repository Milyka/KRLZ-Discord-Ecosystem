#!/usr/bin/env bash
set -e

arch=$(uname -m)
if [ "$arch" = "aarch64" ] || [ "$arch" = "arm64" ]; then
    export DOTNET_GCRegionRange="0x100000000"
    export DOTNET_GCHeapHardLimit="0x40000000"
    export DOTNET_gcServer="0"
fi

cd "$(dirname "$0")"
echo "🌐 Запуск KRLZ Web Panel (http://localhost:5080)..."
exec dotnet run --project src/Bot.Web/Bot.Web.csproj -c Release --no-build "$@"
