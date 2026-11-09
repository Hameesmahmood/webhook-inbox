FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT_DIR
WORKDIR /repo
COPY global.json Directory.Build.props ./
COPY src/ src/
COPY tools/ tools/
RUN dotnet publish "${PROJECT_DIR}" -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG APP
ENV APP_DLL=${APP}.dll
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["sh", "-c", "exec dotnet $APP_DLL"]
