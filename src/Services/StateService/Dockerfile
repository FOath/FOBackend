FROM mcr.azure.cn/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8084

FROM mcr.azure.cn/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Services/StateService/StateService.csproj", "src/Services/StateService/"]
COPY ["src/FOBackend.Protocol/FOBackend.Protocol.csproj", "src/FOBackend.Protocol/"]
COPY ["src/FOBackend.Infrastructure/FOBackend.Infrastructure.csproj", "src/FOBackend.Infrastructure/"]
COPY ["src/Services/AuthService/Protos/", "src/Services/AuthService/Protos/"]
RUN dotnet restore "src/Services/StateService/StateService.csproj"
COPY . .
WORKDIR "/src/src/Services/StateService"
RUN dotnet build "StateService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "StateService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "StateService.dll"]
