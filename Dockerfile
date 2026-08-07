# Multi-stage so the server needs Docker and nothing else — no .NET SDK, no Node.
#
#   docker build -t sancmeld-api .
#
# Build context is the repository root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so a change to source code does not invalidate the restore layer.
# Restoring is the slow part and it only depends on these.
COPY Directory.Build.props ./
COPY src/SancMeld.Core/SancMeld.Core.csproj                     src/SancMeld.Core/
COPY src/SancMeld.Api/SancMeld.Api.csproj                       src/SancMeld.Api/
COPY src/SancMeld.Connectors.EuFsf/SancMeld.Connectors.EuFsf.csproj src/SancMeld.Connectors.EuFsf/
COPY src/SancMeld.Server/SancMeld.Server.csproj                 src/SancMeld.Server/
RUN dotnet restore src/SancMeld.Server/SancMeld.Server.csproj

COPY src/ src/
RUN dotnet publish src/SancMeld.Server/SancMeld.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# libgssapi-krb5-2 is not used — the database uses password authentication — but Npgsql
# probes for it on every start and logs "Cannot load library" and "Error:" when it is
# absent. Two scary lines at the top of every log, on a tool whose logs someone will one
# day read looking for a real failure, are worth a megabyte.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

# Non-root: the API needs no privileges, and a compliance tool running as root is the
# first thing a security review asks about.
RUN useradd --create-home --shell /usr/sbin/nologin sancmeld \
    && mkdir -p /var/lib/sancmeld/snapshots \
    && chown -R sancmeld:sancmeld /var/lib/sancmeld
USER sancmeld

COPY --from=build --chown=sancmeld:sancmeld /app ./

# Downloaded artefacts are kept so a past decision can be replayed; mount a volume here
# or they vanish with the container.
ENV SancMeld__Ingest__SnapshotStoragePath=/var/lib/sancmeld/snapshots \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1

EXPOSE 8080

# No HEALTHCHECK: the aspnet image ships neither curl nor wget, and adding one to a
# runtime image to poll an endpoint the orchestrator can poll itself is not worth the
# extra surface. /health is there for whatever sits in front.

ENTRYPOINT ["dotnet", "SancMeld.Server.dll"]
