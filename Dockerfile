FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/RachaPix.Api/RachaPix.Api.csproj src/RachaPix.Api/
RUN dotnet restore src/RachaPix.Api/RachaPix.Api.csproj
COPY src/ src/
RUN dotnet publish src/RachaPix.Api/RachaPix.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "RachaPix.Api.dll"]
