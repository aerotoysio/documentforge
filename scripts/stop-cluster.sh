#!/usr/bin/env bash
# Stops all locally-running DocumentForge.Api nodes.
echo "Stopping DocumentForge nodes..."
pkill -f DocumentForge.Api || true
echo "Done."
