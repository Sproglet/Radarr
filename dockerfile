FROM node:20-bookworm AS ui
WORKDIR /src

COPY package.json yarn.lock .yarnrc tsconfig.json ./
RUN corepack enable && yarn install --frozen-lockfile

COPY frontend ./frontend
RUN yarn build --env production


FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .

RUN dotnet msbuild -restore src/Radarr.sln \
    -p:SelfContained=True \
    -p:Configuration=Release \
    -p:Platform=Posix \
    -p:RuntimeIdentifiers=linux-x64 \
    -p:EnableAnalyzers=false \
    -t:PublishAllRids \
    && mkdir -p /app \
    && cp -a _output/net8.0/linux-x64/publish/. /app/

COPY --from=ui /src/_output/UI /app/UI

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
WORKDIR /app

RUN useradd --system --uid 1000 radarr \
    && mkdir /config \
    && chown -R radarr:radarr /app /config

COPY --from=build /app/ ./

USER radarr
EXPOSE 7878
VOLUME ["/config"]

ENTRYPOINT ["dotnet", "Radarr.dll", "--nobrowser", "--data=/config"]
