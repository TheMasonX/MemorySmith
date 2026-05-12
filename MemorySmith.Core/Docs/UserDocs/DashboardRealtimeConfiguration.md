# Dashboard Real-Time Configuration

This document describes the runtime knobs for dashboard/worker real-time behavior.

## Dashboard (`MemorySmith.Dashboard/appsettings*.json`)

### `WorkerApiBaseUrl`
Base URL for REST API calls.

Example:
```json
"WorkerApiBaseUrl": "http://localhost:5234"
```

### `WorkerHubUrl`
SignalR hub URL used for live updates.

Example:
```json
"WorkerHubUrl": "http://localhost:5234/hubs/dashboard"
```

### `StatsPollingSeconds`
Polling fallback interval used by Health page when live updates are unavailable (and as resilience refresh).

- Must be a positive integer.
- Invalid or missing values fall back to 10 seconds.

Example:
```json
"StatsPollingSeconds": 10
```

## Worker (`MemorySmith.Worker/appsettings*.json`)

### `DashboardOrigin`
CORS origin allowed for dashboard browser clients.

Example:
```json
"DashboardOrigin": "https://localhost:7001"
```

### `StatsBroadcastSeconds`
Background interval for periodic `ReceiveStats` SignalR pushes.

- Must be a positive integer.
- Invalid or missing values fall back to 10 seconds.

Example:
```json
"StatsBroadcastSeconds": 10
```

## Tuning guidance

- Start with `10` seconds for both polling and broadcast.
- Increase interval to reduce traffic, decrease interval to improve freshness.
- Keep dashboard polling and worker broadcast in the same range for predictable UI behavior.
