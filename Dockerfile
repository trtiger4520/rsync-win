# Multi-mode publish of the RsyncWin CLI.
#   PUBLISH_MODE=fdd  framework-dependent (needs .NET runtime) — DEFAULT
#   PUBLISH_MODE=r2r  ReadyToRun (IL pre-compiled, still needs the runtime)
#   PUBLISH_MODE=aot  Native AOT (self-contained native binary, no runtime; needs a musl base)
# FINAL_BASE must match the mode: dotnet/runtime for fdd/r2r; alpine (musl) for aot. Pass both
# --build-arg together to switch, e.g.
#   --build-arg PUBLISH_MODE=aot --build-arg FINAL_BASE=alpine:3.21
ARG PUBLISH_MODE=fdd
ARG FINAL_BASE=mcr.microsoft.com/dotnet/runtime:10.0-alpine@sha256:036b39f319141abc97fb32652ecfa97294e8108840f807999a0d467f4f1118ab

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:940f919ae84dd92ccd4aab7686fa5b777870b006c9360351039e16bcaad73d89 AS build
ARG PUBLISH_MODE
# clang/build-base/zlib-dev/lld are only consumed by the AOT path; harmless for fdd/r2r.
RUN apk add --no-cache clang build-base zlib-dev lld
WORKDIR /src
COPY . .
RUN set -eux; \
    case "$PUBLISH_MODE" in \
      fdd) dotnet publish src/RsyncWin.Cli/RsyncWin.Cli.csproj -c Release --no-self-contained -o /out ;; \
      r2r) dotnet publish src/RsyncWin.Cli/RsyncWin.Cli.csproj -c Release -r linux-musl-x64 --no-self-contained -p:PublishReadyToRun=true -o /out ;; \
      aot) dotnet publish src/RsyncWin.Cli/RsyncWin.Cli.csproj -c Release -r linux-musl-x64 -p:PublishAot=true -o /out ;; \
      *) echo "unknown PUBLISH_MODE=$PUBLISH_MODE (expected fdd|r2r|aot)" >&2; exit 2 ;; \
    esac

FROM ${FINAL_BASE} AS final
# openssh-client for the ssh transport; libstdc++/libgcc satisfy the AOT binary on a bare alpine base.
RUN apk add --no-cache openssh-client libstdc++ libgcc
WORKDIR /app
COPY --from=build /out/ ./
ENV PATH="/app:${PATH}"
VOLUME ["/data"]

ARG APP_UID=1000
ENV APP_UID=${APP_UID}
USER $APP_UID

ENTRYPOINT ["rsyncwin"]
