FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src


COPY ["Directory.Build.props", "./"]
COPY ["FileManager.Server/FileManager.Server.csproj", "FileManager.Server/"]
COPY ["FileManager.Server/packages.lock.json", "FileManager.Server/"]

RUN dotnet restore "FileManager.Server/FileManager.Server.csproj" --locked-mode

COPY . .

WORKDIR /src/FileManager.Server

RUN dotnet publish "FileManager.Server.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

WORKDIR /app

COPY --from=build /app/publish .

RUN mkdir -p /app/ManagerData && chown -R app:app /app


ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER app

ENTRYPOINT ["dotnet", "FileManager.Server.dll"]
