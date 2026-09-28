# Vexillum build entry points. Everything delegates to the .NET SDK (`dotnet`),
# which is the compiler and dependency manager (NuGet) in one.
#
#   make            compile everything (Debug)
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
TFM     ?= net8.0

SLN        := Vexillum.sln
SERVER_EXE := Server/bin/$(CONFIG)/$(TFM)/VexillumServer
CLIENT_EXE := ZombieSurvival/bin/$(CONFIG)/$(TFM)/VexillumGame
ABS        := $(CURDIR)

.PHONY: all build restore clean server client play dist release help

all: build

## Compile every project in the solution (restores NuGet packages first).
build:
	dotnet build $(SLN) -c $(CONFIG) -nologo

## Only download/verify NuGet dependencies.
restore:
	dotnet restore $(SLN)

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
