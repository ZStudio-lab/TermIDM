FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LicenseIssuer/LicenseIssuer.csproj LicenseIssuer/
RUN dotnet restore LicenseIssuer/LicenseIssuer.csproj
COPY LicenseIssuer/ LicenseIssuer/
RUN dotnet publish LicenseIssuer/LicenseIssuer.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "LicenseIssuer.dll"]
