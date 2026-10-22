# syntax=docker/dockerfile:1

# 构建阶段固定在构建机架构上运行，通过 -a 交叉发布目标架构，无需 QEMU
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/Sgs.Collector/Sgs.Collector.csproj src/Sgs.Collector/
RUN dotnet restore src/Sgs.Collector/Sgs.Collector.csproj -a $TARGETARCH
COPY src/Sgs.Collector/ src/Sgs.Collector/
RUN dotnet publish src/Sgs.Collector/Sgs.Collector.csproj -c Release -a $TARGETARCH --no-restore \
        --self-contained false -p:UseAppHost=false -o /app \
    && mkdir /data

# Wasmtime、SQLite 的原生库依赖 glibc，不能换成 Alpine
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=$APP_UID:$APP_UID /data /data
ENV Urls=http://+:5080 \
    ASPNETCORE_HTTP_PORTS= \
    Collector__DataDir=/data
EXPOSE 5080
VOLUME /data
USER $APP_UID
ENTRYPOINT ["dotnet", "Sgs.Collector.dll"]
