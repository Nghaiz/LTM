<#
.SYNOPSIS
    Collect the joint-drive facts P25 rests on, from the assemblies and assets themselves.

.DESCRIPTION
    WHY THIS EXISTS

    The P25 phase plan opens by saying the ragdoll diverged from the original Ravenfield because
    ActiveRaggy.cs lost exactly one line:

        jointDrive.mode = JointDriveMode.Position;   // "API Unity 5.5 da xoa"

    and concludes that PhysX 4 therefore interprets positionSpring / positionDamper differently,
    so the drive needs recalibrating. Section 5.4 of that plan asks for one check FIRST, because
    it can invalidate the premise: is ConfigurableJoint.targetAngularVelocity actually zero?

    This script runs that check, and the three others that bound it, against ground truth rather
    than recollection:

      1. JointDriveMode's numeric values, and the IL of JointDrive.get_mode / set_mode, read out
         of the UnityEngine.dll that Ravenfield.exe itself ships with (Unity 5.4.0f3).
      2. The same JointDrive shape in the Unity version this project builds on now.
      3. Every ConfigurableJoint in every prefab of both trees: targetAngularVelocity,
         rotationDriveMode, and the serialized drive values.
      4. Every SetDrive call site, and every runtime write to a drive-target field, in both
         C# trees.

    The answer that comes back is in tools/recovered/ragdoll-drive-facts.json, and it does not
    support the plan's premise. See plans/reports/2026-09-21-p25-ragdoll-drive.md.

    This is a fact collector, not a judge. It emits what it read; the report reasons over it.

.NOTES
    Needs the recovered Ravenfield build under tmp/ for the 5.4 assembly. It refuses to run
    without it rather than emitting a file with holes in it -- the committed JSON is the durable
    artifact, tmp/ is disposable.
#>
[CmdletBinding()]
param(
    # Managed/ of the shipped Ravenfield build. Supplies the Unity 5.4.0f3 UnityEngine.dll, which
    # is the only authority on what the original engine's JointDrive actually did.
    [string] $ShipManaged = "tmp/Ravenfield/Ravenfield_Data/Managed",

    # The Unity Editor executable. Same variable tools/build-player.ps1 and ci.ps1 read.
    [string] $UnityPath = $env:UNITY_PATH,

    # Decompiled original sources + the original project's exported assets.
    [string] $RecoveredRoot = "tmp/recovered",

    # This project's Unity tree.
    [string] $UnityProject = "Ironfront_Reborn",

    [string] $OutFile = "tools/recovered/ragdoll-drive-facts.json"
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------- metadata readers

# Open a CLI assembly for metadata-only reading. Nothing is loaded or executed, so a Unity 5.4
# assembly that could never run on .NET 8 is still fully readable.
function Open-Assembly([string] $Path) {
    $fs = [System.IO.File]::OpenRead((Resolve-Path $Path).Path)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
    $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
    [pscustomobject]@{ Stream = $fs; Pe = $pe; Md = $md }
}

function Close-Assembly($Handle) { $Handle.Pe.Dispose(); $Handle.Stream.Dispose() }

function Find-TypeDef($H, [string] $Name) {
    foreach ($th in $H.Md.TypeDefinitions) {
        $td = $H.Md.GetTypeDefinition($th)
        if ($H.Md.GetString($td.Name) -eq $Name) { return $td }
    }
    return $null
}

# [Obsolete("message")] and friends: a single-string constructor blob is
# 01 00 <compressed-length> <utf8 bytes> 00 00. Anything else returns just the attribute name.
function Read-AttributeString($Md, $CustomAttribute) {
    try {
        $blob = $Md.GetBlobBytes($CustomAttribute.Value)
        if ($blob.Length -lt 3 -or $blob[0] -ne 1 -or $blob[1] -ne 0) { return $null }
        $len = $blob[2]
        if ($len -eq 0xFF -or $len -eq 0x00) { return $null }        # null / empty string
        if ($len -ge 0x80) { return $null }                          # multi-byte length, not needed here
        return [System.Text.Encoding]::UTF8.GetString($blob, 3, $len)
    } catch { return $null }
}

function Get-AttributeName($Md, $CustomAttribute) {
    $ctor = $CustomAttribute.Constructor
    switch ($ctor.Kind) {
        "MemberReference" {
            $mr = $Md.GetMemberReference($ctor)
            $parent = $mr.Parent
            if ($parent.Kind -eq "TypeReference") { return $Md.GetString($Md.GetTypeReference($parent).Name) }
            return "<member>"
        }
        "MethodDefinition" {
            $mdef = $Md.GetMethodDefinition($ctor)
            return $Md.GetString($Md.GetTypeDefinition($mdef.GetDeclaringType()).Name)
        }
    }
    return "<unknown>"
}

# Fields, properties, per-property attributes, and the raw IL of every accessor. The IL is the
# point: a property can exist, be public, be documented, and still compile to a bare `ret`.
function Get-TypeFacts($H, [string] $TypeName) {
    $td = Find-TypeDef $H $TypeName
    if ($null -eq $td) { return $null }
    $md = $H.Md

    $fields = @()
    foreach ($fh in $td.GetFields()) {
        $fd = $md.GetFieldDefinition($fh)
        $fields += $md.GetString($fd.Name)
    }

    $props = @()
    foreach ($ph in $td.GetProperties()) {
        $pd = $md.GetPropertyDefinition($ph)
        $attrs = @()
        foreach ($ah in $pd.GetCustomAttributes()) {
            $ca = $md.GetCustomAttribute($ah)
            $attrs += [pscustomobject]@{
                name    = Get-AttributeName $md $ca
                message = Read-AttributeString $md $ca
            }
        }
        $props += [pscustomobject]@{ name = $md.GetString($pd.Name); attributes = $attrs }
    }

    $il = [ordered]@{}
    foreach ($mh in $td.GetMethods()) {
        $m = $md.GetMethodDefinition($mh)
        $n = $md.GetString($m.Name)
        if ($m.RelativeVirtualAddress -eq 0) { $il[$n] = "<no body>"; continue }
        $body = [System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($H.Pe, $m.RelativeVirtualAddress)
        $il[$n] = [BitConverter]::ToString($body.GetILBytes())
    }

    [pscustomobject]@{ type = $TypeName; fields = $fields; properties = $props; methodIL = $il }
}

# Int32-backed enum constants.
function Get-EnumFacts($H, [string] $TypeName) {
    $td = Find-TypeDef $H $TypeName
    if ($null -eq $td) { return $null }
    $md = $H.Md
    $vals = [ordered]@{}
    foreach ($fh in $td.GetFields()) {
        $fd = $md.GetFieldDefinition($fh)
        if (-not ($fd.Attributes.HasFlag([System.Reflection.FieldAttributes]::HasDefault))) { continue }
        $c = $md.GetConstant($fd.GetDefaultValue())
        $blob = $md.GetBlobBytes($c.Value)
        $vals[$md.GetString($fd.Name)] = [BitConverter]::ToInt32($blob, 0)
    }
    return $vals
}

# ---------------------------------------------------------------- asset + source readers

# One row per prefab that owns ConfigurableJoints. The load-bearing columns are
# targetAngularVelocityNonZero (the plan's section 5.4 check) and the rotationDriveMode histogram
# (ActiveRaggy writes slerpDrive, so anything but Slerp would mean the drive it sets is ignored).
function Get-PrefabJointFacts([string] $Root, [string] $Label) {
    $rows = @()
    if (-not (Test-Path $Root)) { return $rows }
    foreach ($f in Get-ChildItem -Path $Root -Recurse -Filter *.prefab -File -ErrorAction SilentlyContinue) {
        $text = Get-Content -LiteralPath $f.FullName -Raw -ErrorAction SilentlyContinue
        if ($null -eq $text -or $text -notmatch 'ConfigurableJoint') { continue }

        $joints = ([regex]::Matches($text, '(?m)^ConfigurableJoint:')).Count
        if ($joints -eq 0) { continue }

        $tav = [regex]::Matches($text, 'm_TargetAngularVelocity:\s*\{([^}]*)\}')
        $tavZero = 0; $tavNonZero = @()
        foreach ($m in $tav) {
            $v = $m.Groups[1].Value
            if ($v -match '^\s*x:\s*-?0(\.0+)?(E[+-]?\d+)?\s*,\s*y:\s*-?0(\.0+)?(E[+-]?\d+)?\s*,\s*z:\s*-?0(\.0+)?(E[+-]?\d+)?\s*$') {
                $tavZero++
            } else { $tavNonZero += $v.Trim() }
        }

        $modes = @{}
        foreach ($m in [regex]::Matches($text, 'm_RotationDriveMode:\s*(\d+)')) {
            $k = $m.Groups[1].Value
            $modes[$k] = 1 + ($(if ($modes.ContainsKey($k)) { $modes[$k] } else { 0 }))
        }

        $rows += [pscustomobject]@{
            tree                        = $Label
            path                        = (Resolve-Path -Relative $f.FullName) -replace '\\', '/'
            configurableJoints          = $joints
            targetAngularVelocityTotal  = $tav.Count
            targetAngularVelocityZero   = $tavZero
            targetAngularVelocityNonZero= $tavNonZero
            rotationDriveMode           = $modes
            usesAcceleration            = ([regex]::Matches($text, 'useAcceleration:\s*1')).Count
        }
    }
    return $rows
}

# SetDrive call sites (the drive values actually in force), plus any runtime write to a field that
# would change what the drive is aiming at. A write to targetAngularVelocity anywhere would defeat
# the section 5.4 check no matter what the prefabs serialize.
function Get-SourceDriveFacts([string] $Root, [string] $Label) {
    $calls = @()
    $writes = @()
    if (-not (Test-Path $Root)) { return [pscustomobject]@{ tree = $Label; setDriveCalls = $calls; driveTargetWrites = $writes } }

    # Anchored on a non-identifier character so an unrelated AI field named
    # lastSeenTargetVelocity is not reported as a write to Joint.targetVelocity.
    $watch = '(?<![A-Za-z0-9_])(targetAngularVelocity|targetVelocity|targetRotation|rotationDriveMode|useAcceleration|angularXDrive|angularYZDrive|slerpDrive)'
    foreach ($f in Get-ChildItem -Path $Root -Recurse -Filter *.cs -File -ErrorAction SilentlyContinue) {
        $i = 0
        foreach ($line in [System.IO.File]::ReadAllLines($f.FullName)) {
            $i++
            $rel = (Resolve-Path -Relative $f.FullName) -replace '\\', '/'
            if ($line -match 'SetDrive\s*\(\s*([0-9.eEf+-]+)\s*,\s*([0-9.eEf+-]+)\s*\)') {
                $calls += [pscustomobject]@{
                    tree = $Label; file = $rel; line = $i
                    spring = $Matches[1]; damper = $Matches[2]; text = $line.Trim()
                }
            }
            if ($line -match $watch) {
                $writes += [pscustomobject]@{ tree = $Label; file = $rel; line = $i; text = $line.Trim() }
            }
        }
    }
    [pscustomobject]@{ tree = $Label; setDriveCalls = $calls; driveTargetWrites = $writes }
}

# ---------------------------------------------------------------- collect

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Push-Location $repo
try {
    $shipUnityEngine = Join-Path $ShipManaged "UnityEngine.dll"
    if (-not (Test-Path $shipUnityEngine)) {
        throw "Ship build not found at '$shipUnityEngine'. The Unity 5.4 UnityEngine.dll is the only authority on what the original drive did; refusing to emit a partial file. Restore tmp/Ravenfield or pass -ShipManaged."
    }

    if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
        $guess = "D:/UnityEditor/6000.3.21f1/Editor/Unity.exe"
        if (Test-Path $guess) { $UnityPath = $guess }
    }
    if (-not $UnityPath -or -not (Test-Path $UnityPath)) {
        throw "Unity Editor not found. Set UNITY_PATH or pass -UnityPath; the current engine's JointDrive shape is half the comparison."
    }
    $editorData = Split-Path (Split-Path $UnityPath -Parent) -Parent
    $currentPhysics = Join-Path $editorData "Editor/Data/Managed/UnityEngine/UnityEngine.PhysicsModule.dll"
    if (-not (Test-Path $currentPhysics)) {
        $currentPhysics = Join-Path (Split-Path $UnityPath -Parent) "Data/Managed/UnityEngine/UnityEngine.PhysicsModule.dll"
    }
    if (-not (Test-Path $currentPhysics)) { throw "UnityEngine.PhysicsModule.dll not found under '$editorData'." }

    Write-Host "original engine : $shipUnityEngine"
    Write-Host "current engine  : $currentPhysics"

    $ho = Open-Assembly $shipUnityEngine
    $originalDrive = Get-TypeFacts $ho "JointDrive"
    $originalMode  = Get-EnumFacts $ho "JointDriveMode"
    $originalRot   = Get-EnumFacts $ho "RotationDriveMode"
    Close-Assembly $ho

    $hc = Open-Assembly $currentPhysics
    $currentDrive = Get-TypeFacts $hc "JointDrive"
    $currentMode  = Get-EnumFacts $hc "JointDriveMode"
    $currentRot   = Get-EnumFacts $hc "RotationDriveMode"
    Close-Assembly $hc

    $prefabs  = @()
    $prefabs += Get-PrefabJointFacts (Join-Path $UnityProject "Assets") "ironfront"
    $prefabs += Get-PrefabJointFacts (Join-Path $RecoveredRoot "UnityProject") "recovered"

    $sources  = @()
    $sources += Get-SourceDriveFacts (Join-Path $UnityProject "Assets/Scripts") "ironfront"
    $sources += Get-SourceDriveFacts (Join-Path $RecoveredRoot "src") "recovered"

    # ------------------------------------------------------------ assert before writing

    $problems = @()
    if ($null -eq $originalDrive) { $problems += "JointDrive absent from the 5.4 assembly" }
    if ($null -eq $originalMode)  { $problems += "JointDriveMode absent from the 5.4 assembly" }
    if ($null -eq $currentDrive)  { $problems += "JointDrive absent from the current assembly" }
    if (($prefabs | Where-Object tree -eq "ironfront").Count -lt 1) { $problems += "no jointed prefab found in $UnityProject" }
    if ((($sources | Where-Object tree -eq "ironfront").setDriveCalls).Count -lt 1) { $problems += "no SetDrive call site found in $UnityProject" }
    if ($problems.Count -gt 0) { throw "Refusing to write $OutFile - " + ($problems -join "; ") }

    $totalJoints = [int](($prefabs | Measure-Object -Property configurableJoints -Sum).Sum)
    $totalTav    = [int](($prefabs | Measure-Object -Property targetAngularVelocityTotal -Sum).Sum)
    $zeroTav     = [int](($prefabs | Measure-Object -Property targetAngularVelocityZero -Sum).Sum)

    # The drive is spring+damper toward Joint.targetRotation, and that target is computed by
    # ConfigurableJointExtensions.SetTargetRotationLocal. A difference there would move the drive's
    # aim without touching a single drive parameter, so both halves are hashed rather than assumed.
    $sharedFiles = @()
    foreach ($pair in @(
            @{ name = "ActiveRaggy.cs";                 a = "$UnityProject/Assets/Scripts/Assembly-CSharp/ActiveRaggy.cs";                 b = "$RecoveredRoot/src/Assembly-CSharp/ActiveRaggy.cs" },
            @{ name = "ConfigurableJointExtensions.cs"; a = "$UnityProject/Assets/Scripts/Assembly-CSharp/ConfigurableJointExtensions.cs"; b = "$RecoveredRoot/src/Assembly-CSharp/ConfigurableJointExtensions.cs" })) {
        $ha = if (Test-Path $pair.a) { (Get-FileHash -LiteralPath $pair.a -Algorithm SHA256).Hash } else { $null }
        $hb = if (Test-Path $pair.b) { (Get-FileHash -LiteralPath $pair.b -Algorithm SHA256).Hash } else { $null }
        $sharedFiles += [pscustomobject]@{
            file = $pair.name; ironfrontSha256 = $ha; recoveredSha256 = $hb
            identical = ($null -ne $ha -and $ha -eq $hb)
        }
    }

    $out = [ordered]@{
        generated = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssK")
        producedBy = "tools/ragdoll_drive_facts.ps1"
        question = "Does the ActiveRaggy line 'jointDrive.mode = JointDriveMode.Position' explain a ragdoll difference between the original build and this one?"
        engines = [ordered]@{
            original = [ordered]@{
                label = "Unity 5.4.0f3 - the engine Ravenfield.exe ships"
                assembly = $shipUnityEngine -replace '\\', '/'
                jointDrive = $originalDrive
                jointDriveMode = $originalMode
                rotationDriveMode = $originalRot
            }
            current = [ordered]@{
                label = "Unity 6000.3.21f1 - the engine this project builds on"
                assembly = $currentPhysics -replace '\\', '/'
                jointDrive = $currentDrive
                jointDriveMode = $currentMode
                rotationDriveMode = $currentRot
            }
        }
        prefabs = $prefabs
        sources = $sources
        sharedFiles = $sharedFiles
        totals = [ordered]@{
            configurableJoints = $totalJoints
            targetAngularVelocityFields = $totalTav
            targetAngularVelocityZero = $zeroTav
            targetAngularVelocityNonZero = $totalTav - $zeroTav
        }
    }

    $dir = Split-Path $OutFile -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = $out | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText((Join-Path $repo $OutFile), $json + "`n", (New-Object System.Text.UTF8Encoding($false)))

    Write-Host ""
    Write-Host "joints scanned              : $totalJoints"
    Write-Host "targetAngularVelocity zero  : $zeroTav of $totalTav"
    Write-Host "5.4 JointDrive.set_mode IL  : $($originalDrive.methodIL.set_mode)"
    Write-Host "5.4 JointDrive.get_mode IL  : $($originalDrive.methodIL.get_mode)"
    Write-Host "current JointDrive fields   : $($currentDrive.fields -join ', ')"
    Write-Host "wrote $OutFile"
}
finally { Pop-Location }
