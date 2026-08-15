# Ironfront headless game server — production container image (phase 03 task 2).
#
# This image is a THIN WRAPPER around the Unity Linux dedicated-server artifact that
# Ironfront/EditorBuild.cs (BuildDedicatedServer) emits — it does not build Unity. GitHub
# CI has no Unity licence, so the artifact is produced on a licensed Unity machine and
# passed in as the build context; .github/workflows/images.yml takes it as an explicit
# input rather than silently shipping a pseudo-headless image.
#
# The build CONTEXT is the artifact directory, not the repo root:
#   pwsh tools/build-server.ps1 -BuildOutput build/server      # on the Unity machine
#   docker build -f infra/docker/gameserver.Dockerfile \
#                -t ghcr.io/<owner>/ironfront-gameserver:<sha> build/server
#
# Expected contents of that directory (produced by the build):
#   Ironfront.Server.x86_64        the executable EditorBuild.cs names
#   Ironfront.Server_Data/         Unity resolves this beside the executable
#   UnityPlayer.so, *.so           the player runtime and native plugins
#
# NEVER place the Unity PROJECT, the built client, secrets or a PFX in this image; the
# artifact directory contains none of those, which is why it is the context.

# Ubuntu LTS: the platform Unity dedicated-server builds are validated against. The
# server subtarget strips the graphics device, so no X11/GL/Vulkan libraries are needed.
FROM ubuntu:24.04 AS runtime

# ca-certificates is the one addition the base image lacks and the deployment needs: the
# game-server-to-master link is TLS (IRONFRONT_GAMESERVER_MASTER_TLS=1) validated against
# the public domain's Let's Encrypt certificate, and CA validation reads the system trust
# store. Without this the registration handshake fails against a real certificate.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Non-root, with a fixed UID that matches the master image's app user (1654) so a shared
# host directory has one owner. HOME must be writable: Unity creates its persistentData
# path under ~/.config/unity3d on first run.
RUN groupadd --gid 1654 ironfront \
    && useradd --uid 1654 --gid 1654 --create-home --home-dir /home/ironfront ironfront
ENV HOME=/home/ironfront

WORKDIR /app/server
COPY . /app/server/
# The artifact arrives without the executable bit through some archive/copy paths.
RUN chmod +x /app/server/Ironfront.Server.x86_64 \
    && chown -R 1654:1654 /app/server

# The UDP data-plane port. It is set per instance from IRONFRONT_GAMESERVER_UDP_PORT
# (read by GameServerConfig), and compose publishes 27015/udp and 27016/udp; EXPOSE here
# is documentation of the default. The two instances differ only by that variable and the
# published port, never by a rebuilt image.
EXPOSE 27015/udp

# No HEALTHCHECK, on purpose. A Unity headless server has no TCP control port to probe,
# and UDP has no handshake, so `nc -z` would report a false "healthy" for a wedged
# process. Recovery is container liveness (restart: unless-stopped) for a crash, and the
# real "no game server can host a match" signal is detected OUT OF BAND: the master's
# metrics report registered/healthy counts and tools/alert.sh pages on healthy==0. That
# is also what the M3 forced-failure evidence exercises.

USER ironfront

# -batchmode -nographics is what makes a Unity build runnable headless at all; without
# them it tries to initialise a graphics device and exits. -logFile /dev/stdout streams
# Unity's log to the container's stdout so `docker logs` and journald capture it (a Unity
# build that logs to a file inside the container is a build whose logs vanish with it).
ENTRYPOINT ["/app/server/Ironfront.Server.x86_64", "-batchmode", "-nographics", "-logFile", "/dev/stdout"]
