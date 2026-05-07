# ============================================================
# FOBackend Server - 多阶段构建
# 优化：最终镜像仅包含运行时依赖
# ============================================================

# 阶段 1: 构建阶段
FROM mcr.azure.cn/dotnet/sdk:8.0 AS build
WORKDIR /src

# 复制项目文件并还原依赖（利用Docker缓存层）
COPY src/FOBackend.sln ./
COPY src/*/FOBackend.*.csproj ./*/
RUN dotnet restore FOBackend.sln

# 复制所有源代码并编译发布
COPY src/ ./
RUN dotnet publish FOBackend.sln \
    -c Release \
    -o /app/publish \
    --no-restore

# 阶段 2: 运行时镜像（精简版）
FROM mcr.azure.cn/dotnet/runtime:8.0 AS final

WORKDIR /app

# 安装基础工具（用于调试和监控）
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    iputils-ping \
    && rm -rf /var/lib/apt/lists/*

# 从构建阶段复制发布输出
COPY --from=build /app/publish .

# 创建必要目录
RUN mkdir -p /app/data /app/logs

# 暴露 UDP 端口（KCP 协议使用）
EXPOSE 7777/udp

# 健康检查（可选）
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:7777/health || exit 1

# 设置环境变量
ENV ASPNETCORE_ENVIRONMENT=Production
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

# 启动服务器
ENTRYPOINT ["dotnet", "FOBackend.Server.dll"]
