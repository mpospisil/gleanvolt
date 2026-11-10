# Finding your devices

Don't know the inverter's or the charger's address, or which port and unit id they speak Modbus on?
The setup wizard's **Search** finds them, and so does `gleanvolt discover` on an installed Gleanvolt.
On a machine with no Gleanvolt, the standalone `gleanvolt-discover` scans your LAN — from any laptop or
Pi on the same network. All three only ever read.

**Stop Gleanvolt first** (and Home Assistant's SolaX or `huawei_solar` integration, if you run it). A
SolaX device accepts very few Modbus TCP connections, and a Huawei EMMA only one, so while something else holds one the scan finds the port open and gets
no reply, and says so: *port open, no Modbus reply. Is Gleanvolt or Home Assistant connected to it right
now?*

```bash
# Build it once, self-contained: the result needs no .NET on the machine that runs it.
# linux-arm64 for a Pi, win-x64 for Windows.
dotnet publish src/Gleanvolt.Discovery.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o discover

./discover/gleanvolt-discover                           # every private interface's subnet, each capped at a /22
./discover/gleanvolt-discover --subnet 192.168.1.0/24   # or say which
./discover/gleanvolt-discover --host 192.168.1.50 --all-unit-ids
```

It tries ports 502 (Modbus TCP), 8899 (the usual RS485 bridge) and 80 (where it recognises a SolaX
dongle's web page, which is not Modbus, or a go-e's local API), unit ids `1,0,2,3,4,5,247`, and names each
box from its serial number (SolaX), its model name (Huawei SUN2000, with a Huawei EMMA noticed in front of
it), its phase voltages (Huawei SCharger) or its API (go-e), with the driver to use and the support tier
from [SUPPORTED_PV_SYSTEMS.md](SUPPORTED_PV_SYSTEMS.md). Under the
table it prints what it found in the form of configuration lines; enter the addresses on **Settings →
Installation** (or in the wizard), or optionally paste the lines into `.env`:

```
Pv__Inverter__Host=192.168.2.10
Pv__Inverter__Port=502
Pv__Inverter__UnitId=1
```

`--json` prints the same results as JSON, `--help` lists the rest (ports, unit ids, timeouts), and Ctrl+C
stops the scan and prints what it had found. Inside a bridged container the default subnet is the
container's own, not your LAN, even when the container reaches the devices fine: the tool says so, and
the fix is `--subnet` with your LAN's range, or running the container with `--network host`, as the
Pi's compose stack runs the controller.
