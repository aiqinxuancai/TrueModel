FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV DataDirectory=/data
WORKDIR /app
COPY --from=build /out .
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet","TrueModel.dll"]
