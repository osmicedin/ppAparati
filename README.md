# ppEvidencija

WPF aplikacija za prijavu korisnika, unos evidencije PP aparata i generisanje DOCX zapisnika po grupama aparata jednog konta.

## Priprema

1. Kopirati `appsettings.example.json` u `appsettings.json` i upisati kredencijale glavne SQL Server baze.
2. Ručno pokrenuti `Sql/Create_ppaparati.sql` nad tom bazom. Skript je idempotentan i ne kreira novu bazu; aplikacija ne pokreće migraciju.
3. Pokrenuti aplikaciju sa `dotnet run`.

`appsettings.json` je namjerno ignorisan i ne treba ga dodavati u Git.

## Baza

Aplikacija koristi glavnu bazu koja već sadrži `a_user` i `konta`. Svaki zapis u tabeli `ppaparati` pripada jednoj grupi u `ppaparati_grupe`. Grupa pripada jednom kontu, čuva status i verziju, a promjene statusa imaju vlastitu historiju. Baza dozvoljava najviše jednu otvorenu grupu po kontu. Postojeće tabele `ppaparati_status` i `ppaparati_status_audit` ostaju sačuvane zbog historije mjesečnih zaključivanja. Ne postoje funkcije za izmjenu ili brisanje korisnika i PP aparata.

### Nadogradnja sa mjesečnih perioda

1. Napraviti sigurnosnu kopiju baze i sačuvati lokalnu konfiguraciju aplikacije.
2. Zaustaviti sve instalacije aplikacije koje koriste istu bazu. Migracija i nova verzija moraju se uvesti zajedno; stara verzija ne smije nastaviti upisivati nakon migracije.
3. Ručno pokrenuti `Sql/Create_ppaparati.sql`. Svaki zaključeni mjesec s aparatima postaje zasebna zaključena grupa povezana s izvornim mjesečnim statusom. Nezaključeni mjeseci jednog konta, uključujući mjesece bez statusnog zapisa, spajaju se u jednu otvorenu grupu. Stari statusi i njihova historija ostaju sačuvani.
4. Provjeriti da je skript uspješno završen, pa instalirati novu verziju na sve klijente i pokrenuti ih. Provjeriti pregled otvorenih grupa i ranijih zaključivanja.

Migracija se izvršava transakcijski i provjerava pripadnost aparata odgovarajućem kontu. Ponovno pokretanje skripta ne pregrupiše već prenesene aparate. Kod neuspjeha migracije otkloniti prijavljeni uzrok i ponoviti skript prije pokretanja klijenata.

## Izvještaji

DOCX se generiše lokalno pomoću Open XML SDK-a. Microsoft Word je potreban samo za dugme **Otvori u Wordu**, odnosno pregled i štampu dokumenta.

Tabela **Kupci/konta** prikazuje jedan red po kontu koje ima evidentirane aparate, bez filtera mjeseca i godine. Filter **Nezaključeni** je početni izbor; dostupni su i **Zaključeni** i **Svi**. Filter prati trenutno stanje konta, pa konto s novim unosima nije među zaključenima iako ima ranija zaključivanja. Red prikazuje broj aparata trenutne grupe, status i posljednju promjenu.

Svaki novi unos ide u otvorenu grupu konta bez obzira na datum servisa. Jedna grupa može sadržavati aparate iz više mjeseci i godina. **Zaključi** završava tu grupu. Prvi naredni unos automatski otvara novu grupu i vraća konto među nezaključene, čak i kada je datum novog servisa stariji od datuma ranijeg zaključivanja. Nema ručnog ponovnog otvaranja; ranije zaključeni aparati ostaju u svojoj grupi.

Odabir konta otvara trenutnu otvorenu grupu ili posljednju zaključenu grupu ako nema otvorene. U detaljima je dostupna historija ranijih zaključivanja s datumom i brojem aparata, za pregled i ponovno generisanje DOCX-a. Prikazuje se raspon datuma servisa odabrane grupe. **Zaključi** je dostupno samo za otvorenu grupu. Nakon unosa i zaključivanja pregled zadržava postojeći statusni filter i odabir konta ako je još vidljivo.

**Sačuvaj DOCX** i **Zaključi** su odvojene radnje: izvoz dokumenta ne mijenja status. Dokument sadrži samo aparate odabranog kupca i grupe. Datum dokumenta bira se pri svakom izvozu i mora biti jednak ili kasniji od posljednjeg uključenog servisa. Period dokumenta počinje najranijim servisom, a završava izabranim datumom dokumenta; mjesec i godina u zaglavlju prate taj datum. Predloženi naziv fajla uključuje konto i identifikator grupe.

Generisanje prvo priprema privremeni dokument, a postojeći fajl zamjenjuje tek nakon uspjeha; neuspjela ili otkazana priprema čuva prethodni izvještaj i status konta. Ako drugi korisnik u međuvremenu doda aparat ili zaključi grupu, pokušaj zaključivanja zastarjelog pregleda se odbija, prikazuje se poruka i podaci se osvježavaju.

Datumi se unose u formatu `dd.MM.yyyy`. Datum servisa mora biti između `01.01.1900` i `30.06.9999`, kako bi se mogao izračunati sljedeći servis. Punjenje prihvata zarez ili tačku kao decimalni separator, do dvije decimale, bez separatora hiljada.

## Testovi

Regresioni testovi za unos, WPF izvještaje i DOCX:

```powershell
dotnet test Tests/ppEvidencija.Tests.csproj -c Release
```

SQL integracijski testovi su podrazumijevano preskočeni. Za njih odaberite lokalnu, izolovanu testnu SQL Server instancu na kojoj Windows korisnik može kreirati i ukloniti zasebnu privremenu bazu. Testovi koji prate SQL zaključavanja dodatno zahtijevaju `VIEW SERVER STATE` (odnosno `VIEW SERVER PERFORMANCE STATE` na novijim verzijama SQL Servera):

```powershell
$env:PPEVIDENCIJA_TEST_SQL_SERVER = '.\SQLEXPRESS'
dotnet test Tests/ppEvidencija.Tests.csproj -c Release
Remove-Item Env:PPEVIDENCIJA_TEST_SQL_SERVER
```

Testovi sami kreiraju bazu jedinstvenog naziva, koriste sintetičke podatke i uklanjaju je nakon izvršavanja. Ne čitaju `appsettings.json` niti koriste poslovnu bazu. Provjeravaju spajanje otvorenih mjeseci, očuvanje zatvorenih grupa i stare historije, ponovljivu migraciju, automatsko otvaranje nove grupe, istovremene unose i zaključivanje te zajednički rollback aparata, grupe i historije promjena.
