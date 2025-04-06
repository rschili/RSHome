FROM  mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build-env
RUN apk update && apk --no-cache add ca-certificates tzdata icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /RSHome
COPY /src .

# Run tests
WORKDIR /RSHome/RSHome.Tests
RUN dotnet test --verbosity normal

# Publish the application
WORKDIR /RSHome/RSHome
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
RUN apk update && apk --no-cache add ca-certificates tzdata icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
WORKDIR /RSHome
COPY --from=build-env RSHome/RSHome/out .
ENTRYPOINT ["./RSHome"]