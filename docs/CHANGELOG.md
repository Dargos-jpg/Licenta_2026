# Changelog — WiFi Signal Optimizer

Toate modificările față de versiunea predată la teză (commit `60ed887 — Versiune finala licenta`).

---

## [v2.1.0] — AI Improvements + UX Polish + Stability

### Adăugat

#### Algoritmi AI îmbunătățiți
- **`Services/HeatmapService.cs`** — IDW Physics-Informed:
  - Parametru `int channel = 1` la `GenerateHeatmap` și `GenerateDeadZoneOverlay`
  - Atenuare per bandă: 2.5 dBm/m (2.4 GHz), 4.0 dBm/m (5 GHz), 5.5 dBm/m (6 GHz)
  - Pondere IDW Physics-Informed: `wallFactor = exp(wallPenalty/30)` — pereții reduc simultan semnalul și influența interpolării
  - Optimizare performanță: guard `wallPenalty > 0` evită `Math.Exp()` inutil (cazul comun fără pereți)

- **`Services/RouterOptimizationService.cs`** — Grid Search Ierarhic:
  - Înlocuit `GRID_STEP = 10` cu search ierarhic în 2 faze:
    - Faza 1 (Coarse): `COARSE_STEP = 40px` — scanează toată harta, reține Top-8 candidați
    - Faza 2 (Fine): `FINE_STEP = 5px` în raza de `FINE_RADIUS = 60px` în jurul fiecărui candidat
  - Helper `ScoreCandidate()` extras pentru claritate
  - Fixat `GenerateWeightedEvaluationPoints` să folosească `COARSE_STEP * 3` (nu `GRID_STEP`)

- **`Services/WifiDoctorService.cs`** — Algoritmi ML.NET și capacitate Shannon:
  - K-Means: normalizare feature-uri înainte de clustering (`/maxInterf`, `/maxCoCh`, `/maxAdj`)
  - Shannon capacity în loc de viteze hardcodate: `C = B·log₂(1+SNR)·η` (η=0.65 pentru 5/6GHz, 0.55 pentru 2.4GHz)

#### UX & UI
- **Loading overlay** — `IsProcessing`/`ProcessingMessage` cu spinner animat (`PackIconMaterial Kind="Loading"` + `RotateTransform`) vizibil pe tot panoul central în timp ce se generează heatmap sau optimizare
- **Animații view transitions** — `DoubleAnimation` + `CubicEase` fade-in la schimbarea modului central (Normal/Optimized/Doctor/Sysadmin)
- **ToolTips** pe toate cele 11 butoane din toolbox — descriere concisă a fiecărui instrument
- **Indicator simulator** — mesaj de status clar când WiFi-ul este simulat (`[SIMULAT]` prefix)

#### Export PDF Panoul de Optimizare
- **`Services/ReportExportService.cs`** — metodă nouă `ExportOptimizationReport(CoverageStats before, CoverageStats after)`:
  - Raport PDF dedicat optimizării cu scor înainte/după, tabel comparativ complet, distribuție calitate semnal pe categorii
  - Același design dark-theme cu Doctor WiFi PDF
- **`ViewModels/MainViewModel.cs`** — `ExportOptimizationReportCommand` (RelayCommand cu CanExecute = HasCompareStats)
- **`Views/MainWindow.xaml`** — buton PDF în headerul panoului „ANALIZĂ AI OPTIMIZARE" (verde, dezactivat până există statistici)

#### Auto-load ultimă sesiune la startup
- **`Services/ProjectService.cs`** — `SaveLastProjectPath()` / `GetLastProjectPath()` / `LoadProjectFromPath(string)`:
  - Calea ultimului proiect salvată în `%LocalAppData%\WifiSignalOptimizer\lastproject.txt`
  - `SaveProject` și `LoadProject` apelează automat `SaveLastProjectPath` după succes
  - `LoadProjectFromPath(string)` — încarcă fără dialog (pentru restaurare automată)
- **`ViewModels/MainViewModel.cs`** — `TryAutoLoadLastProjectAsync()`: la startup, dacă există o sesiune anterioară, oferă utilizatorului opțiunea Yes/No de restaurare
- **`Views/MainWindow.xaml.cs`** — `Loaded += async (_, _) => await _viewModel.TryAutoLoadLastProjectAsync()`

#### UAC Elevation pentru adapter enable/disable (OPEN-001)
- **`Services/SysadminCommandService.cs`**:
  - Helper `IsAdministrator()` via `WindowsPrincipal` + `WindowsBuiltInRole.Administrator`
  - `adapter enable/disable` detectează dacă aplicația rulează ca admin:
    - Dacă da: execuție directă streamată ca înainte
    - Dacă nu: lansează `cmd.exe` cu `Verb = "runas"` (UAC prompt) + mesaj informativ

### IWifiScannerService.cs / WifiScannerService.cs
- Proprietate `bool IsSimulated { get; }` — true când nu s-a găsit adaptor WiFi la startup

---

## [v2.0.0] — Post-teză Portfolio Release

### Adăugat

#### Network Terminal (Sysadmin View) — modul complet nou
- **`Services/SysadminCommandService.cs`** — motor de comenzi cu:
  - 17 comenzi: `help`, `clear`, `ping`, `tracert`, `ipconfig`, `nslookup`, `netstat`, `arp`, `route`, `wifi`, `adapter`, `dns`, `gateway`, `router`, `whoami`, `hostname`, `sysinfo`
  - Sub-comenzi: `wifi [scan|list|profiles|password <ssid>]`, `adapter [list|enable|disable <name>]`, `dns [show|flush|set <primary> [secondary]]`
  - Streaming live output `IAsyncEnumerable<TerminalLine>` — nu blochează UI-ul
  - Colorare automată output pe tip: verde=succes, roșu=eroare, galben=warning, albastru=info, violet=comandă, gri=muted
  - Dictionary static pentru autocomplete cu sub-comenzi per comandă
  - `[EnumeratorCancellation]` pe toate async iteratoarele pentru `Ctrl+C` corect
  - Cancel comanda în rulare: `CancellationTokenSource` + kill process tree

- **`Views/SysadminTerminalView.xaml`** — terminal UI cu:
  - Info bar: hostname, adaptor WiFi activ, indicator status (verde/roșu)
  - RichTextBox cu FlowDocument — output colorat per linie, paragraf Margin=0
  - ScrollViewer cu scrollbar slim 4px (același design cu cel global)
  - Panou sugestii autocomplete (apare dinamic sub input, max 120px height)
  - Prompt `net ❯` stilizat în albastru
  - Butoane: ↵ Run (albastru), ⏹ Stop (roșu, dezactivat când nu rulează), ✕ Clear
  - Font: Cascadia Code → Consolas → Courier New (fallback stack)

- **`Views/SysadminTerminalView.xaml.cs`** — logică terminal:
  - `Tab` → autocomplete (dacă e un singur match, completare directă; mai multe → afișează lista)
  - `↑/↓` → navigare history cu snapshot al liniei curente
  - `Ctrl+C` → cancel comanda activă
  - `Escape` → ascunde sugestiile
  - `Enter` → execută comanda
  - Delegate `GetNetworks` injectat din MainWindow pentru acces la rețelele scanate
  - `RefreshInfoBar()` la fiecare execuție de comandă (actualizează status WiFi)
  - Mesaj welcome la pornire

#### CentralViewMode.Sysadmin
- **`MainViewModel.cs`**: adăugat `Sysadmin` la enum `CentralViewMode`
- Proprietate `IsSysadminViewActive` + notificare în `OnCentralViewModeChanged`
- Comandă `SwitchToSysadminViewCommand` cu mesaj status

#### Meniu Vizualizare — opțiune Network Terminal
- **`MainWindow.xaml`**: buton „Network Terminal" în popup-ul „Vizualizare ▾"
  - Icon: `PackIconMaterial Kind="Console"`, culoare violet #A78BFA
  - Indicator ✓ activ legat la `IsSysadminViewActive`
  - Separator vizual față de Doctor WiFi

#### Handler și wireing
- **`MainWindow.xaml.cs`**: `OptionsMenuSysadmin_Click` → `SwitchToSysadminViewCommand`
- Injectare `SysadminTerminal.GetNetworks = () => _viewModel.VisibleNetworks` în constructor

### Modificat

#### UI — Eliminare toggle BASIC/EXPERT + adăugare Beginner's Guide
- **`MainWindow.xaml`**:
  - Eliminat `<RowDefinition Height="Auto"/>` (Row 1, pentru toggle)
  - Eliminat Border-ul cu butoanele `SetBasicMode_Click` / `SetExpertMode_Click`
  - Renumérotat `Grid.Row`: ScrollViewer central `2→1`, status bar `3→2`
  - Eliminat `DataTrigger` pentru `IsExpertMode` pe: buton Calibrate, indicator calibrare, grid auto-scan, lista Basic, DataGrid expert, expander ANALIZĂ AVANSATĂ
  - Expander „ANALIZĂ AVANSATĂ" acum mereu vizibil (nu mai depinde de `IsExpertMode`)
  - Adăugat Border „Beginner's Guide" în sidebar stânga (Row 2), cu buton care invocă `SetBasicMode_Click`
  - Header subtitle: `"Proiect Licență · v1.0"` → `"Network Audit Suite · v2.0"`
  - Adăugat intrare MOD 4 (SysadminTerminalView) în zona centrală

#### ViewModel — Expert mode by default
- **`MainViewModel.cs`**: `_isExpertMode = false` → `_isExpertMode = true`
  - Wizardul nu mai apare la pornire; accesibil via butonul „Beginner's Guide"

#### App.xaml — ScrollBar slim global
- **`App.xaml`**: Adăugat `Style TargetType="ScrollBar"` global:
  - Vertical: 6px wide, no arrows (RepeatButtons Opacity=0), thumb CornerRadius=3
  - Horizontal: 6px tall, aceeași logică
  - Culori thumb: default #3A3A52, hover #5A5A72, drag #3B82F6
  - Înlocuiește scrollbar-ul urât default Windows în toate ScrollViewer-ele din aplicație

---

## [v1.0.0] — Versiune finală teză (commit `60ed887`)

### Funcționalități complete la predare

#### Core
- Scanare rețele WiFi prin `ManagedNativeWifi` / `wlanapi.dll`
- Heatmap IDW (β=3.0) + raycasting prin pereți și obstacole
- Optimizare router LDPL + Grid Search + scor multi-criteriu
- Doctor WiFi: K-Means (ML.NET, k=3) + interferență logaritmică
- Calibrare scară Ekahau-style (drag pe hartă)
- Export PDF cu QuestPDF
- Salvare/Încărcare proiect JSON + ISF strokes

#### UI
- 3 moduri centrale: Normal, Optimized, Doctor
- Wizard 7 pași (Beginner mode)
- Toolbox cu 12 instrumente + snap-to-grid
- Undo/Redo (Ctrl+Z/Y)
- Comparație Before/After statistici acoperire
- Zone moarte overlay (threshold configurabil dBm)
- Pan/Zoom hartă (scroll + drag)
- Auto-scan cu interval configurabil
- Signal trend (↑/↓/stabil) + alert degradare

#### Algoritmi implementați (documentați în teză)
- IDW interpolation O(n·p) cu β=3
- Raycasting 2D (line-circle, line-line intersection)
- Log-Distance Path Loss (LDPL) parametrizat per bandă WiFi
- K-Means clustering (ML.NET) pentru clasificare canale
- Grid Search O(g·p) pentru optimizare poziție
- TX Power Calibration (inversarea LDPL din măsurători reale)
- WiFi 7 MLO simulation (max(2.4, 5, 6 GHz) per pixel)
