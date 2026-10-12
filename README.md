# ppEvidencija

WPF aplikacija za prijavu korisnika, unos evidencije PP aparata i generisanje mjesečnih DOCX zapisnika.

## Priprema

1. Kopirati `appsettings.example.json` u `appsettings.json` i upisati kredencijale glavne SQL Server baze.
2. Pokrenuti `Sql/Create_ppaparati.sql` nad tom bazom. Skript je idempotentan i ne kreira novu bazu.
3. Pokrenuti aplikaciju sa `dotnet run`.

`appsettings.json` je namjerno ignorisan i ne treba ga dodavati u Git.

## Baza

Aplikacija koristi glavnu bazu koja već sadrži `a_user` i `konta`. Projektne tabele su `ppaparati`, `ppaparati_status` i `ppaparati_status_audit`. Skript automatski preimenuje stare statusne tabele bez gubitka podataka. Ne postoje funkcije za izmjenu ili brisanje korisnika i PP aparata.

## Izvještaji

DOCX se generiše lokalno pomoću Open XML SDK-a. Microsoft Word je potreban samo za dugme **Otvori u Wordu**, odnosno pregled i štampu dokumenta.

Kartica **Izvještaji** prikazuje kupce koji imaju evidentirane aparate u odabranom mjesecu. Zaključeni period blokira nove unose za isti konto, mjesec i godinu sve dok korisnik period ponovo ne otvori.

Filter **Nezaključeni** prikazuje otvorene periode iz svih mjeseci i godina. Zaključivanje i ponovno otvaranje uvijek koriste konto, mjesec i godinu iz potvrđenog reda. Tokom promjene statusa ili generisanja dokumenta povezane radnje su privremeno onemogućene.

Datum zaključivanja zapisnika mora biti jednak ili kasniji od posljednjeg uključenog servisa. Dokument sadrži samo aparate odabranog kupca i mjeseca. Generisanje prvo priprema privremeni dokument, a postojeći fajl zamjenjuje tek nakon uspjeha; neuspjela ili otkazana priprema čuva prethodni izvještaj.

Datumi se unose u formatu `dd.MM.yyyy`. Datum servisa mora biti između `01.01.1900` i `30.06.9999`, kako bi se mogao izračunati sljedeći servis. Punjenje prihvata zarez ili tačku kao decimalni separator, do dvije decimale, bez separatora hiljada.

## Testovi

Regresioni testovi za unos, WPF izvještaje i DOCX:

```powershell
dotnet test Tests/ppEvidencija.Tests.csproj -c Release
```

SQL integracijski testovi su podrazumijevano preskočeni. Za njih odaberite lokalnu testnu SQL Server instancu na kojoj Windows korisnik može kreirati i ukloniti zasebnu privremenu bazu. Test unosa koji je već u toku prati SQL zaključavanja i dodatno zahtijeva `VIEW SERVER STATE` (odnosno `VIEW SERVER PERFORMANCE STATE` na novijim verzijama SQL Servera):

```powershell
$env:PPEVIDENCIJA_TEST_SQL_SERVER = '.\SQLEXPRESS'
dotnet test Tests/ppEvidencija.Tests.csproj -c Release
Remove-Item Env:PPEVIDENCIJA_TEST_SQL_SERVER
```

Testovi sami kreiraju bazu jedinstvenog naziva, koriste sintetičke podatke i uklanjaju je nakon izvršavanja. Ne čitaju `appsettings.json` niti koriste poslovnu bazu. Provjeravaju blokadu unosa u zaključani period, ponovno otvaranje, istovremene operacije i zajednički rollback statusa i historije promjena.
