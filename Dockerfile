# Stage 1: compile with the .NET SDK. Nothing from this stage ships except the published app.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# The project file and shared build files first, so the restore step is reused until one of them changes
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Enqueuer/Enqueuer.csproj src/Enqueuer/
RUN dotnet restore src/Enqueuer/Enqueuer.csproj

COPY src/ src/
RUN dotnet publish src/Enqueuer/Enqueuer.csproj --configuration Release --no-restore --output /app -p:UseAppHost=false

# Stage 2: what ships. The .NET runtime and the published app; no git, as the enqueuer never clones.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# The image's own non-root user, app (1654)
USER $APP_UID
ENTRYPOINT ["dotnet", "Enqueuer.dll"]
