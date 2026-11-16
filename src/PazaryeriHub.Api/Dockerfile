# NOT: Docker şu an kullanılmıyor ve bu dosya doğrulanmadı (bkz. docs/decisions.md ADR-008).
# Derleme aşaması
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Önce proje dosyaları: paket geri yükleme katmanı önbelleğe alınır
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PazaryeriHub.Domain/PazaryeriHub.Domain.csproj src/PazaryeriHub.Domain/
COPY src/PazaryeriHub.Application/PazaryeriHub.Application.csproj src/PazaryeriHub.Application/
COPY src/PazaryeriHub.Infrastructure/PazaryeriHub.Infrastructure.csproj src/PazaryeriHub.Infrastructure/
COPY src/PazaryeriHub.Adapters.Trendyol/PazaryeriHub.Adapters.Trendyol.csproj src/PazaryeriHub.Adapters.Trendyol/
COPY src/PazaryeriHub.Adapters.Hepsiburada/PazaryeriHub.Adapters.Hepsiburada.csproj src/PazaryeriHub.Adapters.Hepsiburada/
COPY src/PazaryeriHub.Adapters.Beymen/PazaryeriHub.Adapters.Beymen.csproj src/PazaryeriHub.Adapters.Beymen/
COPY src/PazaryeriHub.Adapters.Softcotton/PazaryeriHub.Adapters.Softcotton.csproj src/PazaryeriHub.Adapters.Softcotton/
COPY src/PazaryeriHub.Adapters.Mock/PazaryeriHub.Adapters.Mock.csproj src/PazaryeriHub.Adapters.Mock/
COPY src/PazaryeriHub.Api/PazaryeriHub.Api.csproj src/PazaryeriHub.Api/
RUN dotnet restore src/PazaryeriHub.Api/PazaryeriHub.Api.csproj

COPY src/ src/
RUN dotnet publish src/PazaryeriHub.Api/PazaryeriHub.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Çalışma aşaması
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/logs && chown $APP_UID /app/logs
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "PazaryeriHub.Api.dll"]
