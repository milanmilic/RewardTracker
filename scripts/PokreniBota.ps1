# Pokrece RewardTracker i drzi ga u zivotu dok je korisnik prijavljen.
#
# Zasto bas ovako:
#  - Bot radi sa vidljivim browserom (Bot:Headless = false), jer ySense u headless
#    rezimu uopste ne iscrtava zaglavlje sa stanjem. Zbog toga aplikacija mora da radi
#    u prijavljenoj sesiji i ne moze biti Windows servis niti zadatak koji se izvrsava
#    "bez obzira na to da li je korisnik prijavljen".
#  - Baza je u Docker-u, a Docker Desktop se podize u isto vreme kad i ovaj skript,
#    pa se prvo ceka da Docker engine odgovori, zatim se baza podize i ceka se da
#    pocne da prima veze. Skript sam podize kontejner jer posle hibernacije laptopa
#    Docker zatekne kontejnere u stanju Exited i ne mora ih uvek sam vratiti.
#  - Izlaz se pise u fajl, jer se skript pokrece sakriven i konzola se ne vidi.

[CmdletBinding()]
param(
    # Da li pokrenuti i Blazor korisnicki interfejs. Botovi rade i bez njega.
    [bool] $PokreniInterfejs = $true,

    # Koliko najduze cekati da baza postane dostupna.
    [int] $CekanjeBazeSekundi = 300,

    # Koliko najduze cekati da Docker engine pocne da odgovara.
    [int] $CekanjeDockeraSekundi = 300,

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

    # --- Docker engine -------------------------------------------------------
    # Docker Desktop se podize paralelno sa ovim skriptom i zna da mu treba minut-dva.
    function DockerRadi {
        try {
            $null = & docker info --format '{{.ServerVersion}}' 2>&1
            return ($LASTEXITCODE -eq 0)
        }
        catch {
            return $false
        }
    }

    $rok = (Get-Date).AddSeconds($CekanjeDockeraSekundi)
    $dockerSpreman = $false

    while ((Get-Date) -lt $rok) {
        if (DockerRadi) { $dockerSpreman = $true; break }
        Start-Sleep -Seconds 5
    }

    if (-not $dockerSpreman) {
        Zapisi "Docker engine nije odgovorio u roku od $CekanjeDockeraSekundi s - prekidam."
        return
    }

    Zapisi 'Docker engine odgovara.'

    # --- podizanje baze ------------------------------------------------------
    # Kljucni korak: posle hibernacije kontejneri ostaju u stanju Exited (255).
    # Ovo ih vraca u zivot i ujedno ih pravi ako jos ne postoje.
    try {
        # Docker pise upozorenja na stderr, sto bi uz ErrorActionPreference = Stop
        # bilo protumaceno kao greska, pa se ono privremeno spusta.
        $staro = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $ishod = & docker compose --project-directory $koren up -d 2>&1 | Out-String
        $kod = $LASTEXITCODE
        $ErrorActionPreference = $staro

        if ($kod -eq 0) {
            Zapisi 'Kontejneri su podignuti.'
        }
        else {
            Zapisi "docker compose up nije uspeo (kod $kod): $($ishod.Trim())"
        }
    }
    catch {
        Zapisi "Greska pri podizanju kontejnera: $($_.Exception.Message)"
    }

    # --- cekanje na bazu -----------------------------------------------------
    # Nije dovoljno da port prima veze - Postgres ga otvori pre nego sto je spreman
    # za upite, pa se pita pg_isready.
    $rok = (Get-Date).AddSeconds($CekanjeBazeSekundi)
    $bazaSpremna = $false

    while ((Get-Date) -lt $rok) {
        try {
            $null = & docker exec reward_tracker_db pg_isready -U reward_user -d reward_tracker 2>&1
            if ($LASTEXITCODE -eq 0) { $bazaSpremna = $true; break }
        }
        catch {
            # Kontejner se jos podize - normalno odmah posle prijave.
        }

        Start-Sleep -Seconds 5
    }

    if (-not $bazaSpremna) {
        Zapisi "Baza nije postala dostupna u roku od $CekanjeBazeSekundi s - prekidam."
        return
    }

    Zapisi 'Baza je dostupna.'

    # --- gradnja -------------------------------------------------------------
    # Mora se izgraditi jednom, unapred. Ako se "dotnet run" pusti nad oba projekta
    # istovremeno, oba krenu da grade zajednicki RewardTracker.Core i sudare se na
    # istom izlaznom fajlu (CS2012: file is being used by another process).
    $gradnjaLog = Join-Path $logDir ('build-{0}.log' -f (Get-Date -Format 'yyyy-MM-dd'))

    $staro = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet build (Join-Path $koren 'RewardTracker.slnx') -c Debug --nologo 2>&1 |
        Out-File -FilePath $gradnjaLog -Encoding utf8
    $kodGradnje = $LASTEXITCODE
    $ErrorActionPreference = $staro

    if ($kodGradnje -ne 0) {
        Zapisi "Gradnja nije uspela (kod $kodGradnje). Detalji: $gradnjaLog"
        return
    }

    Zapisi 'Gradnja uspesna.'

    # --- ciscenje zaostalih procesa ------------------------------------------
    # Ako je pokretac ranije nasilno ugasen, njegova deca prezive i drze portove
    # 7214/5173, pa novi API ne moze da se veze i vrti se u krug restartovanja.
    Get-Process -Name 'RewardTracker.Api', 'RewardTracker.Client' -ErrorAction SilentlyContinue |
        ForEach-Object {
            Zapisi "Gasim zaostali proces $($_.ProcessName) (PID $($_.Id))."
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }

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

        # Bez citanja Handle-a .NET ne zadrzi handle procesa, pa ExitCode kasnije
        # bude prazan i u logu se ne vidi zasto se proces ugasio.
        $null = $p.Handle

        Zapisi "$ime pokrenut (PID $($p.Id)), log: $izlaz"
        return $p
    }

    $definicije = @(
        @{ Ime = 'api'; Projekat = 'RewardTracker.Api'; Argumenti = @('run', '--no-build', '--launch-profile', 'https') }
    )

    if ($PokreniInterfejs) {
        $definicije += @{ Ime = 'client'; Projekat = 'RewardTracker.Client'; Argumenti = @('run', '--no-build') }
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
