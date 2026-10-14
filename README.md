# RachaConta backend

ASP.NET Core 9 Web API + EF Core 9 (SQLite). Contract: [`../docs/API.md`](../docs/API.md).

## Requirements
- .NET 9 SDK **and the ASP.NET Core 9 runtime** (`Microsoft.AspNetCore.App`).
  On Arch/Manjaro: `sudo pacman -S dotnet-sdk-9.0 aspnet-runtime-9.0`.
  Without sudo, install into your home directory:
  ```bash
  curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
  bash dotnet-install.sh --version 9.0.120 --install-dir ~/.dotnet          # SDK (includes runtimes)
  bash dotnet-install.sh --runtime aspnetcore --version 9.0.19 --install-dir ~/.dotnet
  export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
  ```

## Run
```bash
cd backend/src/RachaConta.Api
ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://localhost:5080
# or: dotnet run --launch-profile http
```
Development auto-applies migrations (SQLite file `rachaconta.db`). Health check: `GET /api/health`.
Outside Development, apply migrations with `dotnet tool run dotnet-ef database update --project src/RachaConta.Api`.

## Test
```bash
cd backend && dotnet test
```

## Migrations
```bash
cd backend
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> --project src/RachaConta.Api --output-dir Migrations
```

## Config (`appsettings.json`)
`ConnectionStrings:Default`, `Database:Provider` (`Sqlite` | `Postgres`; Postgres needs its own migration set),
`Cors:AllowedOrigins`, `Pix:DefaultCity` (city used for group-member Pix codes).
