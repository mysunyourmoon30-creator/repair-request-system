<#
  Runs ../sql/capture-snapshot.sql against the dedicated perf database and writes every result set to one JSON file.
  Read-only. Example:
    .\capture-snapshot.ps1 -Out C:\scratch\before-large.json
#>
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Database = 'RepairRequestDb_PerfTimeline',
    [string] $Server = '(localdb)\MSSQLLocalDB'
)

$sql = Get-Content -Raw -Path (Join-Path $PSScriptRoot '..\sql\capture-snapshot.sql')
$connection = New-Object System.Data.SqlClient.SqlConnection "Server=$Server;Database=$Database;Trusted_Connection=True"
$connection.Open()
try {
    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    $command.CommandTimeout = 300
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
    $set = New-Object System.Data.DataSet
    [void]$adapter.Fill($set)

    $names = 'tables', 'dbts', 'statements', 'plans', 'waits', 'tempdb', 'sessions'
    $result = [ordered]@{}
    for ($i = 0; $i -lt $set.Tables.Count; $i++) {
        $rows = foreach ($row in $set.Tables[$i].Rows) {
            $o = [ordered]@{}
            foreach ($column in $set.Tables[$i].Columns) {
                $value = $row[$column.ColumnName]
                $o[$column.ColumnName] = if ($value -is [System.DBNull]) { $null } else { $value }
            }
            [pscustomobject]$o
        }
        $result[$names[$i]] = @($rows)
    }

    $result | ConvertTo-Json -Depth 6 | Set-Content -Path $Out -Encoding utf8
}
finally {
    $connection.Close()
}
