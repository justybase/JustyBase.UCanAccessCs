param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

# Access/DAO is intentionally used only to author this fixture.  The managed
# provider reads and writes the resulting MSysComplexType_* child table without
# requiring Access at runtime.
$resolved = [IO.Path]::GetFullPath($OutputPath)
$access = $null
$db = $null
$recordset = $null
$history = $null
try {
    if ([IO.File]::Exists($resolved)) {
        Remove-Item -LiteralPath $resolved -Force
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolved)) | Out-Null

    $access = New-Object -ComObject Access.Application
    $access.Visible = $false
    $access.NewCurrentDatabase($resolved)
    $db = $access.CurrentDb()

    # DAO dbLong=4, dbComplexText=109.  AppendOnly causes Access to retain
    # the previous values in the complex child table with Version/Modified
    # metadata instead of replacing the collection in place.
    $tableDef = $db.CreateTableDef("VersionFixture")
    [void] $tableDef.Fields.Append($tableDef.CreateField("ID", 4))
    $historyField = $tableDef.CreateField("History", 109)
    [void] $tableDef.Fields.Append($historyField)
    [void] $db.TableDefs.Append($tableDef)
    $db.TableDefs.Refresh()

    $tableDef = $db.TableDefs("VersionFixture")
    $historyField = $tableDef.Fields("History")
    try {
        $historyField.Properties("AppendOnly").Value = $true
    }
    catch {
        $property = $historyField.CreateProperty("AppendOnly", 1, $true)
        [void] $historyField.Properties.Append($property)
    }

    $recordset = $db.OpenRecordset("VersionFixture")
    $recordset.AddNew()
    $recordset.Fields("ID").Value = 1
    $recordset.Update()
    $recordset.MoveLast()
    $history = $recordset.Fields("History").Value
    foreach ($value in @("first version", "second version", "third version")) {
        $history.AddNew()
        $history.Fields("Value").Value = $value
        $history.Update()
    }
    $recordset.Update()
}
finally {
    foreach ($com in @($history, $recordset, $db, $access)) {
        if ($com -ne $null) {
            try { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) | Out-Null } catch {}
        }
    }
}

Write-Output "Created $resolved"
