FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Python runs model-written world map scripts; setpriv (util-linux) drops them to nobody.
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-numpy python3-pil util-linux \
 && rm -rf /var/lib/apt/lists/*
ENV DataDirectory=/data
WORKDIR /app
COPY --from=build /out .
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet","TrueModel.dll"]
