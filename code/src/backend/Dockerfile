FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/backend/*.csproj ./src/backend/
RUN dotnet restore ./src/backend/Repere.Api.csproj

COPY src/backend/ ./src/backend/
RUN dotnet publish ./src/backend/Repere.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN addgroup --system repere \
    && adduser --system --ingroup repere --home /app --shell /usr/sbin/nologin repere

ENV DATA_DIR=/data
ENV BACKEND_PORT=5000

COPY --from=build /app/publish/ ./

RUN mkdir -p /data \
    && chown -R repere:repere /app /data

VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=5 \
  CMD ["dotnet", "Repere.Api.dll", "--healthcheck"]

USER repere

ENTRYPOINT ["dotnet", "Repere.Api.dll"]
