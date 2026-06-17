# Security policy

## Supported versions

The current supported public line is `v1.x`.

## Reporting vulnerabilities

Please report security issues privately through the repository owner's normal
GitHub security contact path. Do not publish exploit details in a public issue
before maintainers have had time to review and mitigate.

## Data handling

Magic Mirror captures the pixels/text inside the overlay rectangle selected by
the user. AI translation and dictionary requests are sent to configured Sarmad
gateways:

- Primary: `GatewayBaseUrl + /api/sarmad/ask`, when configured.
- Optional fallback: `FallbackSarmadUrl`, only when explicitly configured.

MT fallback execution is off by default. If Sarmad cannot provide a usable
aligned translation, the app may show a per-run confirmation to translate the
whole capture through third-party no-key MT services (Google `gtx` and MyMemory).
The app must not silently mix MT into a Sarmad result. Decline MT for
confidential manuscripts, embargoed research, or publisher workflows.

Do not configure a gateway you do not trust for sensitive documents. The app
does not require API keys in the client repository.
