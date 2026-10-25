# syntax=docker/dockerfile:1
# Build context is the repo root: docker build -f src/BaruchsTreks/Dockerfile .
# Base images are pinned by digest. To bump: `docker buildx imagetools inspect <image:tag>` and copy its Digest here.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/BaruchsTreks/BaruchsTreks.csproj src/BaruchsTreks/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/BaruchsTreks/BaruchsTreks.csproj -r linux-x64
COPY src/BaruchsTreks/ src/BaruchsTreks/
# No --no-restore: CI (gha layer cache) does not carry the cache mount, so publish must be able to restore again.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/BaruchsTreks/BaruchsTreks.csproj -c Release -r linux-x64 --no-self-contained -o /app

# Runtime: 10.0-noble-chiseled-extra (non-root `app` user). The plain chiseled image ships without ICU and runs in
# invariant-globalization mode, so cs-CZ culture and diacritics folding would not work; -extra adds ICU.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:9e3919aff68057f3e45125c5255a71d3b5f40364e45ede883348688851b9f0df
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BaruchsTreks.dll"]
