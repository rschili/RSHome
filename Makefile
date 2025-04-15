# Targets
.PHONY: build push

build:
	cd src && dotnet build

push: build
	./build_and_push.sh

test: build
	cd src && dotnet test