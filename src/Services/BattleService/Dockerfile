FROM mcr.azure.cn/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 7777/udp
EXPOSE 9081

FROM mcr.azure.cn/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Services/BattleService/BattleService.csproj", "src/Services/BattleService/"]
COPY ["src/FOBackend.Protocol/FOBackend.Protocol.csproj", "src/FOBackend.Protocol/"]
COPY ["src/FOBackend.Core/FOBackend.Core.csproj", "src/FOBackend.Core/"]
COPY ["src/FOBackend.Transport/FOBackend.Transport.csproj", "src/FOBackend.Transport/"]
COPY ["src/FOBackend.Infrastructure/FOBackend.Infrastructure.csproj", "src/FOBackend.Infrastructure/"]
COPY ["src/FOBackend.Persistence/FOBackend.Persistence.csproj", "src/FOBackend.Persistence/"]
COPY ["src/Services/AuthService/Protos/", "src/Services/AuthService/Protos/"]
COPY ["src/Services/StateService/Protos/", "src/Services/StateService/Protos/"]
RUN dotnet restore "src/Services/BattleService/BattleService.csproj"
COPY . .
WORKDIR "/src/src/Services/BattleService"
RUN dotnet build "BattleService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "BattleService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BattleService.dll"]
