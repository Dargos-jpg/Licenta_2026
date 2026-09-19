# Bug Log — WiFi Signal Optimizer

Istoricul bug-urilor întâlnite în dezvoltare și modul de rezolvare.  
Format: **[Sesiune/Commit]** — Simptom → Cauză → Fix

---

## Sesiunea v2.1 — AI Improvements + UX Polish

### BUG-2025-001 — CS0103: 'GRID_STEP' does not exist după refactorizare ierarhică
**Simptom:** Build fail — `error CS0103: The name 'GRID_STEP' does not exist in the current context` în `RouterOptimizationService.cs` linia 502  
**Cauza:** Constanta `GRID_STEP = 10` a fost înlocuită cu `COARSE_STEP / FINE_STEP`, dar metoda `GenerateWeightedEvaluationPoints` folosea încă `GRID_STEP * 3` — necapturat de Find & Replace deoarece era într-un loc diferit  
**Fix:** Înlocuit `GRID_STEP * 3` cu `COARSE_STEP * 3` în `GenerateWeightedEvaluationPoints`  
**Lecție:** La redenumirea constantelor, verifică **toate** locurile de utilizare cu Grep, nu doar zona modificată

---

### BUG-2025-002 — Regresie performanță heatmap după IDW physics-informed
**Simptom:** Generarea heatmap-ului durează vizibil mai mult față de versiunea anterioară  
**Cauza:** `Math.Exp(wallPenalty / 30.0)` era apelat pentru fiecare pereche pixel-punct, inclusiv când `wallPenalty == 0` (cazul cel mai frecvent — fără pereți sau obiecte între punct și pixel)  
**Fix:** Guard de performanță: `double wallFactor = wallPenalty > 0.0 ? Math.Exp(wallPenalty / 30.0) : 1.0;`  
**Efect:** Performanța revine la nivelul original pentru proiecte fără pereți desenați; proiectele cu mulți pereți sunt puțin mai lente (acceptabil — calculul este fizic mai corect)  
**Lecție:** Funcțiile matematice transcendente (`exp`, `log`, `sqrt`) în inner loop-uri de milioane de iterații pot degrada semnificativ performanța; early-exit pentru cazul neutru (factor=1) este o optimizare sigură

---

### BUG-2025-003 — CS1626/CS1631: yield return în try-catch (adapter UAC elevation)
**Simptom:** Build fail — `error CS1626: Cannot yield a value in the body of a try block with a catch clause` și `error CS1631: Cannot yield a value in the body of a catch clause`  
**Cauza:** Prima implementare a UAC elevation folosea `yield return` direct în interiorul blocului `try-catch` — interzis în C# pentru async iteratoare  
**Fix:** Rezultatele (success/error) colectate în variabile locale (`elevOk`, `elevErrMsg`) în afara try-catch; `yield return` apelat după blocul try-catch  
**Lecție:** C# nu permite `yield return` / `yield break` în interiorul blocurilor `try-catch`; extrage rezultatele în variabile locale și fă yield după

---

### Potențiale Bug-uri Viitoare (OPEN)

#### OPEN-002 — Heatmap channel mismatch la reload proiect
**Risc:** La auto-load sesiune (v2.1), heatmap-ul este regenerat automat cu `SelectedNetwork.Channel`. Dacă proiectul salvat a avut un canal diferit de cel curent al rețelei (re-scanare după reload), parametrul `airLossPerMeter` poate fi inconsistent cu datele salvate.  
**Severitate:** Scăzută — nu afectează funcționalitatea, doar acuratețea vizuală a heatmap-ului  
**Status:** OPEN — de monitorizat; fix posibil prin salvarea `Channel` în `ProjectData`

#### OPEN-003 — Auto-load race condition la GenerateHeatmap pe startup
**Risc:** `TryAutoLoadLastProjectAsync` apelează `GenerateHeatmap()` la final dacă există date suficiente. Dacă utilizatorul interacționează cu UI-ul în timpul acestei generări asincrone (apasă un buton, schimbă rețeaua), starea poate deveni inconsistentă.  
**Severitate:** Scăzută — protejat parțial de `IsProcessing` overlay; generarea heatmap este single-threaded pe thread-ul UI  
**Status:** OPEN — de investigat dacă se manifestă în practică

#### OPEN-004 — QuestPDF export optimization: `GoodPercent`/`FairPercent`/`PoorPercent` pot lipsi din `CoverageStats`
**Risc:** `ExportOptimizationReport` accesează `before.GoodPercent`, `before.FairPercent`, `before.PoorPercent`. Dacă `CoverageStats` este generat de `CalculateRealStats` (varianta IDW din survey points), aceste câmpuri pot fi 0 dacă nu sunt calculați explicit.  
**Severitate:** Medie — PDF-ul ar arăta date incorecte (0%) fără eroare vizibilă  
**Status:** OPEN — de verificat că `RouterOptimizationService.CalculateRealStats` populează toate câmpurile din `CoverageStats`

#### OPEN-005 — Memory leak potențial în `TryAutoLoadLastProjectAsync` la apel repetat
**Risc:** `TryAutoLoadLastProjectAsync` trimite `WeakReferenceMessenger` messages și înregistrează strokes noi fără să curețe explicit listeners vechi. La un reload manual urmat de auto-load, ar putea exista duplicate listeners.  
**Severitate:** Scăzută — `WeakReferenceMessenger` folosește weak references, GC curăță automat  
**Status:** MONITORIZAT

---

## Sesiunea Post-Teză (v2.0)

### BUG-2024-001 — Build fail după eliminarea toggle-ului BASIC/EXPERT
**Simptom:** `XamlParseException` la pornire — `Grid.Row` cu valoare invalidă  
**Cauza:** Prima încercare de ștergere a row-ului toggle a marcat `Grid.Row="999_REMOVED"` (invalid în XAML)  
**Fix:** Citit blocul complet cu delimitatoarele corecte, înlocuire atomică a întregii secțiuni incluzând `<ScrollViewer Grid.Row="2"` → `Grid.Row="1"`  
**Lecție:** XAML nu permite atribute invalide; orice modificare structurală trebuie să fie completă și consistentă

---

### BUG-2024-002 — Tag-uri XAML neînchise după adăugare Beginner's Guide
**Simptom:** Build fail — `XamlParseException: unexpected end of element 'Grid'`  
**Cauza:** La adăugarea Border-ului „Beginner's Guide", StackPanel-ul interior și Border-ul exterior nu au fost închise corect (`</StackPanel></Border>` lipseau)  
**Fix:** `Read` pentru zona afectată → Edit cu structura completă corect închisă  
**Lecție:** La adăugare de containere WPF imbricate, verifică întotdeauna că fiecare element deschis are perechea de închidere

---

### BUG-2024-003 — CS8425 Warnings: `[EnumeratorCancellation]` lipsă
**Simptom:** 4× `warning CS8425: Async-iterator has CancellationToken parameter not decorated with [EnumeratorCancellation]`  
**Cauza:** Metodele `ShellStreamAsync`, `WifiCommandAsync`, `AdapterCommandAsync`, `DnsCommandAsync` — toateasync iteratoare cu `CancellationToken ct` fără atribut  
**Fix:** Adăugat `using System.Runtime.CompilerServices;` + `[EnumeratorCancellation]` pe fiecare parametru `ct`  
**Lecție:** Orice async iterator care expune `CancellationToken` ca parametru trebuie decorat cu `[EnumeratorCancellation]` pentru ca token-ul din `GetAsyncEnumerator()` să fie propagat corect

---

### BUG-2024-004 — CA2024: `EndOfStream` în async method
**Simptom:** `warning CA2024: Do not use proc.StandardOutput.EndOfStream in an async method`  
**Cauza:** Pattern clasic `while (!stream.EndOfStream)` blochează sincron thread-ul în context async  
**Fix:** Schimbat la `while ((line = await stream.ReadLineAsync(ct)) is not null)` — `ReadLineAsync` returnează `null` la EOF  
**Lecție:** În `async` context, folosește mereu variantele `Async` ale operațiunilor de I/O; `EndOfStream` e sincron și poate crea deadlock-uri cu `ConfigureAwait(false)`

---

## Sesiunea de Teză (v1.0 — commits istorice)

### BUG-HIST-001 — Heatmap verde se extinde infinit în afara casei
**Simptom:** Chiar și colțuri fără nicio măsurătoare apăreau verde intens  
**Cauza:** IDW pur fără penalizare distanță → puncte cu semnal bun dominau toată harta  
**Fix:** Adăugat `airLoss = distMeters * 2.5` (Free Space Path Loss simplificat 2.5 dBm/m)  
**Efect:** Heatmap se „contractă" natural în jurul punctelor de măsurare

---

### BUG-HIST-002 — Routerul sugerat mereu în centrul geometric al hărții
**Simptom:** Indiferent de survey points, optimizatorul recomanda centrul exact al imaginii  
**Cauza:** Scorul de uniformitate folosea valori dBm negative (ex: -65) ridicate la putere → valori enorme care dominau scorul; punctele bune și proaste aveau aceeași greutate  
**Fix:** Normalizat semnalul la `(signal + 90) / 60` → [0, 1] înainte de calcul scor; scor compus cu ponderi distincte pentru semnal minim, uniformitate, poziție

---

### BUG-HIST-003 — OptimizedHeatmapImage nu afișa nimic (dimensiune 0×0)
**Simptom:** View-ul Optimized era gol chiar și după optimizare reușită  
**Cauza:** `Image` din Optimized view nu avea `x:Name` → `SetOptimizedMapSize()` căuta `OptimizedFloorPlanImage` prin `FindName()` și returna null  
**Fix:** Adăugat `x:Name="OptimizedFloorPlanImage"` pe elementul `Image` din Optimized view  
**Lecție:** WPF `FindName()` necesită `x:Name` explicit; binding-ul la `Source` funcționează fără, dar manipularea prin cod-behind necesită name

---

### BUG-HIST-004 — Plan vechi rămânea vizibil după încărcarea unui plan nou
**Simptom:** Survey points, obstacole și strokes din sesiunea anterioară rămâneau pe canvas la `LoadMapAsync`  
**Cauza:** `LoadMapAsync` seta `FloorPlan = bitmap` fără a curăța starea vizuală  
**Fix:** Reset complet în `LoadMapAsync` înainte de a seta noul plan: `_surveyPoints.Clear()`, `_mapElements.Clear()`, `SurveyPointCount = 0`, `WeakReferenceMessenger.Send(new ClearSurveyPointsMessage())`, `WeakReferenceMessenger.Send(new ResetMapCanvasMessage())`

---

### BUG-HIST-005 — K-Means crash cu <3 canale disponibile
**Simptom:** `InvalidOperationException` în `ApplyKMeansClustering` pe rețele cu puțini vecini  
**Cauza:** K-Means cu `numberOfClusters: 3` pe mai puțin de 3 sample-uri  
**Fix:** Try/catch în jurul întregii logici ML; fallback la `Status = "⚪ Necunoscut"` dacă ML.NET eșuează  
**Lecție:** ML.NET nu validează că numărul de sample-uri ≥ k; validarea trebuie făcută manual sau învelită în try/catch

---

### BUG-HIST-006 — `DistanceToLineSegment` returna distanță greșită pentru segmente punctuale
**Simptom:** Obstacole cu X=Y=0 aveau atenuare aplicată eronat pe toată harta  
**Cauza:** Segment cu `lenSq=0` → împărțire la 0 la calculul lui `t`  
**Fix:** `if (lenSq == 0) return Distance(point, lineStart)` — segment punctual tratat ca punct simplu

---

### BUG-HIST-007 — Undo/Redo nu funcționa după `LoadProjectAsync`
**Simptom:** Ctrl+Z după încărcarea unui proiect salvat ducea la stare inconsistentă  
**Cauza:** `LoadProjectAsync` popula `_surveyPoints` direct fără să curețe `_undoStack` și `_redoStack`  
**Fix:** Reset explicit: `_undoStack.Clear(); _redoStack.Clear(); OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo));` la începutul `LoadProjectAsync`

---

### BUG-HIST-008 — Calibrare activată involuntar la click pe hartă
**Simptom:** Uneori, click-ul normal pe hartă declanșa modul calibrare  
**Cauza:** `_isCalibrating = true` rămânea setat dacă utilizatorul apăsa Escape după un StartCalibration inițiat din wizard dar nu finaliza  
**Fix:** `_isCalibrating = false; Mouse.OverrideCursor = null;` în handler-ul `Escape` din `Window_KeyDown`

---

### BUG-HIST-009 — InkCanvas captura mouse-ul și bloca pan/zoom harta
**Simptom:** Harta nu putea fi pan-uită dacă InkCanvas era vizibil (chiar și în mod None)  
**Cauza:** `InkCanvas.IsHitTestVisible = true` rămânea setat după dezactivarea drawing mode  
**Fix:** La orice schimbare de tool non-draw: `EditorCanvas.IsHitTestVisible = false` eliberează harta  
**Lecție:** WPF hit-testing se propagă prin layere; un element invizibil-logic dar cu `IsHitTestVisible=true` consumă toate event-urile mouse

---

### BUG-HIST-010 — Heatmap generat incorect după schimbarea rețelei selectate
**Simptom:** Heatmap-ul afișa semnalul rețelei precedente, nu al celei nou selectate  
**Cauza:** `GenerateHeatmap()` folosea `SelectedNetwork.SSID` snapshot-at la momentul apelului, dar binding-ul UI se actualiza asincron  
**Fix:** `RequestHeatmapGenerationMessage` transportă `TargetSSID` explicit — valoarea corectă e capturată la momentul declanșării comenzii, nu la execuție  
**Lecție:** În WPF async, nu te baza pe `SelectedNetwork` în closure-uri; capturează valorile relevante înainte de `await`

---

## Bugs Known / Nerezolvate

### OPEN-001 — Terminal: comenzile `adapter enable/disable` necesită UAC
**Simptom:** `netsh interface set interface ... enable` eșuează silențios dacă app-ul nu rulează ca Administrator  
**Status:** Nerezolvat — afișat mesaj de avertisment în output terminal (`"Requires administrator privileges. Executing..."`)  
**Plan:** La o versiune viitoare: detect privilege level la startup și solicita UAC elevation selectiv

---

### OPEN-002 — Heatmap `airLoss` nu este calibrat per bandă
**Simptom:** Pe rețele 5 GHz, heatmap-ul Normal supraestimează raza de acoperire (2.5 dBm/m e corect pentru 2.4 GHz, dar 5 GHz ar trebui ~4.0 dBm/m)  
**Status:** Known limitation — documentat, planificat pentru v2.1  
**Plan:** Detecta banda din `SelectedNetwork.Channel` și scala `airLoss` corespunzător
