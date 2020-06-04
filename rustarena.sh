#!/usr/bin/env bash

# Enable debugging
#set -x

mkdir -p /steamcmd/rust/oxide/plugins
curl https://raw.githubusercontent.com/eandersson/rustarena/master/plugin/RustArena.cs --output /steamcmd/rust/oxide/plugins/RustArena.cs
curl https://raw.githubusercontent.com/eandersson/rustarena/master/plugin/QuickSmelt.json --output /steamcmd/rust/oxide/plugins/QuickSmelt.json
curl https://umod.org/plugins/QuickSmelt.cs --output /steamcmd/rust/oxide/plugins/QuickSmelt.cs

bash /app/start.sh
