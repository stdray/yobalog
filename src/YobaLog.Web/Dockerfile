# syntax=docker/dockerfile:1.7
# Build stage: .NET SDK + bun for the MSBuild BuildFrontend target (Release-only, runs
# `bun install --frozen-lockfile && bun run build` inside src/YobaLog.Web). curl + unzip
# needed by the bun installer; apt lists removed after to keep the build image slim.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl unzip \
	&& rm -rf /var/lib/apt/lists/*
RUN curl -fsSL https://bun.sh/install | bash
ENV PATH="/root/.bun/bin:$PATH"

WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/YobaLog.Core/YobaLog.Core.csproj ./src/YobaLog.Core/
COPY src/YobaLog.Web/YobaLog.Web.csproj ./src/YobaLog.Web/
COPY src/YobaLog.Web/package.json src/YobaLog.Web/bun.lock ./src/YobaLog.Web/
RUN dotnet restore src/YobaLog.Web/YobaLog.Web.csproj -r linux-x64

COPY src/YobaLog.Core/ ./src/YobaLog.Core/
COPY src/YobaLog.Web/ ./src/YobaLog.Web/
RUN dotnet publish src/YobaLog.Web/YobaLog.Web.csproj \
	-c Release \
	-r linux-x64 \
	--self-contained \
	--no-restore \
	-o /app/publish

# Chiseled runtime runs as the `app` user (UID 1654). `dotnet publish` leaves /app/publish
# root-owned; if we COPY it as-is the app can't write to data/ on startup ("unable to open
# database file"). Pre-create the data dir in build stage with the expected ownership; COPY
# --chown in the final stage hands the whole tree over.
RUN mkdir -p /app/publish/data

# Runtime stage: chiseled Ubuntu Noble (~15MB, no shell, no package manager).
# linq2db.SQLite.MS bundles e_sqlite3.so which is glibc-compatible so chiseled works.
# Shell-less runtime is validated by the DockerSmoke Cake task (`build.cake`).
FROM mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled AS final
ARG APP_VERSION
ARG GIT_SHORT_SHA
ARG GIT_COMMIT_DATE
ENV APP_VERSION=$APP_VERSION
ENV GIT_SHORT_SHA=$GIT_SHORT_SHA
ENV GIT_COMMIT_DATE=$GIT_COMMIT_DATE
ENV ASPNETCORE_URLS=http://+:8080
# DataProtection keys persist across redeploys on the `/app/data` volume — without
# this, every redeploy regenerates the in-memory master key and invalidates prior
# cookies (users get logged out). See DataProtection block in YobaLogApp.cs.
ENV DataProtection__KeysDirectory=/app/data/keys
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
# `data/` holds per-workspace `.logs.db` + `.meta.db`. In prod mount a host volume here:
# `docker run ... -v /opt/yobalog/data:/app/data ...` so retention-swept state survives restarts.
VOLUME ["/app/data"]
EXPOSE 8080
ENTRYPOINT ["./YobaLog.Web"]
