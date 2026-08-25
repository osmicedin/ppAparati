# ppEvidencija

WPF aplikacija za prijavu korisnika, unos evidencije PP aparata i generisanje mjesečnih DOCX zapisnika.

## Priprema

1. Kopirati `appsettings.example.json` u `appsettings.json` i upisati lokalne SQL Server kredencijale.
2. Pokrenuti `Sql/Create_ppaparati.sql` nad bazom koja već sadrži tabele `a_user` i `konta`.
   Skript je idempotentan i treba ga ponovo pokrenuti i pri nadogradnji postojeće instalacije.
3. Pokrenuti aplikaciju sa `dotnet run`.

`appsettings.json` je namjerno ignorisan i ne treba ga dodavati u Git.

## Baza

Aplikacija radi samo `SELECT` nad `a_user` i `konta`, te `INSERT` i `SELECT` nad `ppaparati`. Status mjesečnog izvještaja čuva se u `ppizvjestaji_status`, a svako zaključivanje i ponovno otvaranje u `ppizvjestaji_status_audit`. Ne postoje funkcije za izmjenu ili brisanje korisnika i PP aparata.

## Izvještaji

DOCX se generiše lokalno pomoću Open XML SDK-a. Microsoft Word je potreban samo za dugme **Otvori u Wordu**, odnosno pregled i štampu dokumenta.

Kartica **Izvještaji** prikazuje kupce koji imaju evidentirane aparate u odabranom mjesecu. Zaključeni period blokira nove unose za isti konto, mjesec i godinu sve dok korisnik period ponovo ne otvori.
