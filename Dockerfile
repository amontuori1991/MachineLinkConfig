# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy everything
COPY . .

# restore & publish
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Render uses PORT env var
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

# IMPORTANT: replace with your dll name if different
ENTRYPOINT ["dotnet", "MachineLinkConfig.dll"]
