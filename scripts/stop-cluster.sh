#!/usr/bin/env bash
# Stops all locally-running dfdb nodes (published binary or dev-mode via dotnet run).
echo "Stopping DocumentForge nodes..."
pkill -f dfdb || true
pkill -f DocumentForge.Cli || true
echo "Done."
