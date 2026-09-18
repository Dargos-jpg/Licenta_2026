# WiFi Signal Optimizer

> **Network Audit Suite** — Desktop application for WiFi coverage analysis, router placement optimization, and network diagnostics.

Built as a Computer Science bachelor's thesis project at *Universitatea „Dunărea de Jos" din Galați* · **Grade: 10/10**

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-10.0-purple)
![WPF](https://img.shields.io/badge/UI-WPF-informational)
![License](https://img.shields.io/badge/license-MIT-green)

---

## What it does

WiFi Signal Optimizer is a professional-grade desktop tool that helps you understand, visualize, and improve your home or office WiFi coverage:

- **Scan** all nearby WiFi networks in real time (signal strength, channel, SNR, band)
- **Map** your floor plan and mark measurement points throughout your space
- **Generate** a color-coded WiFi signal heatmap using IDW interpolation + physics-based raycasting
- **Find** the optimal router placement using Log-Distance Path Loss modeling + multi-criteria AI optimization
- **Diagnose** network health with ML-powered channel analysis (K-Means clustering)
- **Manage** your network with an interactive terminal (ping, tracert, adapter management, DNS control, and more)

---

## Screenshots

> *(add screenshots here)*

| Heatmap View | Optimized View | Doctor WiFi | Network Terminal |
|:---:|:---:|:---:|:---:|
| Signal coverage map | AI-optimal router position | K-Means channel analysis | Interactive network CLI |

---

## Features

### WiFi Heatmap
- Real-time WiFi scanning via Windows native API (`wlanapi.dll`)
- Interactive floor plan canvas — upload an image or draw from scratch on a grid
- Survey point placement: click anywhere on the map to record signal at that location
- IDW interpolation (β=3.0) with 2D raycasting through walls and obstacles
- Calibration: drag a line between two known points to set the pixel-per-meter scale
- Dead zone overlay: highlights areas below a configurable signal threshold

### Router Optimization (AI)
- Log-Distance Path Loss (LDPL) physical model, calibrated per WiFi band:
  - 2.4 GHz (WiFi 4/5/6): range ~28m, path loss exponent 2.8
  - 5 GHz (WiFi 5/6): range ~18m, path loss exponent 3.3
  - 6 GHz (WiFi 6E/7): range ~10m, path loss exponent 3.7
  - WiFi 7 MLO: simulates all 3 bands simultaneously, takes best signal per pixel
- Grid search over the floor plan area + multi-criteria scoring:
  - Minimum signal coverage (30%)
  - Signal uniformity — variance across all evaluation points (25%)
  - Position quality — penalizes edges and corners (15%)
  - Consistency with real measurements — MAE between model and survey data (30%)
- TX power calibration from real measurements (inverted LDPL model)
- Before/After comparison: coverage percentages, average signal, uniformity score

### Doctor WiFi (ML)
- Channel interference analysis using logarithmic IEEE 802.11 model
- Adjacent channel interference for 2.4 GHz (ch±1 × 0.7, ch±2 × 0.3)
- K-Means clustering (ML.NET, k=3) classifying channels as: 🟢 Optimal / 🟡 Congested / 🔴 Critical
- Network health score (0–100) with classification: Excellent / Good / Acceptable / Poor / Critical
- Detected issues: overcrowded spectrum, hidden SSID, multi-AP roaming setup, marginal signal
- Smart recommendations: channel switch, band upgrade, router repositioning
- PDF report export with full analysis

### Network Terminal
An interactive command-line interface for network management — inspired by Cisco CLI style:

```
net ❯ help
net ❯ wifi list
net ❯ ping 8.8.8.8 -n 5
net ❯ adapter list
net ❯ dns flush
net ❯ gateway
net ❯ router open
```

**Available commands:** `ping`, `tracert`, `ipconfig`, `nslookup`, `netstat`, `arp`, `route`, `wifi`, `adapter`, `dns`, `gateway`, `router`, `whoami`, `hostname`, `sysinfo`  
**Features:** Tab autocomplete, ↑/↓ command history, live streaming output, Ctrl+C cancel, color-coded results

### Beginner's Guide (Wizard)
Step-by-step guide for first-time users: Scan → Load Plan → Calibrate → Draw Walls → Mark Locations → Generate Heatmap → Optimize Router

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| UI Framework | WPF (Windows Presentation Foundation) |
| Runtime | .NET 10 |
| Language | C# 13 (preview features) |
| Architecture | MVVM — CommunityToolkit.Mvvm 8.3.2 |
| Icons | MahApps.Metro.IconPacks 6.2.1 |
| WiFi Scanning | ManagedNativeWifi 2.1.0 |
| Machine Learning | ML.NET / Microsoft.ML 5.0.0 |
| PDF Export | QuestPDF 2026.2.4 |

---

## Architecture

```
├── Models/          Data models (WifiNetwork, SurveyPoint, MapElement, CoverageStats, ...)
├── Services/        Business logic (HeatmapService, RouterOptimizationService, WifiDoctorService, ...)
├── ViewModels/      MainViewModel — central MVVM hub with RelayCommands and ObservableProperties
├── Views/           MainWindow, DoctorPanelView, SysadminTerminalView, CalibrationDialog
├── Converters/      Value converters for XAML bindings
└── App.xaml         Global dark theme, slim scrollbars, tooltips
```

**Communication:** `WeakReferenceMessenger` from CommunityToolkit.Mvvm handles cross-component messages (heatmap generation, canvas redraw, save/load events) without tight coupling.

**Central view switching:** `CentralViewMode` enum (Normal / Optimized / Doctor / Sysadmin) drives which panel is visible in the main area.

---

## Requirements

- **OS:** Windows 10 / 11
- **Runtime:** [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- **WiFi adapter:** Required for scanning (any standard 802.11 adapter)
- **Admin rights:** Optional — needed only for `adapter enable/disable` and `dns set` commands in the terminal

---

## Building from source

```bash
git clone https://github.com/yourusername/wifi-signal-optimizer.git
cd wifi-signal-optimizer/Licenta
dotnet restore
dotnet build WifiSignalOptimizer.csproj
dotnet run --project WifiSignalOptimizer.csproj
```

Or open `Licenta.sln` in Visual Studio 2022+ and press F5.

---

## Project structure (docs)

| File | Description |
|------|-------------|
| [`docs/APP_OVERVIEW.md`](docs/APP_OVERVIEW.md) | Full technical documentation — architecture, algorithms, UI layout, data models |
| [`docs/CHANGELOG.md`](docs/CHANGELOG.md) | Version history and feature list by release |
| [`docs/BUGLOG.md`](docs/BUGLOG.md) | Bug history — symptoms, root causes, and fixes |

---

## Algorithms

| Algorithm | Used for | Complexity |
|-----------|----------|------------|
| IDW (β=3) | Heatmap interpolation from survey points | O(n·p) |
| Raycasting 2D | Wall/obstacle attenuation in heatmap | O(n·w) per pixel |
| LDPL | Signal simulation from known router position | O(p) per pixel |
| Grid Search | Find optimal router position | O(g·p) |
| K-Means (ML.NET) | Channel quality clustering | O(k·n·i) |
| TX Calibration | Fit LDPL model to real measurements | O(n) |

*n = survey points, p = pixels, w = wall segments, g = grid candidates, k = clusters, i = iterations*

---

## Academic context

This project was developed as a bachelor's thesis in Computer Science:

- **Title:** Sistem de Optimizare a Semnalului WiFi folosind Algoritmi de Machine Learning  
  *(WiFi Signal Optimization System using Machine Learning Algorithms)*
- **University:** Universitatea „Dunărea de Jos" din Galați, Facultatea de Automatică, Calculatoare, Inginerie Electrică și Electronică
- **Author:** Barbu Dragos-Andrei
- **Grade:** 10/10

---

## Roadmap

- [ ] **v2.1** — AI improvements: physics-informed IDW kernel, hierarchical grid search, Shannon capacity in Doctor WiFi
- [ ] **v2.2** — UX polish: view transition animations, loading spinners, export from optimization panel
- [ ] **v3.0** — Mobile companion app (.NET MAUI) — scan survey points from a phone and send to PC via local network API

---

## License

MIT License — see [LICENSE](LICENSE) for details.
