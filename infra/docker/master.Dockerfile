# Ironfront master server — production container image (phase 03 task 2).
#
# Multi-stage: restore/publish on the pinned .NET 8 SDK, then run framework-dependent
# on the matching runtime image as a non-root user. The image is CODE ONLY — the
# database, TLS certificate and logs are mounted at runtime, never baked in (see the
# volume and secret rules in infra/compose/compose.yaml).
#
# Build from the REPOSITORY ROOT so the project reference graph is in context:
#   docker build -f infra/docker/master.Dockerfile -t ghcr.io/<owner>/ironfront-master:<sha> .
# .dockerignore at the root keeps the Unity project, secrets and state out of the context.

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# Pinned to the 8.0 SDK. global.json requests 8.0.100 with rollForward=latestFeature,
# so any 8.0.x the base image ships satisfies it and a 9.x/10.x never sneaks in — the
# same boundary .github/workflows/ci.yml enforces.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the build contract first (props + central package versions + SDK pin) and the
# three .csproj in the master's reference graph, then restore. This layer is cached
# until a dependency version or a project reference actually changes, so an ordinary
# source edit does not re-download the NuGet graph.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY Ironfront.MasterServer/Ironfront.MasterServer.csproj Ironfront.MasterServer/
COPY Ironfront.Net.Protocol/Ironfront.Net.Protocol.csproj Ironfront.Net.Protocol/
COPY Ironfront.Net.Configuration/Ironfront.Net.Configuration.csproj Ironfront.Net.Configuration/
RUN dotnet restore Ironfront.MasterServer/Ironfront.MasterServer.csproj

# Now the sources for those three projects, and publish. No -r/RID: a portable
# framework-dependent publish emits IL plus the native SQLite assets under runtimes/,
# which the runtime image resolves for its own architecture. UseAppHost=false skips the
# native launcher because the entrypoint invokes `dotnet Ironfront.MasterServer.dll`.
COPY Ironfront.MasterServer/ Ironfront.MasterServer/
COPY Ironfront.Net.Protocol/ Ironfront.Net.Protocol/
COPY Ironfront.Net.Configuration/ Ironfront.Net.Configuration/
RUN dotnet publish Ironfront.MasterServer/Ironfront.MasterServer.csproj \
        -c Release \
        --no-restore \
        -p:UseAppHost=false \
        -o /app/publish

# ---------------------------------------------------------------------------
# Runtime stage
# ---------------------------------------------------------------------------
# runtime (not aspnet): this is a raw-TCP console process, and the metrics endpoint is
# deliberately not HTTP (see MetricsEndpoint.cs), so there is no ASP.NET Core to carry.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# The .NET 8 images define a non-root user `app` (UID/GID 1654). Create the runtime
# mount points and hand them to that user BEFORE dropping privileges, so a bind-mounted
# host directory chowned to 1654 lines up. /data holds the SQLite database and the
# durability CSV; /tls holds the read-only PFX.
RUN mkdir -p /data /tls && chown -R $APP_UID:$APP_UID /data /tls

WORKDIR /app
COPY --from=build /app/publish/ ./

# Sensible container defaults. The database and durability CSV live on the mounted
# volume so they survive a container replacement; TLS points at the read-only mount.
# Everything security-sensitive (the shared secret, the certificate password) is
# supplied at runtime from the protected env file, never set here.
ENV IRONFRONT_DB_PATH=/data/ironfront.db \
    IRONFRONT_METRICS_CSV=/data/durability.csv \
    IRONFRONT_TLS_CERT_PATH=/tls/master.pfx \
    IRONFRONT_STRUCTURED_LOG=1 \
    DOTNET_gcServer=1

# The public lobby/auth/matchmaking port. Documentation only — compose does the actual
# publishing. Metrics (27001) is intentionally NOT exposed: it binds loopback and is
# reached from the host, never published to the Internet.
EXPOSE 27000

# Liveness: a plain TCP connect to the master port. It completes the TCP handshake and
# closes without reading, so it reports "listening" whether the listener is plaintext or
# TLS (the TLS handshake only begins after accept) and never blocks waiting for a server
# banner the MSP protocol does not send. bash ships in the Debian-based runtime image;
# --timeout bounds a hung connect.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/27000' || exit 1

USER $APP_UID

# SIGINT, not the default SIGTERM: Program.cs drains the logic queue and closes sockets
# in order on Console.CancelKeyPress. STOPSIGNAL makes `docker stop` deliver that.
STOPSIGNAL SIGINT

ENTRYPOINT ["dotnet", "Ironfront.MasterServer.dll"]
