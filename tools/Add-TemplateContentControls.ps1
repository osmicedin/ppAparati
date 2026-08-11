param(
    [Parameter(Mandatory = $true)]
    [string]$TemplatePath
)

$markers = [ordered]@{
    'BROJ_ZAPISNIKA' = 'ReportNumber'
    'DATUM_ZAKLJUCIVANJA' = 'ConclusionDate'
    'NAZIV_KUPCA' = 'CustomerTitle'
    'LOKACIJA_MJESEC_GODINA' = 'LocationMonthYear'
    'NAZIV_NARUCIOCA' = 'CustomerOrderer'
    'PERIOD_OD' = 'PeriodFrom'
    'PERIOD_DO' = 'PeriodTo'
    'NAZIV_VLASNIKA' = 'CustomerOwner'
    'BROJ_STAVKI' = 'ConclusionCount'
}

$word = $null
$document = $null

try {
    $word = New-Object -ComObject Word.Application
    $word.Visible = $false
    $word.DisplayAlerts = 0
    $document = $word.Documents.Open($TemplatePath, $false, $false)
    # Referenca potiče iz legacy .doc formata. Bez konverzije Word pri snimanju
    # izravna (ukloni) novododane content-control elemente.
    $document.Convert()

    foreach ($entry in $markers.GetEnumerator()) {
        $range = $document.Content.Duplicate
        $find = $range.Find
        $find.ClearFormatting()
        $find.Text = $entry.Key
        $find.Forward = $true
        $find.Wrap = 0

        if (-not $find.Execute()) {
            throw "Marker '$($entry.Key)' nije pronađen u predlošku."
        }

        $control = $document.ContentControls.Add(1, $range)
        $control.Title = $entry.Value
        $control.Tag = $entry.Value
        $control.LockContentControl = $false
        $control.LockContents = $false
    }

    $document.Save()
}
finally {
    if ($document) {
        $document.Close([ref]0)
    }
    if ($word) {
        $word.Quit()
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
