FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY ["Subscription & Billing/Subscription & Billing.csproj", "Subscription & Billing/"]
RUN dotnet restore "Subscription & Billing/Subscription & Billing.csproj"

FROM restore AS test-restore
COPY tests/Billing.Tests/Billing.Tests.csproj tests/Billing.Tests/
RUN dotnet restore tests/Billing.Tests/Billing.Tests.csproj

FROM test-restore AS test
COPY . .
ENTRYPOINT ["dotnet", "test", "tests/Billing.Tests/Billing.Tests.csproj", "-c", "Release", "--no-restore", "--logger", "console;verbosity=normal"]

FROM restore AS build
COPY . .
RUN dotnet publish "Subscription & Billing/Subscription & Billing.csproj" -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Billing.dll"]
