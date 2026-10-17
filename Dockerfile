# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Directory.Build.props Postfach.sln ./
COPY src/Postfach.Imap/Postfach.Imap.csproj src/Postfach.Imap/
COPY src/Postfach.Mime/Postfach.Mime.csproj src/Postfach.Mime/
COPY src/Postfach.Send/Postfach.Send.csproj src/Postfach.Send/
COPY src/Postfach.Server/Postfach.Server.csproj src/Postfach.Server/
RUN dotnet restore src/Postfach.Server/Postfach.Server.csproj
COPY src/ src/
RUN dotnet publish src/Postfach.Server/Postfach.Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    POSTFACH_DATA_DIR=/data \
    DOTNET_gcServer=0
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Postfach.Server.dll"]
