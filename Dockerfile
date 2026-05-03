# syntax=docker/dockerfile:1.7
#
# DocumentForge - single-node container image.
#
# Builds the self-contained `dfdb` binary with the .NET 9 SDK, then copies it
# into a tiny runtime-deps image. No .NET runtime is needed on the final image.
#
# Run locally:
#   docker build -t dfdb .
#   docker run --rm -p 5000:5000 -v dfdb-data:/data dfdb
#
# With an API key + replication secret (production):
#   docker run --rm -p 5000:5000 \
#     -e DFDB_API_KEY=sk_prod_... \
#     -e DFDB_REPLICATION_SECRET=repl_... \
#     -v dfdb-data:/data \
#     dfdb
#
# As a replication leader:
#   docker run --rm -p 5000:5000 -p 5500:5500 \
#     -v dfdb-data:/data \
#     dfdb serve --bind-all \
#       --replication-role leader --replication-port 5500

# -------- Build stage --------
FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src

# Build-identification info embedded into the binary and surfaced by GET /version
# (issue #36). Pass with --build-arg DFDB_GIT_SHA=$(git rev-parse HEAD); the
# compose file / CI workflow does this automatically. DFDB_BUILT_AT defaults to
# now if not set, so even local `docker build` produces a recognisable timestamp.
ARG DFDB_GIT_SHA=
ARG DFDB_BUILT_AT=
ARG DFDB_IMAGE=
ENV DFDB_GIT_SHA=${DFDB_GIT_SHA} \
    DFDB_BUILT_AT=${DFDB_BUILT_AT}

# Copy project files first so `dotnet restore` is cached separately from sources.
COPY Directory.Build.props ./
COPY src/DocumentForge.Core/*.csproj          src/DocumentForge.Core/
COPY src/DocumentForge.Storage/*.csproj       src/DocumentForge.Storage/
COPY src/DocumentForge.Document/*.csproj      src/DocumentForge.Document/
COPY src/DocumentForge.Index/*.csproj         src/DocumentForge.Index/
COPY src/DocumentForge.Query/*.csproj         src/DocumentForge.Query/
COPY src/DocumentForge.Transactions/*.csproj  src/DocumentForge.Transactions/
COPY src/DocumentForge.Engine/*.csproj        src/DocumentForge.Engine/
COPY src/DocumentForge.Cli/*.csproj           src/DocumentForge.Cli/
COPY samples/DocumentForge.AirlineDemo/*.csproj samples/DocumentForge.AirlineDemo/

RUN dotnet restore src/DocumentForge.Cli/DocumentForge.Cli.csproj -r linux-x64

# Copy the rest of the sources.
COPY src/ src/
COPY samples/DocumentForge.AirlineDemo/ samples/DocumentForge.AirlineDemo/

# Publish a self-contained single-file binary.
RUN dotnet publish src/DocumentForge.Cli/DocumentForge.Cli.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o /out

# -------- Runtime stage --------
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-bookworm-slim AS runtime

# curl for HEALTHCHECK, tini for a clean PID 1 that forwards signals.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl tini \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd -r dfdb && useradd -r -g dfdb -d /data -s /usr/sbin/nologin dfdb \
 && mkdir -p /data /app \
 && chown -R dfdb:dfdb /data /app

COPY --from=build /out/dfdb /app/dfdb
COPY docker/entrypoint.sh   /app/entrypoint.sh
RUN chmod +x /app/dfdb /app/entrypoint.sh

USER dfdb
WORKDIR /app

# Persistent data lives here; mount a volume or a managed disk onto this path.
VOLUME /data

# Carry the image identifier into the runtime so GET /version can report it.
# Override at `docker run` time with -e DFDB_IMAGE=ghcr.io/yourorg/dfdb:tag.
ARG DFDB_IMAGE=
ENV DFDB_IMAGE=${DFDB_IMAGE}

# Defaults; every one of these can be overridden at `docker run` time.
ENV DFDB_NODE_NAME=dfdb-1 \
    DFDB_DATA_DIR=/data \
    DFDB_PORT=5000

EXPOSE 5000 5500

# Render sets $PORT; the entrypoint maps PORT -> DFDB_PORT and forwards args.
ENTRYPOINT ["/usr/bin/tini", "--", "/app/entrypoint.sh"]

# Default command: start the REST API bound to all interfaces.
CMD ["serve", "--bind-all"]

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -fsS "http://localhost:${DFDB_PORT:-5000}/health" || exit 1
