# Multi-stage build for the ASP.NET Core server (plan §11).
# Build context is the repository root so the shared Contracts project is available.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (better layer caching): copy only project files.
COPY QuizGame.slnx ./
COPY global.json ./
COPY src/QuizGame.Contracts/QuizGame.Contracts.csproj src/QuizGame.Contracts/
COPY src/QuizGame.Server/QuizGame.Server.csproj src/QuizGame.Server/
RUN dotnet restore src/QuizGame.Server/QuizGame.Server.csproj

# Copy the rest and publish.
COPY src/QuizGame.Contracts/ src/QuizGame.Contracts/
COPY src/QuizGame.Server/ src/QuizGame.Server/
RUN dotnet publish src/QuizGame.Server/QuizGame.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Listen on HTTP inside the Docker network; Caddy terminates TLS (plan D10).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app ./
ENTRYPOINT ["dotnet", "QuizGame.Server.dll"]
