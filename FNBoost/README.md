# FN Boost – Optimizer & Crosshair per Windows 11

App nativa per Windows 11 (C# / WPF, .NET 10) che:

1. **Analizza il PC** e segnala ciò che limita FPS, costanza dei frametime e stutter in Fortnite.
2. **Applica solo tweak sicuri e reversibili**: ogni valore originale viene salvato prima della modifica e si ripristina con un clic.
3. **Configura Fortnite** (GameUserSettings.ini) a gioco chiuso, con backup automatico.
4. **Mostra un mirino personalizzato** in una finestra overlay trasparente che non tocca il gioco.

Ha due interfacce:

- **Desktop completo**: finestra con barra laterale (Panoramica, Tweak, Fortnite, Mirino, Pulizia, Sicurezza, Guida).
- **Pannello flottante trasparente**: compatto, trascinabile, sempre in primo piano, con trasparenza regolabile. Mostra uso CPU/RAM e stato di Fortnite e ha i comandi rapidi per mirino e tweak. Si apre con `Ctrl+Alt+Z` o con il pulsante "Modalità flottante".

---

## Come ottenere l'exe

**Opzione A – GitHub Actions (senza installare niente)**
Ogni push compila automaticamente `FNBoost.exe`: apri il tab **Actions** del repository › *FN Boost – build Windows* › ultima esecuzione › artifact **FNBoost-win-x64**.

**Opzione B – compilare sul PC**
1. Installa il .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10`
2. Doppio clic su `FNBoost\build.cmd`
3. L'eseguibile è in `FNBoost\dist\FNBoost.exe` (singolo file, non serve installare .NET per usarlo).

Al primo avvio Windows SmartScreen può avvisare perché l'exe non è firmato: *Ulteriori informazioni › Esegui comunque*. L'app chiede i permessi di amministratore: servono per HKLM, piano energetico e punti di ripristino.

---

## Tweak inclusi

| Tweak | Cosa fa | Impatto | Rischio | Riavvio |
|---|---|---|---|---|
| Modalità Gioco | Priorità al gioco, niente installazioni driver o notifiche di Windows Update in partita | Medio | Nessuno | No |
| Game Bar: niente registrazione in background | Ferma la cattura continua che usa GPU, encoder e disco | Medio | Nessuno | No |
| Piano energetico "Prestazioni elevate" | Frequenze CPU stabili e niente core parking | Medio | Basso (consumi a riposo) | No |
| HAGS (pianificazione GPU hardware) | Richiesta per DLSS Frame Generation; effetto variabile | Variabile | Basso | Sì |
| Ottimizzazioni giochi in finestra | Flip model per DX10/11 in finestra (Fortnite è già DX12) | Basso | Nessuno | No |
| Fortnite su GPU ad alte prestazioni | Evita che il gioco finisca sulla GPU integrata (UHD 770) | Alto se l'iGPU è attiva | Nessuno | No |
| Priorità CPU "Alta" per Fortnite (IFEO) | Windows avvia il gioco con priorità alta. L'app non apre il processo | Basso | Basso | No |
| Priorità MMCSS giochi | SystemResponsiveness 10, NetworkThrottling off, classe Games alta | Basso | Nessuno | No |
| Disattiva accelerazione mouse | Solo desktop e menu (in partita Fortnite usa input raw) | Solo menu | Nessuno | No |
| File di paging gestito da Windows | Evita crash e freeze da memoria impegnata | Stabilità | Nessuno | Sì |

Sono contrassegnati come *Consigliato*: Modalità Gioco, Game Bar, piano energetico, HAGS, ottimizzazioni in finestra, GPU per Fortnite e file di paging.

### Cosa FN Boost NON fa, di proposito
- Non disattiva Secure Boot, TPM, IOMMU o VBS. **Epic li richiede per i tornei** e servono contro i malware.
- Non tocca le mitigazioni Spectre/Meltdown, Defender o Windows Update.
- Non applica tweak placebo o rischiosi: Nagle/TCP (Fortnite usa UDP), bcdedit HPET/tick, "RAM cleaner", debloat dei servizi, MPO via `OverlayTestMode` (non funziona più dalla 24H2), disattivazione delle ottimizzazioni schermo intero.
- Non rende l'ini di sola lettura e non svuota la cache shader in automatico (causerebbe più stutter).

---

## Mirino (crosshair)

- Forme: croce, croce + punto, punto, cerchio, cerchio + punto, T, X, oppure un'immagine PNG tua.
- Colore, opacità, lunghezza, spessore, distanza dal centro, bordo, offset X/Y e scelta del monitor.
- Preset integrati, più quelli che salvi tu. Anteprima con zoom su sfondi diversi.
- `Ctrl+Alt+X` lo accende e lo spegne. Opzione "mostra solo quando Fortnite è in primo piano".

**Come funziona:** è una normale finestra Windows trasparente, sempre in primo piano e "click-through" (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE`), disegnata in pixel fisici al centro del monitor. **Non** inietta DLL, **non** aggancia DirectX, **non** legge né scrive la memoria del gioco, **non** apre handle verso il processo di Fortnite e **non** invia input. È lo stesso principio del mirino integrato nell'OSD dei monitor.

**Rischio ban:** Epic non vieta esplicitamente i mirini overlay esterni, ma il regolamento proibisce software di terze parti che diano un vantaggio sleale. Nessun programma può garantire "zero ban" al 100%. Nei tornei con premi in denaro conviene spegnerlo o usare il mirino del monitor. Usa Fortnite in **Schermo intero in finestra** per essere sicuro che l'overlay sia visibile.

---

## Note sul tuo sistema (dalle info che hai inviato)

- **i5-13600K + Z790 AORUS ELITE AX, BIOS F16 (2026):** il BIOS è recente e include le correzioni microcode Intel per l'instabilità dei 13ª gen. Usa il profilo *Intel Default Settings*.
- **Page File Space 2,00 GB:** se è un valore **fisso**, è il primo sospettato per crash e micro-freeze (memoria virtuale disponibile solo 9,8 GB su 33,8). La Panoramica lo verifica e il tweak "File di paging gestito da Windows" lo sistema. Se è già gestito da Windows, cresce da solo e va bene così.
- **VBS in esecuzione ma senza servizi (HVCI spenta):** impatto trascurabile, va lasciato così.
- **Secure Boot attivo, Kernel DMA Protection (IOMMU) attiva:** sono requisiti per i tornei Fortnite. Non disattivarli.
- **RAM 32 GB:** la Panoramica legge la velocità reale. Se è DDR5 a 4800 MT/s e il kit è più veloce, attiva **XMP** nel BIOS (Tweaker › Extreme Memory Profile): con impostazioni basse Fortnite è limitato dalla CPU, e qui XMP è il guadagno più grande sugli 1% low.
- La GPU non era nelle info inviate: l'app la rileva da sola, insieme a versione e data del driver.

---

## Dove salva i dati

`%LOCALAPPDATA%\FNBoost\`
- `tweak-backups.json`: valori originali di ogni tweak applicato
- `ini-backups\`: copie di GameUserSettings.ini prima di ogni salvataggio
- `settings.json`: mirino, preset, scorciatoie, posizione del pannello
- `logs\fnboost.log`: registro di tutte le operazioni

Per annullare tutto: **Sicurezza e backup › Ripristina tutto**. In alternativa usa il punto di ripristino di Windows creato automaticamente prima del primo tweak.

---

## Fonti principali

- Epic: [requisiti anti-cheat PC (Secure Boot, TPM, IOMMU)](https://www.fortnite.com/news/new-fortnite-anti-cheat-pc-requirements-and-latest-legal-action), [perché servono TPM e Secure Boot](https://www.epicgames.com/help/en-US/c-Category_Fortnite/c-Fortnite_Competitive/why-do-i-need-to-enable-tpm-and-secure-boot-on-a-windows-pc-a000093838)
- Epic: [stutter in DirectX 12 e cache shader](https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/technical-support-c-202300000001719/fortnite-stutters-heavily-and-has-below-expected-performance-on-directx-12-a202300000018050), [modalità di rendering](https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/a202300000017914), [rimozione DirectX 11](https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/gameplay-c-202300000001721/has-the-directx-11-option-been-permanently-removed-from-rendering-mode-in-fortnite-a202300000083887), [limite FPS](https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/technical-support-c-202300000001719/how-do-i-adjust-my-fps-in-fortnite-if-it-is-locked-or-capped-a202300000010554)
- Microsoft: [ottimizzazioni per i giochi in finestra](https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952), [MPO su 24H2](https://learn.microsoft.com/en-us/answers/questions/3881462/how-can-i-disable-windows-mpo-in-2025-(24h2-window)
- [PCWorld su HAGS](https://www.pcworld.com/article/2339130/should-you-enable-hardware-accelerated-gpu-scheduling-in-windows-11.html), [Sportskeeda sui mirini e i ban](https://www.sportskeeda.com/fortnite/can-get-banned-custom-crosshairs-fortnite), [Dexerto: impostazioni Fortnite](https://www.dexerto.com/fortnite/best-fortnite-pc-settings-for-fps-graphics-2977102)
- Chiavi reali di GameUserSettings.ini: [wispurn/Fortnite-Optimized-Settings](https://github.com/wispurn/Fortnite-Optimized-Settings)

FN Boost non è affiliato a Epic Games o Microsoft. Fortnite è un marchio di Epic Games, Inc.
