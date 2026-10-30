ARG BUILD_STAGE=build

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    MESSENGER_DB=/state/messenger.db \
    MESSENGER_FILES=/state/files \
    MESSENGER_KEYS=/state/keys \
    DOTNET_EnableDiagnostics=0
RUN mkdir /state && chown 1654:1654 /state && chmod 700 /state
USER 1654:1654
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY . .
ARG REVISION=unknown
RUN dotnet publish backend/MessengerBackend.csproj -c Release --self-contained false -o /out --nologo \
    && printf '%s\n' "$REVISION" > /out/REVISION

# The SSH CLI supplies locally published artifacts to avoid installing an SDK on the server.
FROM scratch AS prebuilt
COPY . /out/

FROM ${BUILD_STAGE} AS artifacts
FROM runtime AS final
COPY --from=artifacts /out/ ./
ENTRYPOINT ["dotnet", "MessengerBackend.dll"]
