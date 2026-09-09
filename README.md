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
