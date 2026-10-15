FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/RachaConta.Api/RachaConta.Api.csproj src/RachaConta.Api/
RUN dotnet restore src/RachaConta.Api/RachaConta.Api.csproj
COPY src/ src/
RUN dotnet publish src/RachaConta.Api/RachaConta.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000
ENTRYPOINT ["dotnet", "RachaConta.Api.dll"]
