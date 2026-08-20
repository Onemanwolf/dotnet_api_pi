#--------------------------------------------------------------------
# Build stage: restore, build and publish the consumer in Release mode.
#--------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the solution and project files first so that the restore step is
# cached unless the package references change. Package versions are managed
# centrally (Directory.Packages.props), so it must be present before restore.
COPY DotNetApiPi.sln ./
COPY Directory.Packages.props ./
COPY src/DotNetApiPi.Consumer/DotNetApiPi.Consumer.csproj src/DotNetApiPi.Consumer/

RUN dotnet restore src/DotNetApiPi.Consumer/DotNetApiPi.Consumer.csproj

# Copy the sources and publish a framework-dependent build.
COPY . .
RUN dotnet publish src/DotNetApiPi.Consumer/DotNetApiPi.Consumer.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

#--------------------------------------------------------------------
# Runtime stage: the plain .NET runtime image (the consumer is a console
# app, not an ASP.NET host), running as the unprivileged `app` user that
# the base image provides.
#--------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build --chown=app:app /app/publish .
USER app
ENTRYPOINT ["dotnet", "DotNetApiPi.Consumer.dll"]
