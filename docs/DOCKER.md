# Running in Docker

Gleanvolt as one container on any 64-bit Linux host with Docker — a home server, a mini PC, a
Raspberry Pi. The image is `ghcr.io/mpospisil/gleanvolt`; the same tag pulls the right build for
x86-64, arm64 and Windows. On a NAS, see [Running on a NAS](NAS.md) instead.

## Start it

Create the directories it keeps its data and logs in, owned by the image's user (uid 1654):

```bash
sudo mkdir -p /opt/gleanvolt/{data,logs}
sudo chown -R 1654:1654 /opt/gleanvolt/data /opt/gleanvolt/logs
```

```bash
docker run -d --name gleanvolt-controller --restart on-failure \
  --network host \
  -e TZ=Europe/Prague \
  -v /opt/gleanvolt/data:/app/data \
  -v /opt/gleanvolt/logs:/app/logs \
  ghcr.io/mpospisil/gleanvolt:latest
```

Then open **`http://<host>:8090`**, choose a password and let the
[setup wizard](WEB_UI.md#the-setup-wizard) find the inverter and the charger and set up the rest.
Everything it saves goes to `data/`, so it survives a new container. Its **Start Gleanvolt** button
restarts the container, which `--restart on-failure` brings straight back.

- **`--network host`** lets the wizard's search see your LAN, and puts the web UI on the host's port
  8090. On Docker's default bridge network, use `-p 8090:8090` instead and type your LAN's range into
  the search.
- **`--restart on-failure`**, not `unless-stopped` or `always`: with those, **Stop service** in the web
  UI restarts it at once.
- **`TZ`** is your time zone, so days and sessions are filed correctly. The wizard asks for it too.

Prefer Compose? [`deploy/docker-compose.nas.yml`](../deploy/docker-compose.nas.yml) runs the same
container and works with a plain `docker compose up -d` in its folder.

## Update and pin

```bash
docker pull ghcr.io/mpospisil/gleanvolt:latest
docker rm -f gleanvolt-controller      # then the docker run above again
```

To stay on a release, use its number as the tag — `:1.1.0`, with no `v`. A restart always comes back
with charging **Off**, so start charging again after an update. The tags are listed in
[Which image you get](../deploy/README.md#which-image-you-get).

## Stop and start

**Stop service** in the web UI, or `docker stop gleanvolt-controller`, stops it gracefully: the charger
is paused and the open session is saved. It stays down until `docker start gleanvolt-controller`. A
reboot brings it back. Avoid `docker kill`: nothing pauses the charger, so the car keeps drawing at the
last current.

## Optional: settings as environment variables

Anything the wizard sets can also be passed to `docker run`, with `:` written as `__`. What is saved in
the web UI wins over it. See [Configuration](CONFIGURATION.md):

```bash
  -e Pv__Inverter__Host=192.168.2.10 \
  -e Pv__Chargers__0__Host=192.168.2.6 \
  -e Pv__Latitude=50.08 -e Pv__Longitude=14.44 \
```

The password can be preset as a hash, which the image prints and exits:
`docker run --rm ghcr.io/mpospisil/gleanvolt:latest hash-password '<your password>'`, then
`-e Web__PasswordHash='<the hash>'`.

Forgot the password? **Forgot your password?** on the sign-in page, or from the host:
`docker run --rm -it -v /opt/gleanvolt/data:/app/data ghcr.io/mpospisil/gleanvolt:latest set-password`,
then `docker restart gleanvolt-controller`.

## Windows containers

The same tag pulls the Nano Server image on a Windows host. There is no host network there, so publish
the port with `-p 8090:8090`. .NET on Windows ignores `TZ`, so set the time zone in the wizard, or as a
Windows id: `-e Controller__TimeZone="Central Europe Standard Time"`.
