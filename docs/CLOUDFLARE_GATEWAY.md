# Dedicated Cloudflare Sarmad gateway

Magic Mirror uses a dedicated Worker gateway instead of the old documentation
gateway. The Worker exposes:

```text
POST /api/sarmad/ask
```

and calls Cloudflare Workers AI through the `AI` binding.

## Deploy

```powershell
cd cloudflare\magicmirror-sarmad-gateway
wrangler deploy
```

or from the repository root:

```powershell
.\scripts\deploy-gateway.ps1
```

The default model is:

```text
@cf/openai/gpt-oss-20b
```

Requests that still ask for the deprecated `@cf/openai/gpt-oss-120b` are
automatically migrated to `@cf/openai/gpt-oss-20b` by the gateway.

## Native app binding

Set **AI gateway base URL** in Magic Mirror settings to the Worker URL, without
the `/api/sarmad/ask` suffix. Example:

```text
https://magicmirror-sarmad-gateway.<your-subdomain>.workers.dev
```

The native app will post to:

```text
{GatewayBaseUrl}/api/sarmad/ask
```

## Security

No API keys are stored in this repository or in the native app. Cloudflare
Workers AI is accessed only through the server-side `AI` binding configured by
Wrangler.

If `wrangler deploy` returns `Authentication error [code: 10000]`, refresh the
local Cloudflare OAuth session:

```powershell
wrangler logout
wrangler login
.\scripts\deploy-gateway.ps1
```
