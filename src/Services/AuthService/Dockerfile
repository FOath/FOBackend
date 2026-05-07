FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Services/AuthService/AuthService.csproj", "src/Services/AuthService/"]
COPY ["src/FOBackend.Protocol/FOBackend.Protocol.csproj", "src/FOBackend.Protocol/"]
COPY ["src/FOBackend.Infrastructure/FOBackend.Infrastructure.csproj", "src/FOBackend.Infrastructure/"]
RUN dotnet restore "src/Services/AuthService/AuthService.csproj"
COPY . .
WORKDIR "/src/src/Services/AuthService"
RUN dotnet build "AuthService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AuthService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AuthService.dll"]
