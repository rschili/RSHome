# Targets
.PHONY: build push

build:
	cd src && dotnet build

push: build
	./build_and_push.sh

test:
	cd src/RSHome.Tests/ && dotnet run --disable-logo --output Detailed

test-integration:
	cd src/RSHome.Tests/ && dotnet run --treenode-filter /*/RSHome.Tests.Integration/*/* --disable-logo --output Detailed