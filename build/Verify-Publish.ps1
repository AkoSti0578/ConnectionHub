<#
.SYNOPSIS
    Fails when a managed assembly in the publish folder references an assembly that is not shipped
    (e.g. a .NET 9 assembly in a .NET 8 self-contained app), which would crash at runtime.
#>
param([Parameter(Mandatory)] [string]$PublishDir)

$ErrorActionPreference = "Stop"
$files = Get-ChildItem $PublishDir -Filter *.dll -File
$available = @{}
foreach ($f in $files) {
    try { $available[[System.Reflection.AssemblyName]::GetAssemblyName($f.FullName).Name] = [System.Reflection.AssemblyName]::GetAssemblyName($f.FullName).Version }
    catch { } # native dll
}

# Assemblies that belong to the app itself or to NuGet packages (not the .NET runtime pack).
$deps = Get-Content (Get-ChildItem $PublishDir -Filter *.deps.json | Select-Object -First 1).FullName -Raw | ConvertFrom-Json
$own = @{}
foreach ($lib in $deps.libraries.PSObject.Properties) {
    if ($lib.Value.type -notin @('project', 'package')) { continue }
    foreach ($target in $deps.targets.PSObject.Properties) {
        $entry = $target.Value.PSObject.Properties[$lib.Name]
        if ($entry -and $entry.Value.runtime) {
            foreach ($asset in $entry.Value.runtime.PSObject.Properties.Name) { $own[[IO.Path]::GetFileName($asset)] = $true }
        }
    }
}

# Optional references that are only used on code paths the app never takes.
$ignoredMissing = @('Microsoft.Windows.SDK.NET', 'WinRT.Runtime')

$problems = @()
foreach ($f in $files) {
    if (-not $available.ContainsKey([IO.Path]::GetFileNameWithoutExtension($f.Name))) { continue }
    $checkMissing = $own.ContainsKey($f.Name)
    $stream = [IO.File]::OpenRead($f.FullName)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { continue }
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($h in $md.AssemblyReferences) {
            $ref = $md.GetAssemblyReference($h)
            $name = $md.GetString($ref.Name)
            if ($name -in @('mscorlib', 'netstandard', 'WindowsBase') -and $available.ContainsKey($name)) { continue }
            if (-not $available.ContainsKey($name)) {
                if (-not $checkMissing -or $name -in $ignoredMissing) { continue }
                $problems += "$($f.Name) -> $name $($ref.Version) (missing)"
            }
            elseif ($available[$name] -lt $ref.Version) {
                $problems += "$($f.Name) -> $name $($ref.Version) (shipped: $($available[$name]))"
            }
        }
    }
    finally { $stream.Dispose() }
}

if ($problems.Count -gt 0) {
    $problems | Sort-Object -Unique | ForEach-Object { Write-Host "::error::$_" }
    throw "Publish output references assemblies that are not shipped."
}
Write-Host "All assembly references in $PublishDir resolve ($($own.Count) app/package assemblies checked)."

