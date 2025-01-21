@echo off
setlocal enabledelayedexpansion

REM Path to your .env file
set ENV_FILE=.env

REM Define variables
set IMAGE_NAME=rshome:latest

REM Check if the .env file exists
if not exist "%ENV_FILE%" (
    echo .env file not found!
    exit /b 1
)

REM Read each line of the .env file
for /f "usebackq tokens=1,2 delims==" %%A in ("%ENV_FILE%") do (
    set "KEY=%%A"
    set "VALUE=%%B"
    set "!KEY!=!VALUE!"
)

REM Check if the DOCKER_REGISTRY_URL has been set
if "%DOCKER_REGISTRY_URL%"=="" (
    echo DOCKER_REGISTRY_URL is not set!
    exit /b 1
)

REM Build the Docker image
docker build -t %IMAGE_NAME% -f Dockerfile .
if errorlevel 1 (
    echo Docker build failed!
    exit /b 1
)

REM Tag the Docker image for the registry
docker tag %IMAGE_NAME% %DOCKER_REGISTRY_URL%/%IMAGE_NAME%

REM Push the Docker image to the registry
docker push %DOCKER_REGISTRY_URL%/%IMAGE_NAME%
if errorlevel 1 (
    echo Docker push failed!
    exit /b 1
)

echo Docker image %IMAGE_NAME% has been built and pushed to %DOCKER_REGISTRY_URL%

endlocal