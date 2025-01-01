FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:3fcf6f1e809c0553f9feb222369f58749af314af6f063f389cbd2f913b4ad556 AS build-env
WORKDIR /Noppelbot
COPY . ./
RUN dotnet restore
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/runtime:9.0.0@sha256:4fb8b01c8a0d06ac36c664d6c55630a7d11c971fc6f8575ca15bf0de1c67b46d
WORKDIR /Noppelbot
COPY --from=build-env /Noppelbot/out .
ENTRYPOINT ["./chatbot"]