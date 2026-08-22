# Minecraft Admin

.NET 10 Blazor Web App with an integrated HTTP API for managing the Minecraft server over RCON and editing a safe subset of `server.properties`.

## Current UI

- Dashboard with online/offline status and players
- Restart server (`say`, `save-all flush`, `stop`)
- Typed `server.properties` editor
- RCON console

## API

- `GET /api/server/status`
- `GET /api/server/players`
- `POST /api/server/command` body: `{ "command": "list" }`
- `POST /api/server/restart`
- `GET /api/config/server`
- `PUT /api/config/server`

## Minecraft Compose changes

Enable RCON on the existing Minecraft service:

```yaml
ENABLE_RCON: "TRUE"
RCON_PASSWORD: "${RCON_PASSWORD}"
```

Add `RCON_PASSWORD=<a long random value>` to the compose `.env` file.

Then merge `compose-service.yaml` into the existing Compose file. Because the web service uses `network_mode: service:wireguard`, it will be reachable from the VPN at:

`http://10.8.0.4:8080`

No host port needs to be published.

## Important

This first version intentionally has no Docker socket access. Restart is implemented by sending `stop` over RCON and relying on Minecraft's `restart: unless-stopped` Docker policy to start it again.

Authentication/authorization should be added before giving untrusted VPN users management access, especially because the current console page can execute arbitrary Minecraft console commands.
