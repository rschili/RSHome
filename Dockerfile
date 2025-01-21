FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:3fcf6f1e809c0553f9feb222369f58749af314af6f063f389cbd2f913b4ad556 AS build-env
WORKDIR /RSHome
COPY /src .

# Run tests
WORKDIR /RSHome/RSHome.Tests
RUN dotnet test --verbosity normal

# Publish the application
WORKDIR /RSHome/RSHome
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:9.0.0
WORKDIR /RSHome
COPY --from=build-env RSHome/RSHome/out .
ENTRYPOINT ["./RSHome"]