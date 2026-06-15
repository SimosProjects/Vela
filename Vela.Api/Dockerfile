# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Vela.sln ./
COPY Vela.AlertPoC/Vela.AlertPoC.csproj   Vela.AlertPoC/
COPY Vela.Worker/Vela.Worker.csproj        Vela.Worker/
COPY Vela.Api/Vela.Api.csproj              Vela.Api/

COPY Vela.AlertPoC/ Vela.AlertPoC/
COPY Vela.Worker/   Vela.Worker/
COPY Vela.Api/      Vela.Api/

RUN dotnet publish Vela.Api/Vela.Api.csproj \
    --configuration Release \
    --output /app/publish \
    /p:ErrorOnDuplicatePublishOutputFiles=false

# Stage 2: Runtime
# aspnet image includes ASP.NET Core runtime (Kestrel etc.)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user, security hardening
RUN groupadd --system appgroup && \
    useradd --system --gid appgroup --no-create-home appuser

COPY --from=build /app/publish .

RUN mkdir -p /app/logs && \
    chown -R appuser:appgroup /app

USER appuser

ENV DOTNET_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "Vela.Api.dll"]