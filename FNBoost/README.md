# FN Boost – Optimizer & Crosshair per Windows 11

App nativa per Windows 11 (C# / WPF, .NET 10) che:

1. **Analizza il PC** e segnala ciò che limita FPS, costanza dei frametime e stutter in Fortnite.
2. **Applica solo tweak sicuri e reversibili**: ogni valore originale viene salvato prima della modifica e si ripristina con un clic.
3. **Configura Fortnite** (GameUserSettings.ini) a gioco chiuso, con backup automatico. Modalità *Performance* = `PreferredRHI=dx11` + `PreferredFeatureLevel=es31` (gli stessi valori che scrive il gioco: nel log compare "RHI D3D11 with Feature Level ES3_1 … will be used"); *DirectX 12* = `dx12` + `sm6`.
4. **Mostra un mirino personalizzato** a livelli in una finestra overlay trasparente che non tocca il gioco.
5. **Misura gli FPS** (media, 1% low, 0,1% low, min/max, stutter) con un overlay in gioco, registra le sessioni e le analizza nel tempo.
6. **Misura ping, jitter, perdita di pacchetti e traffico del gioco** e crea un **report diagnostico** da condividere, già ripulito dai dati personali.

Ha due interfacce:

- **Desktop completo**: finestra con barra laterale (Panoramica, Tweak, Fortnite, Mirino, Prestazioni, Pulizia, Sicurezza, Guida).
- **Pannello flottante trasparente**: compatto, trascinabile, sempre in primo piano, con trasparenza regolabile. Mostra uso CPU/RAM, stato di Fortnite, FPS dal vivo (media, 1% low, 0,1% low, min, max e mini grafico dei frametime) e ha i comandi rapidi per mirino, overlay FPS, registrazione e tweak. Si apre con `Ctrl+Alt+Z` o con il pulsante "Modalità flottante".

### Scorciatoie globali (predefinite)

| Scorciatoia | Azione |
|---|---|
| `Ctrl+Alt+X` | Mirino on/off |
| `Ctrl+Alt+Z` | Pannello flottante |
| `Ctrl+Alt+F` | Overlay FPS on/off |
| `Ctrl+Alt+C` | Preset del mirino successivo (il nome compare per un attimo sotto al mirino) |
| `Ctrl+Alt+R` | Avvia/ferma la registrazione di una sessione |

Si cambiano in **Sicurezza e backup › Scorciatoie da tastiera**: clicca la casella e premi la nuova combinazione (serve almeno Ctrl, Alt o Shift; Backspace/Canc la disattiva). Le combinazioni doppie o già prese da un'altra app vengono segnalate. Usano `RegisterHotKey` di Windows: nessun hook della tastiera.

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
| Ottimizzazioni giochi in finestra | Flip model per i giochi DX10/11 in finestra: conta per Fortnite in modalità Performance (Direct3D 11) in *Schermo intero in finestra*; nessun effetto in DirectX 12 | Variabile | Nessuno | No |
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

- **A livelli**: linee interne, linee esterne, bracci singoli (es. T senza braccio in alto), punto centrale (quadrato o tondo), cerchio, bordo e bagliore. Ogni livello ha dimensione, spessore, opacità e colore propri (vuoto = colore principale).
- **Modelli rapidi** (croce, croce + punto, punto, cerchio, cerchio + punto, T, X, immagine PNG tua) da cui partire, poi si ritocca tutto. Rotazione libera, opacità globale, offset X/Y e scelta del monitor.
- **Colore RGB** cangiante con velocità regolabile (animato solo a schermo, mai salvato).
- **Visibilità intelligente**: solo con Fortnite in primo piano; nascosto quando il cursore è visibile (inventario, mappa, lobby, menu); nascosto mentre miri col tasto destro, anche in modalità "mira alternata". Legge solo quale finestra è in primo piano, se il cursore è visibile e se il tasto destro è premuto (API di Windows in sola lettura).
- **Preset**: 11 integrati più quelli che salvi tu. `Ctrl+Alt+C` passa al successivo e ne mostra il nome sotto al mirino.
- **Codici di condivisione** (`FNB1-…`): esporta il tuo mirino in un codice da incollare in chat e importa quelli degli amici. Il codice contiene solo l'aspetto: **mai** il percorso dell'immagine (rivelerebbe cartelle e nome utente), monitor, posizione o regole di visibilità. I codici danneggiati, troppo lunghi o con colori non validi vengono rifiutati o ripuliti.
- **Anteprima** con zoom su sfondi diversi, oppure su un tuo screenshot di gioco con riquadro 1:1 alla dimensione reale.
- Le impostazioni salvate dalla prima versione vengono convertite in automatico nel nuovo modello a livelli, con lo stesso aspetto di prima.
- `Ctrl+Alt+X` lo accende e lo spegne.

**Come funziona:** è una normale finestra Windows trasparente, sempre in primo piano e "click-through" (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE`), disegnata in pixel fisici al centro del monitor. **Non** inietta DLL, **non** aggancia DirectX, **non** legge né scrive la memoria del gioco, **non** apre handle verso il processo di Fortnite e **non** invia input. È lo stesso principio del mirino integrato nell'OSD dei monitor.

**Rischio ban:** Epic non vieta esplicitamente i mirini overlay esterni, ma il regolamento proibisce software di terze parti che diano un vantaggio sleale. Nessun programma può garantire "zero ban" al 100%. Nei tornei con premi in denaro conviene spegnerlo o usare il mirino del monitor. Usa Fortnite in **Schermo intero in finestra** per essere sicuro che l'overlay sia visibile.

---

## Contatore FPS e analisi delle prestazioni

**Come misura.** FN Boost legge gli eventi *Present* di DXGI da una sessione **ETW** di sistema (Event Tracing for Windows): è lo stesso metodo di **PresentMon**, CapFrameX e del contatore FPS della **Xbox Game Bar**. Il tempo tra due Present consecutivi dello stesso processo è il frametime. Non c'è alcun contatto con il gioco: niente DLL iniettate, niente hook di DirectX, niente handle verso il processo, niente lettura della memoria. CPU e RAM arrivano dai contatori di Windows (PDH), GPU e VRAM dai contatori "GPU Engine"/"GPU Adapter Memory" quando disponibili. Di base misura solo Fortnite; si può scegliere "qualsiasi gioco in primo piano".

**Cosa significano i numeri** (stesse definizioni di PresentMon/CapFrameX):

| Valore | Definizione |
|---|---|
| **Media** | frame ÷ tempo totale. È la media "vera", non la media degli FPS istantanei |
| **1% low** | FPS calcolati dalla media dell'1% dei frametime più lunghi. Misura i rallentamenti che senti davvero |
| **0,1% low** | come sopra con lo 0,1% dei frame più lenti: i singoli scatti più gravi |
| **P1** | FPS al 99° percentile dei frametime (1000 ÷ P99). Più stabile dell'1% low su sessioni brevi |
| **Min / Max** | 1000 ÷ frametime più lungo / più corto |
| **Stutter** | frame lungo almeno 2,5 volte la mediana dei 60 frame precedenti **e** almeno 12 ms (sensibilità regolabile). Mostrato anche come stutter al minuto |
| **Consistenza** | punteggio 0-100: quanto l'1% low resta vicino alla media (≥ 95% della media = pieno punteggio), meno una penalità per gli stutter frequenti |

**Gioco fuori fuoco.** Quando Fortnite non è la finestra in primo piano (per esempio mentre avvii o fermi la registrazione dalla finestra di FN Boost) il gioco si limita da solo a ~30 FPS. Circa 10 volte al secondo FN Boost controlla quale finestra è in primo piano (solo `GetForegroundWindow` + `GetWindowThreadProcessId`, nessun handle verso il gioco): i frame presentati fuori fuoco, quelli a cavallo del cambio e ~0,5 s di assestamento al ritorno **non entrano** in finestra mobile, statistiche, stutter e campioni al secondo. FPS dal vivo, overlay e pannello mostrano "fuori fuoco" invece del numero; nei grafici quei secondi sono buchi (bande grigie nel report). La registrazione automatica parte solo con il gioco in primo piano da almeno 5 s e il tempo fuori fuoco non conta per la durata minima. Le sessioni registrate prima di questa misura con un tratto iniziale/finale a ~30 FPS e GPU sotto il 10% vengono corrette nell'analisi e nel report (stima, dichiarata come tale).

**Overlay in gioco.** `Ctrl+Alt+F` mostra un piccolo contatore sopra al gioco: FPS grandi, media, 1% low (giallo se scende sotto metà della media), 0,1% low, min/max, frametime, CPU/GPU e un mini grafico dei frametime. Angolo, monitor, distanza dal bordo, dimensione del testo, colori, sfondo, righe visibili e layout compatto su una riga sono configurabili. Di base si vede solo con il gioco in primo piano (e mentre configuri FN Boost). È una finestra trasparente click-through come il mirino.

**Sessioni e storico.** Con la registrazione automatica ogni partita (da quando Fortnite inizia a renderizzare a quando smette) diventa una sessione; in alternativa `Ctrl+Alt+R` o il pulsante *Avvia registrazione*. Le sessioni più corte del minimo impostato non vengono salvate. Ogni sessione conserva tutti i frametime, un campione al secondo (FPS, 1% low, CPU, GPU, RAM, VRAM), i tweak FN Boost attivi, modalità di rendering e limite FPS letti da GameUserSettings.ini e la frequenza del monitor. La pagina **Prestazioni** mostra l'elenco con etichette modificabili (es. "dopo tweak", "DX12"), l'andamento delle ultime sessioni, e per ogni sessione 16 statistiche, FPS nel tempo, CPU contro GPU e l'istogramma dei frametime. La Panoramica riassume l'ultima sessione.

**Analisi automatica.** Per ogni sessione FN Boost spiega cosa vede: gioco fluido o irregolare, FPS fermi al limite impostato o agganciati al refresh (VSync), probabile limite GPU (GPU al 95-100%) o CPU (GPU scarica e FPS bassi), molti stutter (con distinzione tra quelli dei primi minuti, tipici della compilazione shader in DX12, e quelli sparsi, tipici di attività in background), VRAM o RAM quasi piene. Confronta poi la sessione con la precedente dello stesso gioco (media, 1% low, stutter, tweak cambiati) e segnala cali o miglioramenti nel tempo.

**Esportazione CSV.** Ogni sessione si esporta in CSV (`index,time_ms,frametime_ms,fps,focused`, un frame per riga) da aprire in Excel o CapFrameX. Sono esportati tutti i frame: `focused = 0` indica quelli esclusi dalle statistiche perché il gioco era fuori fuoco.

**Limiti, onestamente:**
- Misura i frame **presentati dal gioco** (l'equivalente di *MsBetweenPresents* di PresentMon), non quelli effettivamente mostrati dal monitor: con VSync, G-Sync/FreeSync o Frame Generation le due cose possono differire. Non misura la latenza.
- L'overlay è una finestra sopra al gioco: in alcuni casi Windows deve comporre il frame del gioco con l'overlay invece di mostrarlo direttamente (perdendo il "flip indipendente"), con un possibile piccolo aumento di latenza. Accendilo quando ti serve e spegnilo quando giochi sul serio. Il contatore senza overlay non ha questo effetto.
- Come il mirino, l'overlay non compare sopra lo schermo intero esclusivo: usa *Schermo intero in finestra*.
- La sessione ETW richiede i permessi di amministratore (FN Boost li chiede già all'avvio). L'uso GPU/VRAM dipende dai contatori del driver e può mancare su alcune schede.

---

## Rete, ping e report diagnostico

**Cosa misura e come.** Anche qui FN Boost osserva solo il sistema, senza toccare il gioco né l'anti-cheat:

- **Traffico del gioco**: dagli eventi UDP del provider ETW **Microsoft-Windows-Kernel-Network** (solo invio/ricezione UDP, IPv4 e IPv6). Da lì ricava l'indirizzo del server della partita, i pacchetti al secondo, la banda del gioco e le pause tra un pacchetto e l'altro. Serve l'amministratore, come per gli FPS.
- **Ping ICMP** (come il comando `ping`) inviati dal processo di FN Boost, uno al secondo per bersaglio: il **server di gioco**; l'**endpoint ufficiale Epic della regione** (`ping-eu`, `ping-nae`, `ping-nac`, `ping-naw`, `ping-br`, `ping-asia`, `ping-oce`, `ping-me` `.ds.on.epicgames.com`, gli stessi indicati dal supporto Epic), scelto in automatico come il più vicino oppure a mano in *Prestazioni › Misurazione*; il **router** di casa; **1.1.1.1** come riferimento della linea Internet.
- **Interfaccia di rete**: tipo di connessione (Ethernet/Wi-Fi), velocità del collegamento, segnale Wi-Fi e traffico totale del PC, per stimare quanto usano le **altre app**.
- **Processi in background**: i contatori PDH `\Process(*)` di Windows (CPU e RAM per nome di processo), letti senza aprire i processi. Il gioco e FN Boost sono esclusi.

Niente iniezioni, hook, handle verso il gioco, lettura della memoria o input simulato. La misura si spegne con *Misura ping e rete* (e il router con *Pinga anche il router*).

**Dove si vede.** La pagina **Prestazioni** ha la card *Rete e ping* (ping di gioco, jitter, perdita, router, Internet, regione Epic, pacchetti/s, banda, altre app, connessione, server, freeze degli ultimi 60 s) con il grafico del ping degli ultimi 2 minuti. Ogni sessione registrata salva un campione di rete al secondo: l'analisi mostra ping e perdita nel tempo, il riepilogo di rete, i processi in background più pesanti e i consigli automatici (ping alto, server lontano rispetto alla regione migliore, Wi-Fi debole, router instabile, banda occupata da altre app, freeze di rete). L'overlay in gioco può mostrare la riga `Ping 24 ms · jitter 2 · perdita 0%` (gialla da 80 ms / 10 ms di jitter / 1% di perdita, rossa da 120 ms / 25 ms / 3%); il pannello flottante mostra la stessa riga sotto gli FPS.

**Definizioni:**

| Valore | Significato |
|---|---|
| **Ping** | tempo di andata e ritorno di un pacchetto (ms), media dell'ultimo minuto dal vivo |
| **Jitter** | media della differenza tra ping consecutivi: quanto il ping "balla". Sotto 10 ms è buono |
| **Perdita** | percentuale di ping senza risposta. Già l'1-2% si nota in gioco |
| **Freeze di rete** | il server non manda pacchetti per oltre 250 ms: in gioco è lag (teletrasporti, colpi non registrati), anche con FPS perfetti |
| **Stutter** | un frame lento del PC (vedi sopra): è un problema di prestazioni, non di rete. L'analisi li tiene separati e segnala quando coincidono |

**Report diagnostico.** In *Prestazioni › Report diagnostico* (o *Panoramica › Crea report diagnostico*):

- **Esporta report completo (.zip)**: crea `Documenti\FN Boost\Report\FNBoost-Report-<data>.zip` con `report.html` (da aprire nel browser, con grafici), `report.json` (dati completi), `summary.txt`, `frametimes.csv`, gli eventi utili del log di Fortnite, il registro di FN Boost e un `README.txt`. Usa la sessione selezionata nello storico, altrimenti l'ultima salvata. A fine esportazione si apre Esplora file con lo ZIP già selezionato, pronto da allegare.
- **Copia riepilogo per la chat**: copia negli appunti un testo di massimo 4000 caratteri da incollare in una chat (anche con un assistente AI) o in un messaggio.
- **Apri cartella report**: apre la cartella dei report.

**Log di Fortnite nel report.** Il log può coprire molte ore e più avvii del gioco. Gli orari del log di Unreal sono in UTC: FN Boost converte l'inizio della sessione e conta a parte le righe del periodo della sessione (± 60 s). Solo quelle diventano consigli; i totali dell'intero log sono mostrati come contesto. Avvisi ed errori generici sono rumore normale (Fortnite ne scrive migliaia anche quando funziona tutto). Il report riporta anche la versione del gioco e l'API grafica davvero usata (es. `D3D11 · ES3_1` in modalità Performance) accanto ai valori `PreferredRHI`/`PreferredFeatureLevel` del file ini. Quando la sessione è buona (1% low ≥ 60% della media, al massimo 2 stutter al minuto, media ≥ 95% del limite FPS) il report lo dice chiaramente e i consigli diventano facoltativi.

**Privacy.** Il report è pensato per essere condiviso, quindi prima del salvataggio vengono rimossi: nome utente di Windows e percorsi del profilo, nome del PC, ID dell'account Epic, e-mail, token nelle URL, IP locali/privati e il tuo IP pubblico. Dal log di Fortnite si prendono solo gli eventi tecnici (connessione, server, hitch, shader, crash, memoria): chat, party e lista amici vengono saltati, quindi niente nomi di giocatori. Restano in chiaro solo gli IP dei server di gioco e gli endpoint Epic, che servono alla diagnosi. Controlla comunque il contenuto prima di inviarlo.

**Limiti, onestamente:**
- Molti server di gioco **non rispondono all'ICMP**: in quel caso il "ping di gioco" è quello dell'endpoint Epic della regione, che è vicino ma non è lo stesso server. La UI e il report dicono sempre verso cosa è stato misurato.
- Il ping ICMP può differire di **qualche ms** dal ping mostrato da Fortnite (che misura il proprio traffico UDP e il tempo di elaborazione del server).
- Senza permessi di amministratore il traffico del gioco (pacchetti, banda, freeze, indirizzo del server) non è misurabile: i ping funzionano comunque.
- Il report `frametimes.csv` di una sessione lunga può pesare decine di MB prima della compressione.

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
- `settings.json`: mirino, preset, scorciatoie, overlay FPS, posizione del pannello
- `sessions\`: sessioni di prestazioni registrate (`.json` con statistiche, campioni al secondo, rete e processi; `.ft` con tutti i frametime)
- `logs\fnboost.log`: registro di tutte le operazioni

I report diagnostici finiscono in `Documenti\FN Boost\Report\` (solo quando li crei tu).

Per annullare tutto: **Sicurezza e backup › Ripristina tutto**. In alternativa usa il punto di ripristino di Windows creato automaticamente prima del primo tweak.

---

## Fonti principali

- Epic: [requisiti anti-cheat PC (Secure Boot, TPM, IOMMU)](https://www.fortnite.com/news/new-fortnite-anti-cheat-pc-requirements-and-latest-legal-action), [perché servono TPM e Secure Boot](https://www.epicgames.com/help/en-US/c-Category_Fortnite/c-Fortnite_Competitive/why-do-i-need-to-enable-tpm-and-secure-boot-on-a-windows-pc-a000093838)
- Epic: [stutter in DirectX 12 e cache shader](https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/technical-support-c-202300000001719/fortnite-stutters-heavily-and-has-below-expected-performance-on-directx-12-a202300000018050), [modalità di rendering](https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/a202300000017914), [rimozione DirectX 11](https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/gameplay-c-202300000001721/has-the-directx-11-option-been-permanently-removed-from-rendering-mode-in-fortnite-a202300000083887), [limite FPS](https://www.epicgames.com/help/fortnite-battle-royale-c-202300000001636/technical-support-c-202300000001719/how-do-i-adjust-my-fps-in-fortnite-if-it-is-locked-or-capped-a202300000010554)
- Microsoft: [ottimizzazioni per i giochi in finestra](https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952), [MPO su 24H2](https://learn.microsoft.com/en-us/answers/questions/3881462/how-can-i-disable-windows-mpo-in-2025-(24h2-window)
- [PCWorld su HAGS](https://www.pcworld.com/article/2339130/should-you-enable-hardware-accelerated-gpu-scheduling-in-windows-11.html), [Sportskeeda sui mirini e i ban](https://www.sportskeeda.com/fortnite/can-get-banned-custom-crosshairs-fortnite), [Dexerto: impostazioni Fortnite](https://www.dexerto.com/fortnite/best-fortnite-pc-settings-for-fps-graphics-2977102)
- Chiavi reali di GameUserSettings.ini: [wispurn/Fortnite-Optimized-Settings](https://github.com/wispurn/Fortnite-Optimized-Settings)

FN Boost non è affiliato a Epic Games o Microsoft. Fortnite è un marchio di Epic Games, Inc.
