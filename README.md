# Bevdox — DevBox RDP Launcher

A fast, keyboard-driven terminal UI for discovering and launching [Microsoft Dev Box](https://learn.microsoft.com/en-us/azure/dev-box/).

## Features

- **Zero-config authentication** — uses `DefaultAzureCredential` with WAM broker, picking up your current Windows identity automatically
- **Automatic discovery** — finds all your Dev Box instances across every DevCenter project in your Azure subscriptions via Azure Resource Graph + DevCenter data plane APIs
- **Instant startup** — cached instance list loads immediately; background refresh happens in parallel so you never wait
- **Keyboard-driven menu** — arrow keys, number keys (1–9), Enter to launch, with highlighted selection
- **ms-avd protocol launch** — opens the selected Dev Box in Windows Remote Desktop Client (supports running, hibernated, and stopped instances)
- **Instance renaming** — assign friendly display names that persist across refreshes
- **OneDrive-synced config** — cache file stored in `.config/bevdox` under your OneDrive folder, available on any machine

## Requirements

- Windows 10/11 (required for WAM broker authentication)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later
- [Windows Remote Desktop Client](https://learn.microsoft.com/en-us/azure/virtual-desktop/users/connect-windows) (for `ms-avd://` protocol handling)
- An Azure account with access to one or more DevCenter projects

## Getting Started

```powershell
git clone <repo-url>
cd bevdox
dotnet run --project Bevdox
```

On first run, Bevdox will:
1. Authenticate via WAM (a Windows sign-in dialog may appear)
2. Discover all DevCenter projects and query each for your dev boxes
3. Cache the results and present the interactive menu

## Usage

```
Bevdox - DevBox RDP Launcher

  Cache: 3 min ago  │ ⟳ Done — found 4 dev box(es).

  #   Name                           Project              State        Location
  ─   ─                              ─                    ─            ─
> 1   ado-2510                       azuredevops          Running      australiaeast
  2   copilot-ws-2504                yourproject          Hibernated   westus3
  3   my-devbox                      anotherproject       Running      westus3
  4   test-box                       testproject          Stopped      eastus

  ↑↓ Navigate  Enter/1-9 Launch  Esc Cancel launch  N Rename  R Reload  Q Quit
```

| Key | Action |
|-----|--------|
| `↑` / `↓` | Move selection up/down |
| `Enter` | Launch selected Dev Box |
| `1`–`9` | Launch Dev Box by number |
| `Esc` | Cancel a pending launch |
| `N` | Rename selected instance |
| `R` | Force reload of instance list (ignored while a refresh is already running) |
| `Q` | Quit |

## How It Works

### Discovery

Bevdox discovers your dev boxes in two steps:

1. **Azure Resource Graph** — queries for all `microsoft.devcenter/projects` resources across your subscriptions to find DevCenter endpoints
2. **DevCenter data plane** — calls `GET {devCenterUri}/projects/{project}/users/me/devboxes` on each project (max 4 in parallel) to list your personal dev boxes

### Caching

The instance list is cached as JSON at `{OneDrive}/.config/bevdox`. The cache includes:
- Last refresh timestamp
- All discovered instances with their state, project, location, and pool
- User-assigned display names (preserved across refreshes)

On startup, if the cache is available, it's shown immediately. A background refresh runs automatically; if the cache is older than 1 hour, a fresh discovery is triggered. The merge logic protects against empty results (e.g., from auth failures) overwriting a valid cache. If a background refresh fails, the error is shown in the status line until the next refresh.

The config location is resolved in order: `%OneDriveCommercial%` → `%OneDrive%` → `%USERPROFILE%`.

### Authentication

Uses [`DefaultAzureCredential`](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/credential-chains#defaultazurecredential-overview) with the WAM (Web Account Manager) broker enabled via `Azure.Identity.Broker`. This silently picks up your Windows identity without requiring manual login, API keys, or Azure CLI.

Two token scopes are used:
- `https://management.azure.com/.default` — for Azure Resource Graph queries
- `https://devcenter.azure.com/.default` — for DevCenter data plane API calls

Authentication happens on the background thread during discovery, so it never blocks menu interaction when a cached instance list is available. The status line shows which step is running (authenticating to Azure Resource Manager or Dev Center, querying Resource Graph, querying projects) and, once a step takes longer than a couple of seconds, how long it has been waiting. Projects that fail to respond keep their previously cached dev boxes; a summary with the first error is shown in the status line.

### Launching

When you select a Dev Box, Bevdox calls the DevCenter [remote connection API](https://learn.microsoft.com/en-us/rest/api/devcenter/developer/dev-boxes/get-remote-connection) to get the `rdpConnectionUrl` and opens it via `ms-avd://` protocol handler, which launches the Windows Remote Desktop Client. Hibernated and stopped instances are auto-started by the remote desktop client.

The launch runs in the background: the menu stays responsive, the status line shows the current step (authenticating, requesting the connection, opening Remote Desktop), and `Esc` cancels it. If the launch fails, the error is shown in the status line and you stay in the menu.

## Project Structure

```
bevdox/
├── Bevdox.slnx                        # Solution file
├── Bevdox/
│   ├── Bevdox.csproj                   # .NET 9 console app, Windows TFM
│   ├── Program.cs                      # Entry point, TUI loop, user interaction
│   ├── Auth/
│   │   └── AuthService.cs              # DefaultAzureCredential + WAM broker
│   ├── Models/
│   │   ├── BevdoxConfig.cs             # Config model + JSON source generator
│   │   └── DevBoxInstance.cs           # Dev box data model
│   └── Services/
│       ├── ConfigStore.cs              # OneDrive-based JSON persistence
│       ├── DevBoxDiscovery.cs          # ARG + DevCenter data plane discovery
│       └── DevBoxLauncher.cs           # Remote connection + ms-avd launch
└── README.md
```

## License

See [LICENSE](LICENSE) for details.
