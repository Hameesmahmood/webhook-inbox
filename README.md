# Webhook Inbox

A self-hosted inbox that doesn't lose your webhooks. It stores each event before acknowledging it, delivers it to your app with retries, and lets you replay anything that failed.

**Status:** in progress. See the [implementation plan](docs/IMPLEMENTATION_PLAN.md).

## Run it

```bash
docker compose up --build
curl localhost:8080/readyz
```
