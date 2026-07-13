FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:940f919ae84dd92ccd4aab7686fa5b777870b006c9360351039e16bcaad73d89 AS build
WORKDIR /src

COPY . .
RUN dotnet publish src/RsyncWin.Cli/RsyncWin.Cli.csproj \
    --configuration Release \
    --output /out \
    --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine@sha256:036b39f319141abc97fb32652ecfa97294e8108840f807999a0d467f4f1118ab AS final
RUN apk add --no-cache openssh-client
WORKDIR /app
COPY --from=build /out/ ./

ENV PATH="/app:${PATH}"
VOLUME ["/data"]
USER $APP_UID
ENTRYPOINT ["rsyncwin"]
