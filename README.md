# VianaRego Edge Platform - Command Reference

## Quick Start

```powershell
# Start Cloud API only (recommended local loop)
.\rego start cloud

# Build MSI installer
.\installer-build.ps1

# Optional: clean build artifacts before a fresh build
.\clean-all.ps1

# Install MSI locally (Admin shell)
Start-Process msiexec.exe -Wait -ArgumentList '/i ".\VianaRego.Installer\Release\VianaRego.Installer.msi"'
```

---

## Main Scripts

### Development and Building
| Command | Description |
|---------|-------------|
| `.\rego start cloud` | Run Cloud API only (recommended) |
| `.\manage.ps1 start cloud` | Same as above, without wrapper |
| `.\start-cloud.ps1` | Underlying Cloud watcher script used by `rego start cloud` |
| `.\installer-build.ps1` | Build the MSI installer |
| `.\clean-all.ps1` | Remove build/MSI artifacts for a clean rebuild |

### Security and Provisioning
| Command | Description |
|---------|-------------|
| `.\generate-keys.ps1` | Generate RSA signing keys |
| `.\rotate-signing-keys.ps1 -BackupEnv` | Rotate signing keys in `.env` with backup |
| `Start-Process msiexec.exe -Wait -ArgumentList '/i ".\VianaRego.Installer\Release\VianaRego.Installer.msi"'` | Install MSI locally (requires Admin shell) |

---

## Management CLI (`manage.ps1`)

### Device Management
```powershell
.\manage.ps1 list                               # List all registered devices
.\manage.ps1 dashboard                          # Live system dashboard (refreshes every 3s)
.\manage.ps1 get [device-id]                    # Device details + modules
.\manage.ps1 hardware [device-id]               # Hardware info + peripherals
.\manage.ps1 prune-devices [hours]              # Dry-run stale offline devices (default: 24h)
.\manage.ps1 prune-devices [hours] --apply      # Delete stale device records from cloud registry
```

`prune-devices` only removes entries from the Cloud registry. It does not uninstall software from edge devices.

### Module Management
```powershell
.\manage.ps1 add-module [device-id] [name] [version] [download-url] [start-command]
.\manage.ps1 start-module [device-id] [name]
.\manage.ps1 start-module [device-id] [name] --active
.\manage.ps1 stop-module [device-id] [name]
.\manage.ps1 kill-process [device-id] [name]
.\manage.ps1 remove-module [device-id] [name]
.\manage.ps1 remove-module [device-id] --all
```

`start-module` launch modes:
- default: service/session-0 launch (background/non-interactive).
- `--active`: requests launch in active user session (interactive desktop app window).

`stop` vs `kill` behavior:
- **`stop-module`**: Persists a "Stopped" intent to the Cloud. The Edge Watchdog will **actively enforce** this, killing any matching processes if they appear until a `start-module` is sent.
- **`kill-process`**: Sends a one-time kill signal. It does **not** update the persistent state. Use this for ad-hoc cleanup of unmanaged processes.
- **`remove-module`**: 
    - Managed/downloaded modules are uninstalled.
    - Adopted/local apps are only un-managed (original app files are not deleted).
- **`revive-module`**:
    - Starts the module **once** immediately.
    - Sets persistent status to **"Manual"**.
    - Watchdog will **ignore** this module (won't kill it, won't auto-restart it). Useful for ad-hoc debugging where you want the process running but not supervised.


### Discovery and Adoption
```powershell
.\manage.ps1 inventory [device-id]
.\manage.ps1 inventory [device-id] --all
.\manage.ps1 adopt-discovered [device-id] [process-name]
.\manage.ps1 adopt-local [device-id] [module-name]
.\manage.ps1 adopt-local [device-id] [module-name] [executable-path]
.\manage.ps1 convert-to-managed [device-id] [module-name] [download-url]
```

### Monitoring and Logs
```powershell
.\manage.ps1 logs [device-id] [module-name]
.\manage.ps1 logs [device-id] [module-name] 500
.\manage.ps1 logs [device-id] [module-name] [lines] --follow
```

### Advanced Operations
```powershell
.\manage.ps1 clone-state [source-device-id] [target1] [target2]
.\manage.ps1 clone-state [source-device-id] [target1] [target2] --replace
.\manage.ps1 rollback [device-id] [module-name]
```

`clone-state` behavior for adopted modules:
- Managed modules are copied as-is.
- Adopted modules are auto-remapped per target using that device's reported process paths.
- If no matching process is found (for example Brave is not present/running), that adopted module is skipped with a warning.
- If multiple different candidate paths are found, clone skips that module and warns instead of guessing.
- `--replace` clears target desired-state modules first, then applies the cloned set.

---

## Example Workflows

### Testing MSI Installation
The MSI installer is now **self-stopping**. It uses WiX `util:CloseApplication` logic to automatically detect and terminate any running Edge Agent, Watchdog, or Telemetry processes before upgrading or uninstalling. This prevents "File in Use" prompts and reduces the need for reboots.

```powershell
# 1. Build installer
.\installer-build.ps1

# 2. Install MSI (Admin shell)
Start-Process msiexec.exe -Wait -ArgumentList '/i ".\VianaRego.Installer\Release\VianaRego.Installer.msi"'

# 3. Run cloud only
.\rego start cloud

# 4. Verify device connection
.\manage.ps1 list
```

---

## Reliability & Performance

The platform includes several advanced reliability features:

### 1. Robust Path Resolution
The Watchdog handles unquoted executable paths with spaces (common in `C:\Program Files\`). It uses an iterative algorithm to correctly resolve the binary even when arguments are passed without quotes.

### 2. High-Performance Non-Blocking Logging
The `SharedLogger` uses a background `ConcurrentQueue` and worker thread. Log calls return immediately (low latency), and writes to the SQLite database are handled asynchronously in the background.

### 3. Active Desired State Enforcement
- The **Edge Agent** reconciles local state with the Cloud every 30 seconds.
- The **Watchdog** monitors processes every 5 seconds. If a module is marked "Stopped", the Watchdog actively ensures all matching processes are killed, preventing "zombie" processes or accidental restarts.

### 4. Sidecar Supervision
The Edge Agent manages the **Watchdog** and **Telemetry** services as sidecars. If they exit unexpectedly, the Agent restarts them with the correct working directory and configuration.

### Clean Rebuild
```powershell
# Remove build/MSI artifacts only
.\clean-all.ps1

# Optional: also clear runtime data and logs
.\clean-all.ps1 -IncludeRuntimeData -IncludeLogs

# Build fresh installer
.\installer-build.ps1
```

### Adopt Existing App
```powershell
# 1. Discover running processes
.\manage.ps1 inventory device-02

# 2. Adopt one process
.\manage.ps1 adopt-discovered device-02 MyApp

# 3. Verify module state
.\manage.ps1 get device-02
```

---

## Architecture Overview

```text
Cloud API (http://localhost:5074, SQLite: data/viana-cloud.db)
    |
    | MQTT
    v
Edge Device
  - Edge Agent (reconciler, desired-state enforcement)
  - Watchdog (process monitoring and auto-restart)
  - Local DB: data/viana-edge.db
```

## File Structure

```text
VianaRego2/
|-- src/
|   |-- VianaRego.Cloud.Api/
|   |-- VianaRego.Edge.Agent/
|   |-- VianaRego.Edge.Telemetry/
|-- start-cloud.ps1
|-- installer-build.ps1
|-- clean-all.ps1
|-- manage.ps1
|-- rego.ps1
|-- rego.cmd
|-- rego-edge.ps1
|-- rego-edge.cmd
`-- README.md
```
