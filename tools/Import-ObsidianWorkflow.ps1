param(
    [string]$VaultPath = $env:OPENZA_OBSIDIAN_VAULT,
    [string[]]$DatabasePath = @(
        "$env:LOCALAPPDATA\Packages\Openza.OpenzaTasks_rt595jwp8ay6e\LocalState\openza_tasks.db",
        "$env:LOCALAPPDATA\Packages\Openza.OpenzaTasks.Dev_rt595jwp8ay6e\LocalState\openza_tasks.db"
    ),
    [string]$SqlitePath = "C:\Program Files (x86)\Touch Portal\plugins\adb\platform-tools\sqlite3.exe",
    [string]$SqlOutputPath,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$sources = @(
    @{
        Source = "Personal"
        Path = Join-Path $VaultPath "3 Personal\Tasks"
        ProjectPath = Join-Path $VaultPath "3 Personal\Projects"
        ProjectKey = "personal_project"
        LabelKeys = @("personal_context")
    },
    @{
        Source = "Work"
        Path = Join-Path $VaultPath "2 Work\Tasks"
        ProjectPath = Join-Path $VaultPath "2 Work\Projects"
        ProjectKey = "work_project"
        LabelKeys = @("work_area", "assignee")
    }
)

function Convert-ObsidianValue {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    $values = if ($Value -is [array]) { $Value } else { @($Value) }
    $result = [System.Collections.Generic.List[string]]::new()

    foreach ($raw in $values) {
        if ($null -eq $raw) {
            continue
        }

        $value = ([string]$raw).Trim()
        if ([string]::IsNullOrWhiteSpace($value) -or $value -eq "[]") {
            continue
        }

        $linkMatches = [regex]::Matches($value, "\[\[([^\]|]+)(?:\|[^\]]+)?\]\]")
        if ($linkMatches.Count -gt 0) {
            foreach ($match in $linkMatches) {
                $name = $match.Groups[1].Value.Trim()
                if (-not [string]::IsNullOrWhiteSpace($name)) {
                    $result.Add($name)
                }
            }

            continue
        }

        if ($value.StartsWith("[") -and $value.EndsWith("]")) {
            $inner = $value.Substring(1, $value.Length - 2).Trim()
            if (-not [string]::IsNullOrWhiteSpace($inner)) {
                foreach ($part in ($inner -split ",")) {
                    $name = $part.Trim().Trim('"').Trim("'")
                    if (-not [string]::IsNullOrWhiteSpace($name)) {
                        $result.Add($name)
                    }
                }
            }

            continue
        }

        $result.Add($value.Trim('"').Trim("'"))
    }

    return @($result | Select-Object -Unique)
}

function Read-FrontMatter {
    param([string]$Path)

    $lines = Get-Content -LiteralPath $Path -TotalCount 160
    $map = @{}
    if ($lines.Count -eq 0 -or $lines[0].Trim() -ne "---") {
        return $map
    }

    $currentKey = $null
    for ($index = 1; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line.Trim() -eq "---") {
            break
        }

        if ($line -match "^([A-Za-z0-9_]+):\s*(.*)$") {
            $currentKey = $matches[1]
            $value = $matches[2]
            $map[$currentKey] = if ([string]::IsNullOrWhiteSpace($value)) { @() } else { $value }
            continue
        }

        if ($currentKey -and $line -match "^\s+-\s*(.*)$") {
            $existing = if ($map.ContainsKey($currentKey) -and $map[$currentKey] -is [array]) {
                @($map[$currentKey])
            } elseif ($map.ContainsKey($currentKey)) {
                @($map[$currentKey])
            } else {
                @()
            }

            $map[$currentKey] = @($existing + $matches[1])
        }
    }

    return $map
}

function Convert-Status {
    param([object]$Value)

    $status = Convert-ObsidianValue $Value | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($status)) {
        return "inbox"
    }

    $normalized = $status.ToLowerInvariant()
    if ($normalized -in @("inbox", "next", "waiting", "someday", "done")) {
        return $normalized
    }

    return "inbox"
}

function Convert-DateToUnix {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $dateText = $Value.Trim()
    if ($dateText -match "^(\d{4}-\d{2}-\d{2})") {
        $dateText = $matches[1]
    }

    $parsed = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse($dateText, [ref]$parsed)) {
        return $parsed.ToUnixTimeSeconds()
    }

    return $null
}

function Convert-SqlString {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) {
        return "NULL"
    }

    return "'" + ([string]$Value).Replace("'", "''") + "'"
}

function Convert-SqlNullableInt {
    param([Nullable[int64]]$Value)
    if ($null -eq $Value) {
        return "NULL"
    }

    return [string]$Value
}

function Convert-NameToId {
    param(
        [string]$Prefix,
        [string]$Name
    )

    $slug = $Name.ToLowerInvariant() -replace "[^a-z0-9]+", "_" -replace "^_+|_+$", ""
    if ([string]::IsNullOrWhiteSpace($slug)) {
        $slug = [guid]::NewGuid().ToString("N")
    }

    if ($slug.Length -gt 80) {
        $slug = $slug.Substring(0, 80)
    }

    return "$Prefix$slug"
}

function Read-NoteBody {
    param([string]$Path)

    $lines = Get-Content -LiteralPath $Path
    if ($lines.Count -eq 0) {
        return ""
    }

    $startIndex = 0
    if ($lines[0].Trim() -eq "---") {
        for ($index = 1; $index -lt $lines.Count; $index++) {
            if ($lines[$index].Trim() -eq "---") {
                $startIndex = $index + 1
                break
            }
        }
    }

    return (($lines | Select-Object -Skip $startIndex) -join [Environment]::NewLine).Trim()
}

function Convert-ObsidianTaskId {
    param(
        [string]$Source,
        [string]$FileName
    )

    return Convert-NameToId "obsidian_$($Source.ToLowerInvariant())_task_" $FileName
}

function New-ObsidianMetadataJson {
    param(
        [string]$Source,
        [string]$File,
        [string]$Kind
    )

    return [ordered]@{
        source = "obsidian"
        space = $Source
        kind = $Kind
        file = $File
    } | ConvertTo-Json -Compress
}

function Get-ObsidianWorkflowEntries {
    $entries = [System.Collections.Generic.List[object]]::new()

    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source.Path)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $source.Path -Filter "*.md" -File) {
            $frontMatter = Read-FrontMatter $file.FullName
            $type = Convert-ObsidianValue $frontMatter["type"] | Select-Object -First 1
            if ($type -ne "Task") {
                continue
            }

            $todoistId = Convert-ObsidianValue $frontMatter["todoist_id"] | Select-Object -First 1

            $labels = [System.Collections.Generic.List[string]]::new()
            foreach ($labelKey in $source.LabelKeys) {
                foreach ($label in (Convert-ObsidianValue $frontMatter[$labelKey])) {
                    $labels.Add($label)
                }
            }

            $entries.Add([pscustomobject]@{
                TodoistId = $todoistId
                ObsidianId = Convert-ObsidianTaskId $source.Source $file.BaseName
                Source = $source.Source
                Status = Convert-Status $frontMatter["task_status"]
                Project = Convert-ObsidianValue $frontMatter[$source.ProjectKey] | Select-Object -First 1
                Labels = @($labels | Select-Object -Unique)
                Scheduled = Convert-ObsidianValue $frontMatter["scheduled"] | Select-Object -First 1
                Recurrence = Convert-ObsidianValue $frontMatter["recurrence"] | Select-Object -First 1
                Title = $file.BaseName
                Description = Read-NoteBody $file.FullName
                Created = Convert-ObsidianValue $frontMatter["created"] | Select-Object -First 1
                File = $file.BaseName
            })
        }
    }

    return $entries
}

function Get-ObsidianProjectEntries {
    $entries = [System.Collections.Generic.List[object]]::new()

    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source.ProjectPath)) {
            continue
        }

        foreach ($file in Get-ChildItem -LiteralPath $source.ProjectPath -Filter "*.md" -File) {
            $frontMatter = Read-FrontMatter $file.FullName
            $type = Convert-ObsidianValue $frontMatter["type"] | Select-Object -First 1
            $status = Convert-ObsidianValue $frontMatter["project_status"] | Select-Object -First 1
            if ($type -ne "Project" -or [string]::IsNullOrWhiteSpace($status)) {
                continue
            }

            if ($status.ToLowerInvariant().Contains("completed") -or $status.ToLowerInvariant().Contains("archived")) {
                continue
            }

            $entries.Add([pscustomobject]@{
                Source = $source.Source
                Project = $file.BaseName
                Status = $status
                File = $file.BaseName
            })
        }
    }

    return $entries
}

function Get-DatabaseTaskIds {
    param([string]$Path)

    $ids = & $SqlitePath $Path "select source_external_id from tasks where source_integration_id='todoist';"
    $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($id in $ids) {
        if (-not [string]::IsNullOrWhiteSpace($id)) {
            [void]$set.Add($id)
        }
    }

    return $set
}

function Test-DatabaseColumn {
    param(
        [string]$Path,
        [string]$Table,
        [string]$Column
    )

    $columns = & $SqlitePath $Path "PRAGMA table_info($Table);"
    foreach ($columnInfo in $columns) {
        $parts = $columnInfo -split "\|"
        if ($parts.Count -gt 1 -and $parts[1] -eq $Column) {
            return $true
        }
    }

    return $false
}

function New-ImportSql {
    param(
        [object[]]$Entries,
        [object[]]$ProjectEntries,
        [string]$DatabasePath
    )

    $matched = @($Entries | Where-Object { $_.Status -ne "done" })
    $todoistMatched = @($matched | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.TodoistId) })
    $localTasks = @($matched | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.TodoistId) })
    $projectRoutes = @(
        $ProjectEntries
        $matched | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Project) }
    )
    $projects = @($projectRoutes |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Project) -and -not [string]::IsNullOrWhiteSpace([string]$_.Source) } |
        Group-Object Source, Project |
        Sort-Object Name |
        ForEach-Object { $_.Group[0] })
    $labels = @($matched | ForEach-Object { $_.Labels } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique | Sort-Object)
    $projectIds = @{}
    $labelIds = @{}

    $sql = [System.Collections.Generic.List[string]]::new()
    $sql.Add(".bail on")
    $sql.Add("PRAGMA foreign_keys=OFF;")
    $sql.Add("CREATE TABLE IF NOT EXISTS spaces (id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL, color TEXT DEFAULT '#808080', icon TEXT, sort_order INTEGER NOT NULL DEFAULT 0, is_archived INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL DEFAULT (strftime('%s', 'now')), updated_at INTEGER);")
    if (-not (Test-DatabaseColumn $DatabasePath "projects" "space_id")) {
        $sql.Add("ALTER TABLE projects ADD COLUMN space_id TEXT NOT NULL DEFAULT 'space_default';")
    }
    if (-not (Test-DatabaseColumn $DatabasePath "tasks" "space_id")) {
        $sql.Add("ALTER TABLE tasks ADD COLUMN space_id TEXT NOT NULL DEFAULT 'space_default';")
    }
    if (-not (Test-DatabaseColumn $DatabasePath "provider_source_items" "suggested_space_id")) {
        $sql.Add("ALTER TABLE provider_source_items ADD COLUMN suggested_space_id TEXT;")
    }
    $sql.Add("PRAGMA foreign_keys=ON;")
    $sql.Add("PRAGMA foreign_keys=ON;")
    $sql.Add("BEGIN IMMEDIATE;")

    $spaces = @($projectRoutes + $matched | Select-Object -ExpandProperty Source -Unique | Sort-Object)
    $spaceIds = @{}
    $spaceSortOrder = 0
    foreach ($space in $spaces) {
        $spaceId = Convert-NameToId "space_" $space
        $spaceIds[$space] = $spaceId
        $sql.Add("INSERT OR IGNORE INTO spaces (id, name, color, icon, sort_order, is_archived, created_at, updated_at) VALUES ($(Convert-SqlString $spaceId), $(Convert-SqlString $space), '#808080', 'Folder', $spaceSortOrder, 0, strftime('%s','now'), strftime('%s','now'));")
        $sql.Add("UPDATE spaces SET name=$(Convert-SqlString $space), sort_order=$spaceSortOrder, is_archived=0, updated_at=strftime('%s','now') WHERE id=$(Convert-SqlString $spaceId);")
        $spaceSortOrder++
    }

    $sortOrder = 0
    foreach ($entry in $projects) {
        $sortOrder++
        $project = [string]$entry.Project
        $space = [string]$entry.Source
        $spaceId = $spaceIds[$space]
        $projectKey = "$space|$project"
        $projectId = Convert-NameToId "obsidian_$($space.ToLowerInvariant())_project_" $project
        $projectIds[$projectKey] = $projectId
        $metadata = "{""source"":""obsidian"",""space"":""$space"",""importedFrom"":""$($project.Replace('"', '\"'))""}"
        $sql.Add("INSERT OR IGNORE INTO projects (id, space_id, integration_id, name, description, color, icon, sort_order, is_favorite, is_archived, status, provider_metadata, created_at, updated_at) VALUES ($(Convert-SqlString $projectId), $(Convert-SqlString $spaceId), 'openza_tasks', $(Convert-SqlString $project), $(Convert-SqlString "Imported from Obsidian $space workflow setup."), '#808080', 'Folder', $sortOrder, 0, 0, 'active', $(Convert-SqlString $metadata), strftime('%s','now'), strftime('%s','now'));")
        $sql.Add("UPDATE projects SET space_id=$(Convert-SqlString $spaceId), name=$(Convert-SqlString $project), description=$(Convert-SqlString "Imported from Obsidian $space workflow setup."), sort_order=$sortOrder, is_archived=0, status='active', provider_metadata=$(Convert-SqlString $metadata), updated_at=strftime('%s','now') WHERE id=$(Convert-SqlString $projectId);")
    }

    $sortOrder = 0
    foreach ($label in $labels) {
        $sortOrder++
        $labelId = Convert-NameToId "obsidian_label_" $label
        $labelIds[$label] = $labelId
        $sql.Add("INSERT OR IGNORE INTO labels (id, integration_id, name, color, description, sort_order, is_favorite, provider_metadata, created_at) VALUES ($(Convert-SqlString $labelId), 'openza_tasks', $(Convert-SqlString $label), '#808080', 'Imported from Obsidian workflow setup.', $sortOrder, 0, '{""source"":""obsidian""}', strftime('%s','now'));")
        $sql.Add("UPDATE labels SET name=$(Convert-SqlString $label), description='Imported from Obsidian workflow setup.', sort_order=$sortOrder, provider_metadata='{""source"":""obsidian""}' WHERE id=$(Convert-SqlString $labelId);")
    }

    foreach ($entry in $todoistMatched) {
        $projectName = [string]$entry.Project
        $space = [string]$entry.Source
        $spaceId = $spaceIds[$space]
        $projectKey = "$([string]$entry.Source)|$projectName"
        $projectId = if (-not [string]::IsNullOrWhiteSpace($projectName) -and $projectIds.ContainsKey($projectKey)) { $projectIds[$projectKey] } else { $null }
        $scheduledAt = Convert-DateToUnix $entry.Scheduled
        $recurrence = if ([string]::IsNullOrWhiteSpace($entry.Recurrence)) { $null } else { $entry.Recurrence }

        $sql.Add("UPDATE tasks SET space_id=$(Convert-SqlString $spaceId), workflow_status=$(Convert-SqlString $entry.Status), project_id=$(Convert-SqlString $projectId), planned_date=$(Convert-SqlNullableInt $scheduledAt), recurrence_rule=$(Convert-SqlString $recurrence), updated_at=strftime('%s','now') WHERE source_integration_id='todoist' AND source_external_id=$(Convert-SqlString $entry.TodoistId);")
        $sql.Add("UPDATE provider_source_items SET suggested_space_id=$(Convert-SqlString $spaceId) WHERE provider_connection_id IN (SELECT source_connection_id FROM tasks WHERE source_integration_id='todoist' AND source_external_id=$(Convert-SqlString $entry.TodoistId)) AND external_id=$(Convert-SqlString $entry.TodoistId);")

        if ($entry.Labels.Count -gt 0) {
            $sql.Add("DELETE FROM task_labels WHERE task_id IN (SELECT id FROM tasks WHERE source_integration_id='todoist' AND source_external_id=$(Convert-SqlString $entry.TodoistId)) AND label_id LIKE 'obsidian_label_%';")
            foreach ($label in $entry.Labels) {
                if ($labelIds.ContainsKey($label)) {
                    $sql.Add("INSERT OR IGNORE INTO task_labels (task_id, label_id) SELECT id, $(Convert-SqlString $labelIds[$label]) FROM tasks WHERE source_integration_id='todoist' AND source_external_id=$(Convert-SqlString $entry.TodoistId);")
                }
            }
        }
    }

    foreach ($entry in $localTasks) {
        $taskId = [string]$entry.ObsidianId
        $projectName = [string]$entry.Project
        $space = [string]$entry.Source
        $spaceId = $spaceIds[$space]
        $projectKey = "$([string]$entry.Source)|$projectName"
        $projectId = if (-not [string]::IsNullOrWhiteSpace($projectName) -and $projectIds.ContainsKey($projectKey)) { $projectIds[$projectKey] } else { $null }
        $scheduledAt = Convert-DateToUnix $entry.Scheduled
        $createdAt = Convert-DateToUnix $entry.Created
        $createdValue = if ($null -eq $createdAt) { "strftime('%s','now')" } else { [string]$createdAt }
        $recurrence = if ([string]::IsNullOrWhiteSpace($entry.Recurrence)) { $null } else { $entry.Recurrence }
        $metadata = New-ObsidianMetadataJson $space ([string]$entry.File) "task"

        $sql.Add("INSERT OR IGNORE INTO tasks (id, space_id, integration_id, title, description, project_id, priority, completion_state, workflow_status, planned_date, recurrence_rule, local_metadata, created_at, updated_at) VALUES ($(Convert-SqlString $taskId), $(Convert-SqlString $spaceId), 'openza_tasks', $(Convert-SqlString $entry.Title), $(Convert-SqlString $entry.Description), $(Convert-SqlString $projectId), 3, 'open', $(Convert-SqlString $entry.Status), $(Convert-SqlNullableInt $scheduledAt), $(Convert-SqlString $recurrence), $(Convert-SqlString $metadata), $createdValue, strftime('%s','now'));")
        $sql.Add("UPDATE tasks SET space_id=$(Convert-SqlString $spaceId), workflow_status=$(Convert-SqlString $entry.Status), project_id=$(Convert-SqlString $projectId), planned_date=$(Convert-SqlNullableInt $scheduledAt), recurrence_rule=$(Convert-SqlString $recurrence), local_metadata=$(Convert-SqlString $metadata), updated_at=strftime('%s','now') WHERE id=$(Convert-SqlString $taskId);")

        $sql.Add("DELETE FROM task_labels WHERE task_id=$(Convert-SqlString $taskId) AND label_id LIKE 'obsidian_label_%';")
        foreach ($label in $entry.Labels) {
            if ($labelIds.ContainsKey($label)) {
                $sql.Add("INSERT OR IGNORE INTO task_labels (task_id, label_id) VALUES ($(Convert-SqlString $taskId), $(Convert-SqlString $labelIds[$label]));")
            }
        }
    }

    if ($spaceIds.ContainsKey("Personal")) {
        $personalSpaceId = $spaceIds["Personal"]
        $sql.Add("UPDATE tasks SET space_id=$(Convert-SqlString $personalSpaceId), updated_at=strftime('%s','now') WHERE source_integration_id='todoist' AND space_id='space_default' AND source_connection_id IS NOT NULL AND source_external_id IN (SELECT external_id FROM provider_source_items WHERE source_project_name IN ('Personal Tasks', 'Notes'));")
        $sql.Add("UPDATE provider_source_items SET suggested_space_id=$(Convert-SqlString $personalSpaceId), updated_at=strftime('%s','now') WHERE suggested_space_id IS NULL AND source_project_name IN ('Personal Tasks', 'Notes');")
    }

    if ($spaceIds.ContainsKey("Work")) {
        $workSpaceId = $spaceIds["Work"]
        $sql.Add("UPDATE tasks SET space_id=$(Convert-SqlString $workSpaceId), updated_at=strftime('%s','now') WHERE source_integration_id='todoist' AND space_id='space_default' AND source_connection_id IS NOT NULL AND source_external_id IN (SELECT external_id FROM provider_source_items WHERE source_project_name = 'Work Tasks');")
        $sql.Add("UPDATE provider_source_items SET suggested_space_id=$(Convert-SqlString $workSpaceId), updated_at=strftime('%s','now') WHERE suggested_space_id IS NULL AND source_project_name = 'Work Tasks';")
    }

    $sql.Add("DELETE FROM projects WHERE id LIKE 'obsidian_project_%' AND id NOT LIKE 'obsidian_personal_project_%' AND id NOT LIKE 'obsidian_work_project_%' AND id NOT IN (SELECT project_id FROM tasks WHERE project_id IS NOT NULL);")

    $sql.Add("COMMIT;")
    return ($sql -join [Environment]::NewLine)
}

if (-not (Test-Path -LiteralPath $SqlitePath)) {
    throw "sqlite3.exe not found at $SqlitePath"
}

if ([string]::IsNullOrWhiteSpace($VaultPath) -or -not (Test-Path -LiteralPath $VaultPath)) {
    throw "Set -VaultPath or OPENZA_OBSIDIAN_VAULT to a valid Obsidian vault path."
}

$entries = @(Get-ObsidianWorkflowEntries)
$projectEntries = @(Get-ObsidianProjectEntries)
Write-Host "Obsidian task notes: $($entries.Count)"
Write-Host "Obsidian active project notes: $($projectEntries.Count)"

foreach ($path in $DatabasePath) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Warning "Database not found: $path"
        continue
    }

    $dbIds = Get-DatabaseTaskIds $path
    $activeMatches = @($entries | Where-Object {
        $_.Status -ne "done" -and (
            [string]::IsNullOrWhiteSpace([string]$_.TodoistId) -or
            $dbIds.Contains($_.TodoistId)
        )
    })
    $doneMatches = @($entries | Where-Object { $_.Status -eq "done" -and $dbIds.Contains($_.TodoistId) })
    $todoistMatches = @($activeMatches | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.TodoistId) })
    $localMatches = @($activeMatches | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.TodoistId) })
    $projects = @($projectEntries + $activeMatches | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Project) } | Group-Object Source, Project | ForEach-Object { $_.Group[0].Project })
    $labels = @($activeMatches | ForEach-Object { $_.Labels } | Where-Object { $_ } | Select-Object -Unique)

    Write-Host ""
    Write-Host $path
    Write-Host "  Matched active Todoist tasks: $($todoistMatches.Count)"
    Write-Host "  Obsidian-local active tasks to import: $($localMatches.Count)"
    Write-Host "  Done in Obsidian but active in Openza, skipped: $($doneMatches.Count)"
    Write-Host "  Projects to upsert: $($projects.Count)"
    Write-Host "  Labels to upsert: $($labels.Count)"
    $activeMatches | Group-Object Status | Sort-Object Name | ForEach-Object {
        Write-Host "  Status $($_.Name): $($_.Count)"
    }

    if (-not [string]::IsNullOrWhiteSpace($SqlOutputPath)) {
        $sql = New-ImportSql $activeMatches $projectEntries $path
        $target = if ($DatabasePath.Count -gt 1) {
            $baseName = [System.IO.Path]::GetFileNameWithoutExtension($path)
            $directory = Split-Path -Parent $SqlOutputPath
            $name = [System.IO.Path]::GetFileNameWithoutExtension($SqlOutputPath)
            $extension = [System.IO.Path]::GetExtension($SqlOutputPath)
            Join-Path $directory "$name-$baseName$extension"
        } else {
            $SqlOutputPath
        }

        [System.IO.File]::WriteAllText($target, $sql, [System.Text.UTF8Encoding]::new($false))
        Write-Host "  SQL: $target"
    }

    if ($Apply) {
        $backupPath = "$path.obsidian-import-$(Get-Date -Format 'yyyyMMddHHmmss').bak"
        Copy-Item -LiteralPath $path -Destination $backupPath -Force
        Write-Host "  Backup: $backupPath"

        $sql = New-ImportSql $activeMatches $projectEntries $path
        $tempSql = [System.IO.Path]::GetTempFileName()
        [System.IO.File]::WriteAllText($tempSql, $sql, [System.Text.UTF8Encoding]::new($false))
        try {
            & $SqlitePath $path ".read '$tempSql'"
            if ($LASTEXITCODE -ne 0) {
                throw "sqlite3 failed while applying generated SQL to $path."
            }
        }
        finally {
            Remove-Item -LiteralPath $tempSql -Force -ErrorAction SilentlyContinue
        }

        Write-Host "  Applied."
    }
}
