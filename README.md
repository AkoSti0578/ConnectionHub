# Connection Hub

Een moderne Windows-applicatie om **RDP**- en **SSH**-sessies te beheren en te openen vanuit één venster, met groepen, tabbladen en een MSI-installer.

## Functies

### RDP
- Ingebouwde Remote Desktop-sessies in tabbladen (Microsoft RDP ActiveX-control, `MsRdpClient9NotSafeForScripting`).
- **Schaalbaar**:
  - *Dynamische resolutie*: de resolutie van het externe bureaublad volgt live de grootte van het tabblad/venster, inclusief DPI-schaal (Windows 8.1 / Server 2012 R2 of nieuwer aan de serverkant).
  - *Smart sizing*: vaste resolutie die wordt geschaald naar het venster.
  - *Vaste resolutie* met schuifbalken.
  - Instelbare DPI-schaal op afstand (automatisch of 100–300%).
- Volledig scherm, automatisch opnieuw verbinden, NLA/CredSSP, admin-/consolesessie, RD Gateway.
- Doorsturen van klembord, geluid, stations, printers en smartcards.
- Openen in de standaard Windows-client (mstsc), ook over alle beeldschermen.

### SSH
- Volwaardige terminal (xterm.js in WebView2, 256 kleuren, klikbare links, 10.000 regels scrollback).
- Authenticatie: wachtwoord, public key (OpenSSH/PuTTY, met passphrase) en **keyboard-interactive** (standaard voor nieuwe SSH-verbindingen).
- **2FA / MFA**: OTP-codes (Google Authenticator, Microsoft Authenticator), Duo, RADIUS-tokens enz. worden via keyboard-interactive gevraagd in een eigen dialoog. Een opgeslagen wachtwoord wordt automatisch ingevuld, zodat je alleen de verificatiecode hoeft in te voeren. Ook combinaties als *public key + OTP* (partial success) werken.
- Controle van hostsleutels (SHA256-vingerafdruk) met waarschuwing bij een gewijzigde sleutel.
- Kopiëren bij selecteren, plakken met rechtermuisknop, `Ctrl+Shift+C` / `Ctrl+Shift+V`.

### SFTP
- Bestandsbrowser per SSH-server in een eigen tabblad: via het rechtermuismenu (**Bestanden (SFTP)**) of de mapknop bij een open SSH-sessie.
- Aanmelden zoals bij de terminal, inclusief keyboard-interactive / 2FA en controle van de hostsleutel.
- Bladeren (pad typen, map omhoog, thuismap), verborgen bestanden tonen, rechten en wijzigingsdatum.
- Uploaden (knop of bestanden/mappen vanuit Verkenner slepen), downloaden (ook hele mappen), nieuwe map, hernoemen, verwijderen (mappen met inhoud), pad kopiëren.
- Dubbelklikken op een bestand opent een kopie met het standaardprogramma van Windows.
- Voortgang met snelheid en een knop om een overdracht te annuleren; half overgezette bestanden worden opgeruimd.
- Symbolische links: hernoemen en verwijderen raken alleen de link, nooit het doel.

### Beheer
- **Groepen en subgroepen** met eigen kleur; verplaatsen met slepen en neerzetten.
- Een hele groep in één keer openen.
- Zoeken op naam, host, gebruiker of protocol.
- Snel verbinden (`rdp server01`, `ssh beheer@10.0.0.5:2222`, `gebruiker@host`).
- Recente verbindingen op het startscherm.
- Import/export (JSON).
- Wachtwoorden en passphrases worden versleuteld met Windows DPAPI (gekoppeld aan je Windows-account).

### Sneltoetsen
| Toets | Actie |
|---|---|
| `Ctrl+N` | Nieuwe verbinding |
| `Ctrl+Shift+N` | Nieuwe groep |
| `Ctrl+F` | Zoeken |
| `Ctrl+W` | Tabblad sluiten |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Volgend / vorig tabblad |
| `Ctrl+Home` | Startscherm |
| `Enter` / `F2` / `Del` | Verbinden / bewerken / verwijderen (in de lijst) |
| `Ctrl+Alt+Break` | Volledig scherm van RDP verlaten |

## Installeren

1. Download `ConnectionHub-<versie>-x64.msi` uit de GitHub Actions-artefacten (workflow **Build MSI**) of uit een release.
2. Dubbelklik op het MSI-bestand en volg de wizard (installatiemap kiezen, snelkoppeling in het startmenu en op het bureaublad).

Vereisten: Windows 10/11 of Windows Server 2016+ (x64) en de Microsoft Edge WebView2 Runtime (standaard aanwezig op Windows 10/11). De .NET-runtime zit in de installer; die hoeft niet apart geïnstalleerd te worden.

Een nieuwere versie installeren werkt de bestaande installatie bij. Je verbindingen blijven bewaard in `%APPDATA%\ConnectionHub\connections.json`.

## Zelf bouwen

Vereisten: Windows met de [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). WiX Toolset 5 wordt automatisch via NuGet opgehaald.

```powershell
.\build.ps1 -Version 1.0.0
# Resultaat: artifacts\msi\ConnectionHub-1.0.0-x64.msi
```

Alleen de applicatie starten tijdens ontwikkeling:

```powershell
dotnet run --project src\ConnectionHub
```

Bij elke push bouwt GitHub Actions automatisch het MSI-bestand. Een tag `v1.2.3` maakt een release met het MSI-bestand als bijlage.

## Projectstructuur

```
src/ConnectionHub/
  Models/        Verbindingen, groepen, instellingen
  Services/      Opslag (JSON + DPAPI), externe mstsc-start, thema van het venster
  Rdp/           Interop met de RDP ActiveX-control (events, extended settings)
  Ssh/           SSH-sessie met keyboard-interactive / 2FA (SSH.NET)
  Views/         RDP-weergave, terminal (WebView2 + xterm.js), dialogen
  ViewModels/    MVVM: boomstructuur, sessies, commando's
  Themes/        Donker, modern thema
  Assets/        Icoon en terminalbestanden (xterm.js)
installer/       WiX 5-project voor het MSI-bestand
```
