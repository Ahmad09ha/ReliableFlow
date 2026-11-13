FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ReliableFlow.EntityFrameworkCore/ src/ReliableFlow.EntityFrameworkCore/
COPY samples/Shared.Contracts/ samples/Shared.Contracts/
COPY samples/Inventory.Service/ samples/Inventory.Service/
RUN dotnet restore samples/Inventory.Service/Inventory.Service.csproj
RUN dotnet publish samples/Inventory.Service/Inventory.Service.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Inventory.Service.dll"]
