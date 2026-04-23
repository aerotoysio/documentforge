#!/bin/sh
# Docker entrypoint for dfdb.
#
# Translates platform-specific env vars so the same image runs cleanly on
# Render, Fly, Railway, Docker Compose, and bare Docker:
#
#   $PORT  -> DFDB_PORT    (Render / Heroku / Fly inject this)
#
# Everything else is passed straight through to the dfdb binary, so
# the container honors every `dfdb serve ...` flag and every DFDB_*
# env var from NodeConfig exactly as you'd pass them to a local binary.

set -e

if [ -n "$PORT" ]; then
    export DFDB_PORT="$PORT"
fi

# The first argument is the subcommand (serve by default via Dockerfile CMD).
# Exec keeps dfdb as PID 1's child so tini handles signals correctly.
exec /app/dfdb "$@"
