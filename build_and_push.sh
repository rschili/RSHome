#!/bin/bash

# Path to your .env file
ENV_FILE=".env"

# Define variables
IMAGE_NAME="rshome:latest"

# Check if the .env file exists
if [ ! -f "$ENV_FILE" ]; then
    echo ".env file not found!"
    exit 1
fi

# Export variables from the .env file
export $(grep -v '^#' $ENV_FILE | xargs)

# Check if the DOCKER_REGISTRY_URL has been set
if [ -z "$DOCKER_REGISTRY_URL" ]; then
    echo "DOCKER_REGISTRY_URL is not set!"
    exit 1
fi

# Build the Docker image
docker buildx build -t $IMAGE_NAME .
if [ $? -ne 0 ]; then
    echo "Docker build failed!"
    exit 1
fi

# Tag the Docker image for the registry
docker tag $IMAGE_NAME $DOCKER_REGISTRY_URL/$IMAGE_NAME

# Push the Docker image to the registry
docker push $DOCKER_REGISTRY_URL/$IMAGE_NAME
if [ $? -ne 0 ]; then
    echo "Docker push failed!"
    exit 1
fi

echo "Docker image $IMAGE_NAME has been built and pushed to $DOCKER_REGISTRY_URL"