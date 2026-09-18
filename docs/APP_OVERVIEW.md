# WiFi Signal Optimizer — Documentație Completă Aplicație

> **Proiect licență** · Barbu Dragos-Andrei · Universitatea „Dunărea de Jos" din Galați  
> **Notă obținută:** 10/10  
> **Stack:** WPF · .NET 10 · C# · MVVM · ML.NET

---

## 1. Structura proiectului

```
Licenta/
├── Models/
│   ├── WifiNetwork.cs          — rețea WiFi detectată (SSID, BSSID, dBm, canal, SNR, calitate)
│   ├── SurveyPoint.cs          — punct de măsurare pe hartă (X, Y, timestamp, rețele detectate)
│   ├── MapElement.cs           — obstacol plasat pe hartă (tip, poziție, penalizare semnal)
│   ├── HeatmapPoint.cs         — punct simplificat pentru interpolare (X, Y, SignalStrength)
│   ├── NetworkHealthReport.cs  — raport complet diagnostic WiFi (scor, recomandări, canale)
│   ├── CoverageStats.cs        — statistici acoperire (excellent/good/fair/poor/dead %, avg dBm)
│   ├── OptimizationReport.cs   — raport optimizare router
│   └── ProjectData.cs          — serializar JSON proiect salvat
│
├── Services/
│   ├── WifiScannerService.cs       — scanare rețele via ManagedNativeWifi / wlanapi.dll
│   ├── IWifiScannerService.cs      — interfață pentru scanner (testabilitate)
│   ├── HeatmapService.cs           — generare heatmap IDW + raycasting
│   ├── RouterOptimizationService.cs — optimizare poziție router (LDPL + Grid Search)
│   ├── WifiDoctorService.cs        — diagnostic AI rețea (K-Means clustering ML.NET)
│   ├── MapService.cs               — încărcare/salvare imagine plan
│   ├── IMapService.cs              — interfață pentru map service
│   ├── ProjectService.cs           — salvare/încărcare proiect JSON + ISF strokes
│   ├── ReportExportService.cs      — export PDF cu QuestPDF
│   └── SysadminCommandService.cs   — [NOU] motor comenzi terminal de rețea
│
├── ViewModels/
│   └── MainViewModel.cs        — ViewModel central (MVVM, 1100+ linii)
│
├── Views/
│   ├── MainWindow.xaml/.cs     — fereastra principală
│   ├── DoctorPanelView.xaml/.cs — panou diagnostic embedded (UserControl)
│   ├── DoctorReportWindow.xaml/.cs — fereastră raport detaliată
│   ├── CalibrationDialog.xaml/.cs  — dialog calibrare scară (metri)
│   └── SysadminTerminalView.xaml/.cs — [NOU] terminal de rețea interactiv
│
├── Converters/
│   ├── InvertBooleanConverter.cs
│   ├── NullToVisibilityConverter.cs
│   ├── InvertNullToVisibilityConverter.cs
│   ├── SignConverter.cs
│   └── WizardStepBrushConverter.cs
│
├── App.xaml/.cs                — resurse globale, stiluri, scrollbar slim
└── WifiSignalOptimizer.csproj  — .NET 10, WPF, pachete NuGet
```

---

## 2. Dependențe NuGet

| Pachet | Versiune | Utilizare |
|--------|----------|-----------|
| `CommunityToolkit.Mvvm` | 8.3.2 | `[ObservableProperty]`, `[RelayCommand]`, `WeakReferenceMessenger` |
| `MahApps.Metro.IconPacks` | 6.2.1 | Iconițe `PackIconMaterial` (Console, Stethoscope, Router, etc.) |
| `ManagedNativeWifi` | 2.1.0 | Scanare rețele WiFi reale prin `wlanapi.dll` |
| `Microsoft.ML` | 5.0.0 | K-Means clustering pentru Doctor WiFi |
| `QuestPDF` | 2026.2.4 | Export rapoarte PDF |

---

## 3. Arhitectură MVVM

### Flux de date
```
WifiScannerService (hardware) 
    → MainViewModel.VisibleNetworks (ObservableCollection)
    → UI DataGrid (binding)

MapCanvas (click) 
    → RecordPointCommand 
    → _surveyPoints (List<SurveyPoint>)
    → WeakReferenceMessenger → RequestHeatmapGenerationMessage
    → HeatmapService.GenerateHeatmap()
    → MainViewModel.HeatmapOverlay (ImageSource)
    → HeatmapImage (binding)
```

### Enum-uri centrale
```csharp
public enum CentralViewMode { Normal, Optimized, Doctor, Sysadmin }
public enum WizardStep { ScanWifi=0, LoadPlan, Calibrate, DrawWalls, MarkLocations, GenerateHeatmap, OptimizeRouter, Done }
public enum ElementType { Router, Fridge, Microwave, ConcreteWall, BrickWall, WoodDoor, TV }
```

### Mesaje WeakReferenceMessenger
| Mesaj | Sender | Receiver | Scop |
|-------|--------|----------|------|
| `ClearSurveyPointsMessage` | ViewModel | MainWindow | Șterge vizual punctele de survey |
| `RedrawSurveyPointsMessage` | ViewModel | MainWindow | Redesenează punctele (undo/redo) |
| `RedrawMapElementsMessage` | ViewModel | MainWindow | Redesenează obstacolele |
| `RestoreSketchMessage` | ViewModel | MainWindow | Restaurează stroke-urile InkCanvas |
| `RequestSaveWithSketchMessage` | ViewModel | MainWindow | Declanșează salvare cu schiță |
| `RequestHeatmapGenerationMessage` | ViewModel | MainWindow | Generează heatmap async |
| `RequestOptimalRouterMessage` | ViewModel | MainWindow | Calculează poziție optimă async |
| `ResetMapCanvasMessage` | ViewModel | MainWindow | Reset complet canvasuri |
| `StartCalibrationFromWizardMessage` | ViewModel | MainWindow | Pornește mod calibrare |
| `ActivateDrawLineForCalibrationMessage` | ViewModel | MainWindow | Activează tool linie dreaptă |

---

## 4. Funcționalități detaliate

### 4.1 Scanare WiFi
- **Serviciu:** `WifiScannerService` via `ManagedNativeWifi`
- **Date returnate per rețea:** SSID, BSSID, dBm, canal, calitate %, SNR estimat, BandLabel (2.4G/5G/6G)
- **Auto-scan:** `DispatcherTimer` cu interval configurabil (2-60s)
- **Signal history:** `Queue<int>` per SSID, ultimele 10 valori → trend (↑/↓/stabil)
- **Alert degradare:** dacă semnalul scade >10 dBm față de baseline

### 4.2 Heatmap Normal (IDW + Raycasting)
- **Algoritm:** Inverse Distance Weighting, β=3.0
- **Air loss:** 2.5 dBm/metru (simplificat, independent de bandă)
- **Raycasting:** pentru fiecare pixel → verifică intersecția cu obstacole (cercuri r=20px) și pereți desenați (segmente)
- **Penalizare pereți:** +10 dBm per perete desenat intersectat
- **Step:** pixel 2×2 pentru performanță (rezoluție efectivă ½)
- **Fade:** alpha scade sub -80 dBm, dispare la -90 dBm
- **Paletă:** verde (#00C800) → galben-verde → galben → portocaliu → roșu (#FF0000)

### 4.3 Optimizare Router (LDPL + Grid Search)
- **Model fizic:** Log-Distance Path Loss (LDPL)
  - `signal = TX - PL(d0) - 10·n·log10(d/d0) - wallLoss - obstacleLoss - σ/2`
- **Parametri per bandă:**
  - 2.4 GHz: n=2.8, wallPenalty=5 dBm, TX=20 dBm, range=28m
  - 5 GHz: n=3.3, wallPenalty=8 dBm, TX=23 dBm, range=18m
  - 6 GHz: n=3.7, wallPenalty=12 dBm, TX=23 dBm, range=10m
  - WiFi 7 MLO: max(2.4+5+6 GHz), TX=26 dBm, range=30m
- **Grid Search:** step=10px, candidați limitați la bounding box survey points ±30px margine
- **Scor compus (4 criterii):**
  - minSignal normalizat: 30% (cu măsurători) / 50% (fără)
  - uniformitate (1/(1+var/100)): 25% / 35%
  - poziție (penalizare margini/colțuri): 15%
  - consistență cu măsurători reale (MAE): 30% / 0%
- **Calibrare TX:** inversarea LDPL din măsurători reale → TX calibrat la 10-33 dBm
- **Statistici:** ExcellentPercent, GoodPercent, FairPercent, PoorPercent, DeadZonePercent, AverageSignal, UniformityScore, AvgWallCount

### 4.4 Doctor WiFi (K-Means ML.NET)
- **Detectare bandă:** canal ≤14 → 2.4 GHz, 15-177 → 5 GHz, >177 → 6 GHz
- **Model interferență:** logaritmic IEEE 802.11 `weight = 10^((dBm+100)/20)`
- **Adjacent channel interference (2.4 GHz):** ch±1 × 0.7, ch±2 × 0.3
- **K-Means:** k=3, seed=0, features: [InterferenceScore, CoChannelNeighbors, AdjacentNeighbors]
- **Clasificare clustere:** 🟢 Optim / 🟡 Aglomerat / 🔴 Critic (ordonate după interference medie)
- **Scor sănătate 0-100:** penalizări pentru semnal slab + interferențe + congestie, bonus 5G
- **Clasificare:** Excelent (≥85) / Bun (≥70) / Acceptabil (≥50) / Slab (≥30) / Critic
- **Estimare viteză:** theoretical 866/300 Mbps × signalFactor × congestionFactor
- **Probleme detectate:** spectru aglomerat, SSID ascuns, multi-AP, semnal marginal

### 4.5 Network Terminal (Sysadmin)
- **17 comenzi:** help, clear, ping, tracert, ipconfig, nslookup, netstat, arp, route, wifi, adapter, dns, gateway, router, whoami, hostname, sysinfo
- **Sub-comenzi:** wifi [scan/list/profiles/password], adapter [list/enable/disable], dns [show/flush/set]
- **Streaming live:** `IAsyncEnumerable<TerminalLine>` — output linie cu linie fără blocare UI
- **Colorare automată:** verde=succes, roșu=eroare, galben=warning, albastru=info, violet=comandă
- **Tab autocomplete:** primul cuvânt → comenzi, al doilea → sub-comenzi
- **History:** ↑/↓ navigare, snapshot linie curentă la navigare
- **Ctrl+C:** cancel comanda în rulare (`CancellationToken`)
- **Info bar:** hostname, adaptor WiFi activ, status conectivitate

### 4.6 Wizard (Beginner's Guide)
- **7 pași:** ScanWifi → LoadPlan → Calibrate → DrawWalls → MarkLocations → GenerateHeatmap → OptimizeRouter → Done
- **Auto-advance:** la fiecare pas când condiția e îndeplinită (ex: ≥2 rețele → avansează la LoadPlan)
- **Acces:** buton „Beginner's Guide" în sidebar stânga
- **Implicit dezactivat:** `IsExpertMode = true` by default

### 4.7 Calibrare scară
- **Metodă Ekahau-style:** drag pe hartă între 2 puncte cu distanță cunoscută
- **Dialog:** `CalibrationDialog` — utilizatorul introduce distanța în metri
- **Rezultat:** `PixelsPerMeter = pixelDistance / realMeters`
- **Efect:** toate calculele de distanță și LDPL devin calibrate fizic

### 4.8 Toolbox (Trusa de Unelte)
- **Instrumente:** SurveyPoint, Pointer/Navigate, Router, ConcreteWall, BrickWall, WoodDoor, Fridge, TV, Microwave, DrawWall (InkCanvas), DrawLine (segment drept snap-to-grid), Eraser
- **Snap to grid:** linii drepte se fixează la grilă 20px
- **Drag & drop:** obstacolele pot fi mutate pe canvas după plasare
- **Undo/Redo:** Ctrl+Z / Ctrl+Y pentru survey points și strokes

### 4.9 Modul Optimized View
- **Afișare:** heatmap simulat din poziția optimă calculată de AI
- **Comparație Before/After:** statistici paralelă cu heatmap-ul normal
- **Indicator router:** cerc verde animat + icon router + label „POZIȚIE OPTIMĂ"
- **Pan/Zoom:** scroll wheel + drag

### 4.10 Export PDF
- **Bibliotecă:** QuestPDF 2026.2.4
- **Conținut:** scor sănătate, statistici acoperire, canale K-Means, recomandări, probleme detectate
- **Acces:** buton din DoctorPanelView

### 4.11 Salvare/Încărcare Proiect
- **Format:** JSON + ISF (ink strokes) + PNG base64 (plan)
- **Serviciu:** `ProjectService`
- **Restaurare completă:** survey points, obstacole, calibrare, schiță, rețele

---

## 5. UI Layout

```
┌─────────────────────────────────────────────────────────────┐
│ HEADER: Logo | Rețea selectată | Vizualizare▾ | Zone Moarte  │
├──────────────┬──────────────────────────────┬───────────────┤
│   SIDEBAR    │     ZONA CENTRALĂ            │   TOOLBOX     │
│   STÂNGA     │                              │   (col. dreaptă│
│              │  [Normal/Optimized/Doctor/   │   doar Normal) │
│  - Lista     │   Sysadmin Terminal]         │               │
│    rețele    │                              │  Instrumente  │
│  - Doctor    │                              │  Obstacole    │
│    WiFi btn  │                              │  Analiză      │
│  - Calibrate │                              │  Avansată     │
│  - Heatmap   │                              │               │
│  - Beginner  │                              │               │
│    Guide     │                              │               │
│  - Status    │                              │               │
└──────────────┴──────────────────────────────┴───────────────┘
```

### CentralViewMode switching
- **Meniu hover** „Vizualizare ▾" în header-ul central
- **Opțiuni:** Heatmap Normal | Heatmap Optimizat | Doctor WiFi | Network Terminal
- **Indicator activ:** ✓ lângă opțiunea curentă

---

## 6. Modele de date cheie

### WifiNetwork
```csharp
SSID, BSSID, SignalStrength (dBm), Channel
// Computed:
SignalQuality (0-100%), EstimatedSnr, BandLabel (2.4G/5G/6G), ShortBssid, SnrQuality
```

### SurveyPoint
```csharp
double X, Y
List<WifiNetwork> DetectedNetworks
DateTime Timestamp
```

### MapElement
```csharp
ElementType Type  // Router, Fridge, Microwave, ConcreteWall, BrickWall, WoodDoor, TV
double X, Y
int SignalPenalty  // Router=0, Fridge=15, Microwave=8, ConcreteWall=12, BrickWall=6, WoodDoor=3, TV=5
```

### CoverageStats (record)
```csharp
double ExcellentPercent, GoodPercent, FairPercent, PoorPercent, DeadZonePercent
double AverageSignal, UniformityScore, AvgWallCount
int TotalSamples
// Computed: OverallScore, EstimatedThroughputMbps
```

---

## 7. Stilizare globală (App.xaml)

- **Temă:** dark, custom (nu MahApps window chrome)
- **Culori principale:** BgBase #0A0A0E, BgPanel #0D0D14, BgCard #101015, BgHover #1A1A2A
- **Accent:** #3B82F6 (blue-500)
- **Text:** Primary #F0F0F8, Secondary #9090B0, Muted #5A5A72
- **ScrollBar slim global:** 6px wide, no arrows, thumb #3A3A52 → hover #5A5A72 → drag #3B82F6
- **ToolTip dark:** background #1A1A2A, border #3B82F6
