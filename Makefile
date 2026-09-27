# Vexillum build entry points. Everything delegates to the .NET SDK (`dotnet`),
# which is the compiler, dependency manager (NuGet) and test runner in one.
#
#   make            compile everything (Debug)
#   make test       run the unit tests (Tests/Vexillum.Tests: shims, terrain oracle)
#   make acceptance run the acceptance tests (real server + scripted protocol clients)
#   make e2e        run the end-to-end tests (real game windows; needs a display)
#   make test-all   unit + acceptance
#   make server     compile, then run the dedicated server from Test/
#   make client     compile, then run the game from Test/ (main menu)
#   make play       compile, then run the game and join the server at CONNECT
#   make dist       self-contained release build for RID into dist/<rid>/
#   make clean      remove build output
#
# Variables: CONFIG=Debug|Release  ROOT=Test  CONNECT=127.0.0.1:24224  RID=osx-arm64
# Requires: .NET SDK 8 or newer (https://dotnet.microsoft.com/download), nothing else.

CONFIG  ?= Debug
ROOT    ?= Test
CONNECT ?= 127.0.0.1:24224
RID     ?= $(shell dotnet --info 2>/dev/null | awk '/RID:/{print $$2}')
TFM     ?= net9.0

SLN        := Vexillum.sln
SERVER_EXE := Server/bin/$(CONFIG)/$(TFM)/VexillumServer
CLIENT_EXE := ZombieSurvival/bin/$(CONFIG)/$(TFM)/VexillumGame
ABS        := $(CURDIR)

.PHONY: all build restore test acceptance e2e test-all clean server client play smoke dist release check help

all: build

## Compile every project in the solution (restores NuGet packages first).
build:
	dotnet build $(SLN) -c $(CONFIG) -nologo

## Only download/verify NuGet dependencies.
restore:
	dotnet restore $(SLN)

## Run the unit tests (shims, terrain fidelity oracle, Nuclex port). Fast, no processes started.
test: build
	dotnet test Tests/Vexillum.Tests -c $(CONFIG) -nologo --no-build

## Run the acceptance tests: the real server in scratch copies of Test/ driven by scripted protocol clients (docs/TESTING.md).
acceptance: build
	dotnet test Tests/Vexillum.Acceptance -c $(CONFIG) -nologo --no-build

## Run the end-to-end tests: real server + real game windows through the debug console (needs a display; opens windows).
e2e: build
	python3 -m pytest Tests/e2e -m e2e -x -q

## Unit tests followed by the acceptance tests.
test-all: test acceptance

## Compile in Release configuration.
release:
	$(MAKE) build CONFIG=Release

## Remove build output for all projects.
clean:
	dotnet clean $(SLN) -c $(CONFIG) -nologo
	find . -type d \( -name bin -o -name obj \) -not -path './.claude/*' -not -path './Test/*' -prune -exec rm -rf {} +
	rm -rf dist

## Run the dedicated server (headless) with ROOT as its working directory.
server: build
	cd $(ROOT) && $(ABS)/$(SERVER_EXE)

## Run the game as shipped: main menu, join via "Start Playing".
client: build
	cd $(ROOT) && $(ABS)/$(CLIENT_EXE)

## Run the game and join the server at CONNECT as soon as the menu loads.
play: build
	cd $(ROOT) && $(ABS)/$(CLIENT_EXE) --connect $(CONNECT)

## Loopback smoke test: server + two clients, checks logs and takes a screenshot.
smoke: build
	python3 .claude/mcp/vexillum_dev.py smoke_test seconds=40 clients=2

## Build, protocol/format invariants and preservation check in one go.
check: build
	python3 .claude/mcp/vexillum_dev.py invariant_check
	python3 .claude/mcp/vexillum_dev.py preservation_check

## Self-contained, single-folder distribution for one platform (no .NET install needed to run it).
## Output: dist/<RID>/ with the runtime directory (Content, Maps, Server config) copied in.
dist:
	@test -n "$(RID)" || { echo "set RID, e.g. make dist RID=osx-arm64 | linux-x64 | win-x64"; exit 2; }
	dotnet publish ZombieSurvival/Vexillum.csproj -c Release -r $(RID) --self-contained -o dist/$(RID) -nologo
	dotnet publish Server/Server.csproj           -c Release -r $(RID) --self-contained -o dist/$(RID) -nologo
	cp -R $(ROOT)/Content $(ROOT)/Maps $(ROOT)/Server $(ROOT)/settings.xml $(ROOT)/controls.xml dist/$(RID)/
	rm -f dist/$(RID)/Server/debug_server.log
	@echo "dist/$(RID): run ./VexillumServer and ./VexillumGame from inside that folder"

help:
	@awk '/^## /{c=c (c?" ":"") substr($$0,4); next} /^[a-z][a-z0-9-]*:/{if(c){printf "  %-11s %s\n", substr($$1,1,length($$1)-1), c}; c=""} {if($$0 !~ /^## /) c=(c && $$0 ~ /^[a-z]/) ? c : c}' $(MAKEFILE_LIST)
