FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PrairieBookings.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish --no-restore -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends tzdata && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p App_Data && chown -R app:app App_Data 2>/dev/null || (useradd -m app && chown -R app:app App_Data)
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_FORWARDEDHEADERS_ENABLED=true AllowedHosts=*
EXPOSE 8080
ENTRYPOINT ["dotnet", "PrairieBookings.dll"]
