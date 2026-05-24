# HTTPS Production TLS Guide

This runbook covers production-style TLS deployment options for MemorySmith.

Use this guide for hosted environments. For local developer HTTPS, use [HTTPS Setup Guide](../guides/https-setup.md).

## 1. Decide Your TLS Termination Model

Pick one model before deployment:

1. Reverse proxy terminates TLS (IIS, Nginx, Caddy) and forwards HTTP to Kestrel.
2. Kestrel terminates TLS directly with a bound certificate.

Recommendation: use reverse proxy termination for easier certificate rotation and host hardening.

> [!NOTE]
> Screenshot placeholder [HTTPS-PROD-01]: deployment diagram showing reverse proxy and app host.

## 2. Certificate Requirements

- Use a certificate from a trusted CA for the production host name.
- Ensure private key permissions allow the service identity to read the key.
- Track certificate expiration with operational alerting.

> [!NOTE]
> Screenshot placeholder [HTTPS-PROD-02]: certificate details pane showing subject and expiration.

## 3. IIS Reverse Proxy (Windows)

1. Install IIS + ASP.NET Core Hosting Bundle.
2. Configure an IIS site binding for `https` with your production certificate.
3. Point the site/app pool to MemorySmith publish output.
4. Confirm `web.config` and ASP.NET Core Module are active.
5. Start site and validate HTTPS response.

> [!NOTE]
> Screenshot placeholder [HTTPS-PROD-03]: IIS site bindings dialog with HTTPS binding.
> [!NOTE]
> Screenshot placeholder [HTTPS-PROD-04]: successful `/health` over production host HTTPS.

## 4. Kestrel Direct TLS (No Reverse Proxy)

If reverse proxy is not used, configure Kestrel certificate binding in app settings or environment-specific config and expose only required ports.

Checklist:

- Bind HTTPS endpoint and certificate path/store reference.
- Restrict firewall to required inbound ports.
- Keep HTTP disabled or redirect HTTP to HTTPS.

> [!NOTE]
> Screenshot placeholder [HTTPS-PROD-05]: Kestrel HTTPS endpoint config snippet.

## 5. Forwarded Headers And Scheme Correctness

When TLS is terminated by a proxy, ensure forwarded headers are configured so app-generated links and auth flows preserve `https` scheme.

Validation checks:

- Login and callback paths stay on `https`.
- No mixed-content warnings in browser dev tools.
- API links and redirects keep HTTPS URLs.

> [!NOTE]
> Screenshot placeholder [HTTPS-PROD-06]: browser network trace confirming HTTPS redirects and no mixed content.

## 6. Security Verification Checklist

1. `https://<host>/health` reachable with valid certificate chain.
2. `https://<host>/login` and admin flows work end-to-end.
3. HSTS policy enabled at proxy/app policy layer as appropriate.
4. TLS versions/ciphers meet your org baseline.
5. Certificate renewal procedure tested before expiration window.

> [!NOTE]
> Screenshot placeholder [HTTPS-PROD-07]: SSL/TLS scan summary for production host.

## 7. Rollback Plan

Prepare rollback before cutover:

- Keep previous binding/certificate reference documented.
- Keep backup of prior proxy/site config.
- Define go/no-go validation window and responsible owner.

> [!NOTE]
> Screenshot placeholder [HTTPS-PROD-08]: deployment checklist with rollback owner and timestamps.

## Screenshot Backlog Template

- [ ] HTTPS-PROD-01 deployment diagram
- [ ] HTTPS-PROD-02 certificate details
- [ ] HTTPS-PROD-03 IIS HTTPS binding
- [ ] HTTPS-PROD-04 production `/health` over HTTPS
- [ ] HTTPS-PROD-05 Kestrel TLS endpoint config
- [ ] HTTPS-PROD-06 no mixed content validation
- [ ] HTTPS-PROD-07 TLS scan summary
- [ ] HTTPS-PROD-08 rollback checklist evidence
