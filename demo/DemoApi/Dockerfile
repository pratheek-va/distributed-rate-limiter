# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore (copy csproj files first for layer caching)
COPY RateLimiter.sln ./
COPY src/RateLimiter.Core/RateLimiter.Core.csproj src/RateLimiter.Core/
COPY src/RateLimiter.AspNetCore/RateLimiter.AspNetCore.csproj src/RateLimiter.AspNetCore/
COPY demo/DemoApi/DemoApi.csproj demo/DemoApi/
RUN dotnet restore demo/DemoApi/DemoApi.csproj

# Build + publish
COPY . .
RUN dotnet publish demo/DemoApi/DemoApi.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "DemoApi.dll"]
