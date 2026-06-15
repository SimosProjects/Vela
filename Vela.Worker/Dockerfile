# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files FIRST
# If only source files change, NuGet restore is skipped (cached layer)
COPY Vela.sln ./
COPY Vela.AlertPoC/Vela.AlertPoC.csproj   Vela.AlertPoC/
COPY Vela.Worker/Vela.Worker.csproj        Vela.Worker/

# Copy all source files, this layer changes on every code change
COPY Vela.AlertPoC/ Vela.AlertPoC/
COPY Vela.Worker/   Vela.Worker/

# Build and publish release binaries
RUN dotnet publish Vela.Worker/Vela.Worker.csproj \
    --configuration Release \
    --output /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user, security hardening
RUN groupadd --system appgroup && \
    useradd --system --gid appgroup --no-create-home appuser

# Copy only the published output, no SDK, no source
COPY --from=build /app/publish .

# Create logs directory and set ownership
RUN mkdir -p /app/logs && \
    chown -R appuser:appgroup /app

# Switch to non-root user
USER appuser

# XTRADES_TOKEN must be injected at runtime, never baked in
ENV DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Vela.Worker.dll"]