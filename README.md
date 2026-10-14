# ppEvidencija

WPF aplikacija za prijavu korisnika, unos evidencije PP aparata i generisanje DOCX zapisnika po grupama aparata jednog konta.

## Priprema

1. Na instanci `s-office1\sqlexpress` pokrenuti `Sql/Create_ppaparati.sql`. Skript kreira bazu `evidencija` ako ne postoji, bira je i kreira pet PP tabela. Može se ponavljati bez brisanja podataka. Aplikacija ne pokreće migraciju.
2. Ako PP podaci još nisu preneseni, jednom pokrenuti `Sql/Import_zemi2026.sql`. Prenosi samo aparate, grupe i historiju, čuva identifikatore i provjerava sve vrijednosti prije potvrde transakcije. Sve ciljne PP tabele moraju biti prazne. Postojeći podaci se ne prepisuju, a ERP tabele se ne kopiraju.
3. Pokrenuti `Sql/Configure_erp.sql`. Skript postavlja zajedničku konfiguraciju i automatski priprema ključeve za šifriranje. Postojeće ključeve i podatke čuva. Spremanje ERP konekcije spremno je odmah, bez dodatnih lozinki ili interaktivnog postavljanja ključeva. ERP baza se ne bira automatski.
4. Postaviti početnu konekciju prema `evidencija` u lokalnom `appsettings.json`, pa izgraditi ili objaviti aplikaciju. Za ograničeni SQL nalog `ppEvidencija_app` i zasebne sigurnosne kopije ključeva SQL administrator može dodatno pokrenuti `tools/Initialize-ErpConfiguration.ps1` u PowerShellu 7.2 ili novijem. Instalater traži lozinku za zaštitu izvezenih kopija i postojeći direktorij na SQL serveru dostupan njegovom servisnom nalogu; provjerava novi SQL nalog i tek potom upisuje lokalni `appsettings.json`. Istu ograničenu početnu konekciju koristiti za ostale instalacije.
5. Pokrenuti aplikaciju i izabrati **Konfiguracija ERP baze**. Forma se otvara direktno, bez posebne lozinke za pristup konfiguraciji. Unijeti ERP instancu, bazu, SQL korisnika i SQL lozinku. Lozinka je obavezna pri svakom testiranju i spremanju, uključujući izmjenu postojeće konfiguracije. **Testiraj konekciju** provjerava potrebne tabele i kolone; **Sačuvaj** ponavlja provjeru. Potom se prijaviti postojećim ERP korisnikom.

Pokretanje SQL skripti iz direktorija projekta, uz Windows korisnika s potrebnim SQL dozvolama:

```powershell
sqlcmd -S 's-office1\sqlexpress' -E -b -i Sql/Create_ppaparati.sql
# Samo ako PP podaci još nisu preneseni:
sqlcmd -S 's-office1\sqlexpress' -E -b -i Sql/Import_zemi2026.sql
sqlcmd -S 's-office1\sqlexpress' -E -b -i Sql/Configure_erp.sql
# Dodatno: izvoz ključeva za oporavak i ograničeni SQL nalog (Windows SQL administrator):
pwsh -File tools/Initialize-ErpConfiguration.ps1 -ServerInstance 's-office1\sqlexpress' -TrustServerCertificate
```

SQL fajlovi se mogu pokrenuti i u SQL Server Management Studio. `Configure_erp.sql` postavlja konfiguracijsku tabelu, procedure, ulogu i potrebne ključeve; pri nadogradnji uklanja ranije procedure za posebnu konfiguracijsku lozinku. Postojeću tabelu `pp_config_admin` i njene podatke ostavlja sačuvane, ali ih aplikacija više ne koristi. Dodatni instalater nije uslov za spremanje ERP konfiguracije. Za SQL instancu s pouzdanim certifikatom izostaviti `-TrustServerCertificate`. ERP konekcija koristi šifrirani prijenos uz povjerenje serverskom certifikatu, kao postojeća lokalna SQL Express instalacija.

`appsettings.json` sadrži samo početnu konekciju prema `evidencija`, namjerno je ignorisan i ne treba ga dodavati u Git. `appsettings.example.json` prikazuje format, a stvarnu lozinku namjenskog SQL naloga upisuje instalater. Varijabla `PPEVIDENCIJA_ConnectionStrings__DefaultConnection` može zamijeniti ovu početnu konekciju; ERP izvor se bira isključivo kroz zajedničke postavke u bazi.

## Baza

Aplikacija koristi dvije nezavisne konekcije. `evidencija` sadrži PP podatke i centralne postavke. ERP baza sadrži `dbo.a_user` i `dbo.konta` i može biti na istoj ili drugoj SQL instanci. ERP SQL nalogu dovoljan je `SELECT` nad ovim tabelama. Za prijavu su potrebne kolone `korime`, `ime`, `prezime`, `lozinka`, `kriploz`, `aktivan`; za konta `konto` i `naziv`. Postojeća ERP provjera lozinke i aktivnog korisnika ostaje ista.

Svi upiti konta imaju uslov `konto LIKE '211%'`, uključujući provjeru prije snimanja aparata i dohvat naziva za izvještaje. Lokalna transakcija počinje tek poslije ERP provjere; izvještaji prvo učitaju PP podatke i završe lokalnu transakciju, pa grupno dohvate aktuelne nazive iz ERP-a. Nije potreban linked server niti distribuirana transakcija. Ako konto nestane iz ERP-a, historija ostaje dostupna sa šifrom i oznakom **Naziv konta nije pronađen**. Nedostupnost ERP-a prikazuje grešku; nema prelaska na stare lokalne kopije.

Svaki zapis u `ppaparati` pripada jednoj grupi u `ppaparati_grupe`. Grupa pripada jednom kontu, čuva status i verziju, a promjene statusa imaju vlastitu historiju. Baza dozvoljava najviše jednu otvorenu grupu po kontu. Tabele `ppaparati_status` i `ppaparati_status_audit` ostaju sačuvane zbog historije mjesečnih zaključivanja. Ranije kopirane `a_user` i `konta`, kao i nepovezane tabele u `evidencija`, ostaju sačuvane, ali ih aplikacija ne koristi.

Polje **Ispitivanje izvršio** preuzima posljednji sačuvani potpis iz `ppaparati`, po najvećem identifikatoru unosa, nezavisno od prijavljenog korisnika i kupca. Potpis se pamti nakon uspješnog snimanja aparata i koristi pri sljedećim unosima i pokretanjima aplikacije. U praznoj bazi polje ostaje prazno dok se ne unese prvi potpis. Evidencija korisnika koji je izvršio upis ili zaključivanje i dalje prati prijavljeni nalog.

### Zajednička konfiguracija i zaštita

`pp_erp_config` čuva jedan ERP izvor, šifriranu SQL lozinku, vrijeme izmjene i `rowversion`. Svaka prijava učitava nepromjenjivu kopiju konekcije; otvorene sesije nastavljaju koristiti prethodni izvor do zatvaranja aplikacije. SQL lozinka mora biti unesena pri svakom testiranju i spremanju. Prazna lozinka ili lozinka sastavljena samo od razmaka i drugih praznih znakova odbija se; prethodna lozinka se ne preuzima iz spremljene konfiguracije. Istovremena izmjena druge instalacije odbija zastarjelo spremanje.

SQL lozinka zaštićena je AES-256 ključem `pp_ErpConfigurationKey`, certifikatom `pp_ErpConfigurationCertificate` i glavnim ključem baze. Konfiguraciju može otvoriti i sačuvati svaki korisnik aplikacije bez dodatne lozinke. Aplikacijska uloga koristi procedure za čitanje i spremanje, bez direktnog pristupa konfiguracijskoj tabeli. Provjera ERP konekcije, šifriranje SQL lozinke i zaštita od istovremenog prepisivanja postavki ostaju aktivni.

SQL skript automatski kreira nedostajuće ključeve za novu konfiguraciju. Ista provjera postoji pri spremanju, pod ovlaštenjima procedure, pa aplikacijskom nalogu nisu potrebna prava za kreiranje ključeva. Istovremena početna spremanja serijalizovana su zajedno s provjerom verzije konfiguracije. Ako je dio ključeva izgubljen uz postojeću šifriranu lozinku, postupak odbija spremanje i čuva podatke umjesto kreiranja zamjenskog ključa.

### Ponovna instalacija i oporavak

Novi glavni ključ baze dobiva kriptografski nasumičnu lozinku koja se ne prikazuje niti upisuje u aplikacijske postavke; SQL Server ga automatski štiti svojim servisnim glavnim ključem (service master key). Ta serverska zaštita omogućava normalan rad na istoj instanci bez unosa lozinke. SQL administrator treba čuvati sigurnosne kopije baze i servisnog glavnog ključa, ili izvesti prenosivu kopiju glavnog ključa pomoću dodatnog instalatera prije premještanja na drugu instancu. Sama kopija baze nije dovoljan plan oporavka nakon gubitka servisnog ključa.

Instalater ne mijenja lozinku postojećeg SQL naloga. Pri ponavljanju dostaviti njegovu trenutnu lozinku kroz `-RuntimePassword (Read-Host -AsSecureString)`. Postojeća ERP konekcija i ključevi ostaju sačuvani. Ako ranije ručno postavljeni glavni ključ nije automatski dostupan SQL serveru, dostaviti njegovu poznatu lozinku kroz `-ExistingMasterKeyPassword (Read-Host -AsSecureString)`; za automatski generisani ključ koristiti sigurnosnu kopiju. Za automatizovanu instalaciju lozinka za izvoz kopija može se dostaviti kao `SecureString` kroz `-RecoveryPassword`, uz putanju `-RecoveryDirectory`. Ova lozinka štiti izvezene datoteke i ne mijenja lozinku postojećeg glavnog ključa.

Instalater pravi zasebne šifrirane kopije glavnog ključa i privatnog ključa certifikata (`.masterkey`, `.cer`, `.pvk`), uključujući ključeve koje je SQL skript automatski pripremio. Sačuvati ih zajedno s podacima o izabranoj lozinci za oporavak, odvojeno od baze i izvornog koda. Pri premještanju baze na drugu SQL instancu SQL administrator obnavlja glavni ključ iz šifrirane kopije i dodaje zaštitu novim service master key-om (`ALTER MASTER KEY ADD ENCRYPTION BY SERVICE MASTER KEY`). Ranije ručno postavljeni ključ može prvo otvoriti njegovom poznatom lozinkom (`OPEN MASTER KEY ...`). Postojeći ključevi se ne brišu i ne kreiraju ponovo preko starih. Bez ispravnog ključa aplikacija odbija učitavanje spremljene ERP lozinke.

### Nadogradnja sa mjesečnih perioda

1. Napraviti sigurnosnu kopiju baze i sačuvati lokalnu konfiguraciju aplikacije.
2. Zaustaviti sve instalacije aplikacije koje koriste istu bazu. Migracija i nova verzija moraju se uvesti zajedno; stara verzija ne smije nastaviti upisivati nakon migracije.
3. Ručno pokrenuti `Sql/Create_ppaparati.sql` (nadograđuje bazu `evidencija`). Svaki zaključeni mjesec s aparatima postaje zasebna zaključena grupa povezana s izvornim mjesečnim statusom. Nezaključeni mjeseci jednog konta, uključujući mjesece bez statusnog zapisa, spajaju se u jednu otvorenu grupu. Stari statusi i njihova historija ostaju sačuvani.
4. Provjeriti da je skript uspješno završen, pa instalirati novu verziju na sve klijente i pokrenuti ih. Provjeriti pregled otvorenih grupa i ranijih zaključivanja.

Migracija se izvršava transakcijski i provjerava pripadnost aparata odgovarajućem kontu. Ponovno pokretanje skripta ne pregrupiše već prenesene aparate. Kod neuspjeha migracije otkloniti prijavljeni uzrok i ponoviti skript prije pokretanja klijenata.

## Izvještaji

DOCX se generiše lokalno pomoću Open XML SDK-a. Microsoft Word je potreban samo za dugme **Otvori u Wordu**, odnosno pregled i štampu dokumenta.

Tabela **Kupci/konta** prikazuje jedan red po kontu koje ima evidentirane aparate, bez filtera mjeseca i godine. Filter **Nezaključeni** je početni izbor; dostupni su i **Zaključeni** i **Svi**. Filter prati trenutno stanje konta, pa konto s novim unosima nije među zaključenima iako ima ranija zaključivanja. Red prikazuje broj aparata trenutne grupe, status i posljednju promjenu.

Svaki novi unos ide u otvorenu grupu konta bez obzira na datum servisa. Jedna grupa može sadržavati aparate iz više mjeseci i godina. **Zaključi** završava tu grupu. Prvi naredni unos automatski otvara novu grupu i vraća konto među nezaključene, čak i kada je datum novog servisa stariji od datuma ranijeg zaključivanja. Nema ručnog ponovnog otvaranja; ranije zaključeni aparati ostaju u svojoj grupi.

Odabir konta otvara trenutnu otvorenu grupu ili posljednju zaključenu grupu ako nema otvorene. U detaljima je dostupna historija ranijih zaključivanja s datumom i brojem aparata, za pregled i ponovno generisanje DOCX-a. Prikazuje se raspon datuma servisa odabrane grupe. **Zaključi** je dostupno samo za otvorenu grupu. Nakon unosa i zaključivanja pregled zadržava postojeći statusni filter i odabir konta ako je još vidljivo.

**Sačuvaj DOCX** i **Zaključi** su odvojene radnje: izvoz dokumenta ne mijenja status. Dokument sadrži samo aparate odabranog kupca i grupe. Datum dokumenta automatski se postavlja na najnoviji datum servisa u odabranoj grupi, nezavisno od redoslijeda unosa aparata. Može se ručno promijeniti prije izvoza, ali ne može biti raniji od najnovijeg uključenog servisa. Datum zaključka te mjesec i godina u zaglavlju prate izabrani datum. Period dokumenta počinje najranijim servisom i završava datumom dokumenta. Predloženi naziv fajla uključuje konto i identifikator grupe.

Generisanje prvo priprema privremeni dokument, a postojeći fajl zamjenjuje tek nakon uspjeha; neuspjela ili otkazana priprema čuva prethodni izvještaj i status konta. Ako drugi korisnik u međuvremenu doda aparat ili zaključi grupu, pokušaj zaključivanja zastarjelog pregleda se odbija, prikazuje se poruka i podaci se osvježavaju.

Datumi se unose u formatu `dd.MM.yyyy`. Datum servisa mora biti između `01.01.1900` i `30.06.9999`, kako bi se mogao izračunati sljedeći servis. Punjenje prihvata zarez ili tačku kao decimalni separator, do dvije decimale, bez separatora hiljada.

## Testovi

Regresioni testovi za unos, WPF izvještaje i DOCX:

```powershell
dotnet test Tests/ppEvidencija.Tests.csproj -c Release
```

SQL integracijski testovi su podrazumijevano preskočeni. Za njih odaberite lokalnu testnu SQL Server instancu s miješanom autentifikacijom, na kojoj Windows korisnik može kreirati i ukloniti privremene baze i SQL naloge. Testovi koji prate SQL zaključavanja dodatno zahtijevaju `VIEW SERVER STATE` (odnosno `VIEW SERVER PERFORMANCE STATE` na novijim verzijama SQL Servera):

```powershell
$env:PPEVIDENCIJA_TEST_SQL_SERVER = '.\SQLEXPRESS'
dotnet test Tests/ppEvidencija.Tests.csproj -c Release
Remove-Item Env:PPEVIDENCIJA_TEST_SQL_SERVER
```

Testovi sami kreiraju odvojene ERP i PP baze jedinstvenih naziva, koriste sintetičke podatke i uklanjaju privremene baze i naloge nakon izvršavanja. PP testna baza nema `a_user` ili `konta`. Iz instalacijskih skripti koriste samo sheme iza oznaka `SCHEMA_BEGIN` i `ERP_SCHEMA_BEGIN`, bez odabira stvarne baze `evidencija`. Ne čitaju lokalni `appsettings.json`. Provjeravaju ERP prijavu, prefiks 211, izvještaje, očuvanje historije, nedostupnost baza, šifriranje, SQL dozvole, sukobe konfiguracija, WPF forme, migraciju i istovremene transakcije.
