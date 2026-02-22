.PHONY: build release setcap

build: release setcap

release:
	@dotnet build BPSR-ZDPS/BPSR-ZDPS.csproj -c Release -f net9.0 -v minimal

setcap:
	@sudo setcap cap_net_raw=+ep $(realpath ./BPSR-ZDPS/bin/Release/net9.0/BPSR-ZDPS)
