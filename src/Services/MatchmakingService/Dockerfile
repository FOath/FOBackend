FROM mcr.azure.cn/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8082

FROM mcr.azure.cn/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Services/MatchmakingService/MatchmakingService.csproj", "src/Services/MatchmakingService/"]
COPY ["src/FOBackend.Protocol/FOBackend.Protocol.csproj", "src/FOBackend.Protocol/"]
COPY ["src/FOBackend.Infrastructure/FOBackend.Infrastructure.csproj", "src/FOBackend.Infrastructure/"]
COPY ["src/Services/AuthService/Protos/", "src/Services/AuthService/Protos/"]
COPY ["src/Services/BattleService/Protos/", "src/Services/BattleService/Protos/"]
RUN dotnet restore "src/Services/MatchmakingService/MatchmakingService.csproj"
COPY . .
WORKDIR "/src/src/Services/MatchmakingService"
RUN dotnet build "MatchmakingService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "MatchmakingService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "MatchmakingService.dll"]
