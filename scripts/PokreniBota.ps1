# Pokrece RewardTracker i drzi ga u zivotu dok je korisnik prijavljen.
#
# Zasto bas ovako:
#  - Bot radi sa vidljivim browserom (Bot:Headless = false), jer ySense u headless
#    rezimu uopste ne iscrtava zaglavlje sa stanjem. Zbog toga aplikacija mora da radi
#    u prijavljenoj sesiji i ne moze biti Windows servis niti zadatak koji se izvrsava
#    "bez obzira na to da li je korisnik prijavljen".
#  - Baza je u Docker-u, a Docker Desktop se podize u isto vreme kad i ovaj skript,
#    pa se prvo ceka da Postgres pocne da prima veze.
#  - Izlaz se pise u fajl, jer se skript pokrece sakriven i konzola se ne vidi.

[CmdletBinding()]
param(
    # Da li pokrenuti i Blazor korisnicki interfejs. Botovi rade i bez njega.
    [bool] $PokreniInterfejs = $true,

    # Koliko najduze cekati da baza postane dostupna.
    [int] $CekanjeBazeSekundi = 300,

    # Koliko dana cuvati logove.
    [int] $CuvajLogoveDana = 14
)

$ErrorActionPreference = 'Stop'

$koren = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $koren 'logs'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

$glavniLog = Join-Path $logDir ('pokretac-{0}.log' -f (Get-Date -Format 'yyyy-MM-dd'))

function Zapisi([string] $poruka) {
    $red = '{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $poruka
    Add-Content -Path $glavniLog -Value $red -Encoding utf8
}

# Samo jedna instanca, ma koliko puta se korisnik odjavio i prijavio.
$mutex = New-Object System.Threading.Mutex($false, 'Global\RewardTrackerPokretac')
if (-not $mutex.WaitOne(0)) {
    Zapisi 'Pokretac vec radi - prekidam.'
    return
}

try {
    Zapisi '=== Pokretanje ==='

    Get-ChildItem -Path $logDir -Filter '*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$CuvajLogoveDana) } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    # --- cekanje na bazu -----------------------------------------------------
    $rok = (Get-Date).AddSeconds($CekanjeBazeSekundi)
    $bazaSpremna = $false

    while ((Get-Date) -lt $rok) {
        try {
            $klijent = [System.Net.Sockets.TcpClient]::new()
            $veza = $klijent.BeginConnect('localhost', 5432, $null, $null)

            if ($veza.AsyncWaitHandle.WaitOne(2000) -and $klijent.Connected) {
                $klijent.EndConnect($veza)
                $bazaSpremna = $true
            }

            $klijent.Close()
        }
        catch {
            # Baza jos nije podignuta - normalno odmah posle prijave.
        }

        if ($bazaSpremna) { break }
        Start-Sleep -Seconds 5
    }

    if (-not $bazaSpremna) {
        Zapisi "Baza nije postala dostupna u roku od $CekanjeBazeSekundi s - prekidam."
        return
    }

    Zapisi 'Baza je dostupna.'

    # --- pokretanje i odrzavanje procesa -------------------------------------
    function Pokreni([string] $ime, [string] $projekat, [string[]] $argumenti) {
        $izlaz = Join-Path $logDir ('{0}-{1}.log' -f $ime, (Get-Date -Format 'yyyy-MM-dd'))

        $p = Start-Process -FilePath 'dotnet' `
            -ArgumentList $argumenti `
            -WorkingDirectory (Join-Path $koren $projekat) `
            -WindowStyle Hidden `
            -RedirectStandardOutput $izlaz `
            -RedirectStandardError ($izlaz -replace '\.log$', '.err.log') `
            -PassThru

        Zapisi "$ime pokrenut (PID $($p.Id)), log: $izlaz"
        return $p
    }

    $definicije = @(
        @{ Ime = 'api'; Projekat = 'RewardTracker.Api'; Argumenti = @('run', '--launch-profile', 'https') }
    )

    if ($PokreniInterfejs) {
        $definicije += @{ Ime = 'client'; Projekat = 'RewardTracker.Client'; Argumenti = @('run') }
    }

    $procesi = @{}
    $uzastopniPadovi = @{}

    foreach ($d in $definicije) {
        $procesi[$d.Ime] = Pokreni $d.Ime $d.Projekat $d.Argumenti
        $uzastopniPadovi[$d.Ime] = 0
    }

    # Ako se nesto srusi nocu, podize se ponovo. Uzastopni brzi padovi se usporavaju
    # da se ne bi vrtelo u krug kada je greska trajna (npr. neispravan build).
    while ($true) {
        Start-Sleep -Seconds 30

        foreach ($d in $definicije) {
            $ime = $d.Ime
            $p = $procesi[$ime]

            if ($p -and -not $p.HasExited) {
                $uzastopniPadovi[$ime] = 0
                continue
            }

            $uzastopniPadovi[$ime]++
            $pauza = [Math]::Min(600, 30 * $uzastopniPadovi[$ime])
            Zapisi "$ime se ugasio (izlazni kod $($p.ExitCode)). Ponovno pokretanje za $pauza s (pokusaj $($uzastopniPadovi[$ime]))."

            Start-Sleep -Seconds $pauza
            $procesi[$ime] = Pokreni $ime $d.Projekat $d.Argumenti
        }
    }
}
catch {
    Zapisi "GRESKA: $($_.Exception.Message)"
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
