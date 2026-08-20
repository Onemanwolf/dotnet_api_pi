#--------------------------------------------------------------------
# Build stage: restore, build and publish the API in Release mode.
#--------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the solution and project files first so that the restore step is
# cached unless the package references change. Package versions are managed
# centrally (Directory.Packages.props), so it must be present before restore.
COPY DotNetApiPi.sln ./
COPY Directory.Packages.props ./
COPY src/DotNetApiPi.Domain/DotNetApiPi.Domain.csproj src/DotNetApiPi.Domain/
COPY src/DotNetApiPi.Application/DotNetApiPi.Application.csproj src/DotNetApiPi.Application/
COPY src/DotNetApiPi.Infrastructure/DotNetApiPi.Infrastructure.csproj src/DotNetApiPi.Infrastructure/
COPY src/DotNetApiPi.Api/DotNetApiPi.Api.csproj src/DotNetApiPi.Api/

RUN dotnet restore src/DotNetApiPi.Api/DotNetApiPi.Api.csproj

# Copy the sources and publish a framework-dependent build.
COPY . .
RUN dotnet publish src/DotNetApiPi.Api/DotNetApiPi.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

#--------------------------------------------------------------------
# Runtime stage: the ASP.NET runtime image, running as the unprivileged
# `app` user that the base image provides.
#--------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080

# The base image ships no HTTP client. curl is installed solely so the
# container can probe /health (see the healthcheck in docker-compose.yml).
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# The app user must own the working directory: when no Mongo server is
# configured the API falls back to creating a SQLite database file here.
RUN chown app:app /app

COPY --from=build --chown=app:app /app/publish .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "DotNetApiPi.Api.dll"]
