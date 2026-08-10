param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

# Microsoft Access is used deliberately here: DAO is the reliable public API
# for creating attachment and multi-value fields. This is a fixture generator,
# not a runtime dependency of UCanAccess-csharp.
$resolved = [IO.Path]::GetFullPath($OutputPath)
$payload = [IO.Path]::Combine([IO.Path]::GetTempPath(), "uca-attachment-$([Guid]::NewGuid().ToString('N')).txt")
$access = $null
$db = $null
$recordset = $null
try {
    if ([IO.File]::Exists($resolved)) {
        Remove-Item -LiteralPath $resolved -Force
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolved)) | Out-Null
    Set-Content -LiteralPath $payload -Value "fixture attachment" -Encoding UTF8

    $access = New-Object -ComObject Access.Application
    $access.Visible = $false
    $access.NewCurrentDatabase($resolved)
    $db = $access.CurrentDb()

    # DAO DataTypeEnum: dbLong=4, dbAttachment=101, dbComplexText=109.
    $tableDef = $db.CreateTableDef("ComplexFixture")
    [void] $tableDef.Fields.Append($tableDef.CreateField("ID", 4))
    [void] $tableDef.Fields.Append($tableDef.CreateField("Tags", 109))
    [void] $tableDef.Fields.Append($tableDef.CreateField("Files", 101))
    [void] $db.TableDefs.Append($tableDef)

    $recordset = $db.OpenRecordset("ComplexFixture")
    $recordset.AddNew()
    $recordset.Fields("ID").Value = 1
    $recordset.Update()
    $recordset.MoveLast()
    $recordset.Edit()

    $tags = $recordset.Fields("Tags").Value
    foreach ($tag in @("alpha", "beta")) {
        $tags.AddNew()
        $tags.Fields("Value").Value = $tag
        $tags.Update()
    }

    $files = $recordset.Fields("Files").Value
    $files.AddNew()
    $files.Fields("FileData").LoadFromFile($payload)
    $files.Update()
    $recordset.Update()
}
finally {
    if ($recordset -ne $null) {
        try { $recordset.Close() } catch {}
    }
    if ($access -ne $null) {
        try { $access.Quit(2) } catch {}
    }
    if ($payload -and [IO.File]::Exists($payload)) {
        Remove-Item -LiteralPath $payload -Force -ErrorAction SilentlyContinue
    }
    foreach ($com in @($recordset, $db, $access)) {
        if ($com -ne $null) {
            try { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) | Out-Null } catch {}
        }
    }
}

Write-Output "Created $resolved"
