FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/backend/*.csproj ./src/backend/
RUN dotnet restore ./src/backend/Bento.Api.csproj

COPY src/backend/ ./src/backend/
# The csproj embeds the demo workspace (schema + rows) from scripts/fixtures.
COPY scripts/fixtures/ ./scripts/fixtures/
RUN dotnet publish ./src/backend/Bento.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN addgroup --system bento \
    && adduser --system --ingroup bento --home /app --shell /usr/sbin/nologin bento

ENV DATA_DIR=/data
ENV BACKEND_PORT=5000

COPY --from=build /app/publish/ ./

RUN mkdir -p /data \
    && chown -R bento:bento /app /data

VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=5 \
  CMD ["dotnet", "Bento.Api.dll", "--healthcheck"]

USER bento

ENTRYPOINT ["dotnet", "Bento.Api.dll"]
