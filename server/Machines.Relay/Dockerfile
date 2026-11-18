# The Machines multiplayer relay as a container, for Cloud Run (deploy-cloudrun.sh) or any host with Docker.
#
# The build context is the repository's root, which holds every project the relay is made of; only those go in
# (Dockerfile.dockerignore beside this file):
#   docker build -f server/Machines.Relay/Dockerfile -t machines-relay .
# Run it with the web side on 8080 (/health, WebSockets on /ws) and TCP on 7777 for clients typing host:port:
#   docker run --rm -p 8080:8080 -p 7777:7777 machines-relay

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# The project files first, so that the packages are fetched again only when they change.
COPY Directory.Build.props ./
COPY import/Machines.Import/Machines.Import.csproj import/Machines.Import/
COPY sim/Machines.Sim/Machines.Sim.csproj sim/Machines.Sim/
COPY net/Machines.Net/Machines.Net.csproj net/Machines.Net/
COPY server/Machines.Relay/Machines.Relay.csproj server/Machines.Relay/
RUN dotnet restore server/Machines.Relay/Machines.Relay.csproj
COPY import/Machines.Import/ import/Machines.Import/
COPY sim/Machines.Sim/ sim/Machines.Sim/
COPY net/Machines.Net/ net/Machines.Net/
COPY server/Machines.Relay/ server/Machines.Relay/
RUN dotnet publish server/Machines.Relay/Machines.Relay.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# Cloud Run sets PORT itself; the relay serves HTTP and WebSockets on it, and TCP on 7777 as everywhere.
ENV PORT=8080
EXPOSE 8080 7777
USER $APP_UID
ENTRYPOINT ["dotnet", "Machines.Relay.dll"]
