FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /repo
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet publish "src/${PROJECT}" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["sh", "-c", "exec dotnet $APP_DLL"]
