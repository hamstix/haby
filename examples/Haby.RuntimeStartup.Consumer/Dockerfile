FROM mcr.microsoft.com/dotnet/sdk@sha256:ddbaedfc8b774389d91a511af08b77296f2c7a8d15d86f6214d2a87166933625 AS build
WORKDIR /src
COPY . .
RUN test "$(dotnet --version)" = "10.0.404" \
    && dotnet restore examples/Haby.RuntimeStartup.Consumer/Haby.RuntimeStartup.Consumer.csproj --configfile examples/Haby.RuntimeStartup.Consumer/NuGet.Config --locked-mode \
    && dotnet publish examples/Haby.RuntimeStartup.Consumer/Haby.RuntimeStartup.Consumer.csproj -c Release --no-restore -p:ContinuousIntegrationBuild=true -o /consumer
FROM mcr.microsoft.com/dotnet/runtime@sha256:342e2476e856efde687241821612b1d6792a76192ca71a28e31147314821dbb0
LABEL org.opencontainers.image.source="https://github.com/hamstix/haby"
WORKDIR /app
COPY --from=build /consumer .
ENTRYPOINT ["dotnet", "Haby.RuntimeStartup.Consumer.dll"]
