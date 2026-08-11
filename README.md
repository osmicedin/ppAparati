# ppEvidencija

WPF aplikacija za prijavu korisnika, unos evidencije PP aparata i generisanje mjesečnih DOCX zapisnika.

## Priprema

1. Kopirati `appsettings.example.json` u `appsettings.json` i upisati lokalne SQL Server kredencijale.
2. Pokrenuti `Sql/Create_ppaparati.sql` nad bazom koja već sadrži tabele `a_user` i `konta`.
3. Pokrenuti aplikaciju sa `dotnet run`.

`appsettings.json` je namjerno ignorisan i ne treba ga dodavati u Git.

## Baza

Aplikacija radi samo `SELECT` nad `a_user` i `konta`, te `INSERT` i `SELECT` nad `ppaparati`. Ne postoje funkcije za izmjenu ili brisanje korisnika i PP aparata.

## Izvještaji

DOCX se generiše lokalno pomoću Open XML SDK-a. Microsoft Word je potreban samo za dugme **Otvori u Wordu**, odnosno pregled i štampu dokumenta.
