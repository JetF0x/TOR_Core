param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,
    [Parameter(Mandatory = $true)]
    [string]$TorAssembly
)

# Run in a fresh Windows PowerShell process against a compiled TOR_Core.dll and
# the matching installed game assemblies. No game installation is modified.
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;

public static class OrcBossTestAssemblyResolver
{
    public static void Install(string[] directories)
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            foreach (string directory in directories)
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
    }
}
'@

$gameBin = Join-Path $GameRoot 'bin\Win64_Shipping_Client'
$directories = @((Split-Path -Parent $TorAssembly), $gameBin)
foreach ($module in @('Native', 'SandBox', 'StoryMode', 'CustomBattle'))
{
    $directories += Join-Path $GameRoot "Modules\$module\bin\Win64_Shipping_Client"
}
[OrcBossTestAssemblyResolver]::Install($directories)

$campaignAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $gameBin 'TaleWorlds.CampaignSystem.dll'))
$coreAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $gameBin 'TaleWorlds.Core.dll'))
$tor = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $TorAssembly).Path)
$instanceFlags = [Reflection.BindingFlags]'Instance, Public, NonPublic'
$staticFlags = [Reflection.BindingFlags]'Static, Public, NonPublic'

function New-Uninitialized([Type]$type)
{
    return [Runtime.Serialization.FormatterServices]::GetUninitializedObject($type)
}

function Set-TestField([Type]$type, [object]$instance, [string]$name, [object]$value)
{
    $field = $type.GetField($name, $instanceFlags)
    if ($null -eq $field) { throw "Game field '$name' was not found on $type." }
    $field.SetValue($instance, $value)
}

$campaignType = $campaignAssembly.GetType('TaleWorlds.CampaignSystem.Campaign', $true)
$mobilePartyType = $campaignAssembly.GetType('TaleWorlds.CampaignSystem.Party.MobileParty', $true)
$partyType = $campaignAssembly.GetType('TaleWorlds.CampaignSystem.Party.PartyBase', $true)
$sideType = $campaignAssembly.GetType('TaleWorlds.CampaignSystem.MapEvents.MapEventSide', $true)
$mapEventType = $campaignAssembly.GetType('TaleWorlds.CampaignSystem.MapEvents.MapEvent', $true)
$questBaseType = $campaignAssembly.GetType('TaleWorlds.CampaignSystem.QuestBase', $true)
$journalType = $campaignAssembly.GetType('TaleWorlds.CampaignSystem.JournalLog', $true)
$battleStateType = $coreAssembly.GetType('TaleWorlds.Core.BattleState', $true)
$battleSideType = $coreAssembly.GetType('TaleWorlds.Core.BattleSideEnum', $true)
$currentField = $campaignType.GetField('<Current>k__BackingField', $staticFlags)
$oldCampaign = $currentField.GetValue($null)

# Real engine objects with only the managed state read by the production handler
# initialized. Bypass campaign/quest constructors, which require running the game.
$campaign = New-Uninitialized $campaignType
$mobileParty = New-Uninitialized $mobilePartyType
$party = New-Uninitialized $partyType
$side = New-Uninitialized $sideType
Set-TestField $campaignType $campaign '<MainParty>k__BackingField' $mobileParty
Set-TestField $mobilePartyType $mobileParty '<Party>k__BackingField' $party
Set-TestField $partyType $party '_mapEventSide' $side
$currentField.SetValue($null, $campaign)

# Quest finalizers refer to this publisher; create its actual implementation so
# they remain safe when these isolated quest instances are collected.
$publisherType = $tor.GetType('TOR_Core.Utilities.TORCampaignEvents', $true)
$publisher = [Activator]::CreateInstance($publisherType)
$failures = [Collections.Generic.List[string]]::new()
$checks = 0

try
{
    foreach ($questName in @('OrcBossQuest1', 'OrcBossQuest2'))
    {
        $questType = $tor.GetType("TOR_Core.Quests.Careers.$questName", $true)
        $required = [int]$questType.GetField('RequiredBattlesWon', $staticFlags).GetRawConstantValue()
        $handler = $questType.GetMethod('OnMapEventEnded', $instanceFlags)

        foreach ($playerSide in @('Attacker', 'Defender'))
        {
            Set-TestField $sideType $side '<MissionSide>k__BackingField' ([Enum]::Parse($battleSideType, $playerSide))
            $opponentSide = if ($playerSide -eq 'Attacker') { 'Defender' } else { 'Attacker' }
            $cases = @(
                @{ Name = 'no winner'; State = 'None'; Won = $false; Initial = 2 },
                @{ Name = 'pullback'; State = 'DefenderPullBack'; Won = $false; Initial = 2 },
                @{ Name = 'defeat'; State = "${opponentSide}Victory"; Won = $false; Initial = 2 },
                @{ Name = 'victory'; State = "${playerSide}Victory"; Won = $true; Initial = 2 },
                @{ Name = 'defeat at completion threshold'; State = "${opponentSide}Victory"; Won = $false; Initial = $required - 1 },
                @{ Name = 'victory at completion threshold'; State = "${playerSide}Victory"; Won = $true; Initial = $required - 1 }
            )

            foreach ($case in $cases)
            {
                $quest = New-Uninitialized $questType
                $journal = New-Uninitialized $journalType
                Set-TestField $journalType $journal 'Range' $required
                $journal.UpdateCurrentProgress($case.Initial)
                Set-TestField $questType $quest '_taskBattlesWon' $journal
                Set-TestField $questType $quest '_currentBattlesWon' $case.Initial
                $journalsField = $questBaseType.GetField('_journalEntries', $instanceFlags)
                $journals = [Activator]::CreateInstance($journalsField.FieldType)
                $journals.Add($journal)
                $journalsField.SetValue($quest, $journals)
                $mapEvent = New-Uninitialized $mapEventType
                Set-TestField $mapEventType $mapEvent '_battleState' ([Enum]::Parse($battleStateType, $case.State))

                $handler.Invoke($quest, @($mapEvent)) | Out-Null

                $expected = $case.Initial + [int]$case.Won
                $actual = [int]$questType.GetField('_currentBattlesWon', $instanceFlags).GetValue($quest)
                $ready = [bool]$questType.GetField('_readyToComplete', $instanceFlags).GetValue($quest)
                $expectedReady = $expected -ge $required
                $checks++
                if ($actual -ne $expected -or $journal.CurrentProgress -ne $expected -or $ready -ne $expectedReady)
                {
                    $failures.Add("$questName / $playerSide / $($case.Name): expected wins/log=$expected ready=$expectedReady; got wins=$actual log=$($journal.CurrentProgress) ready=$ready")
                }
            }
        }
    }
}
finally
{
    $currentField.SetValue($null, $oldCampaign)
}

if ($failures.Count -gt 0)
{
    $failures | ForEach-Object { Write-Output "FAIL: $_" }
    throw "$($failures.Count) of $checks production-handler checks failed."
}

Write-Output "PASS: $checks production-handler checks (both quests, both player sides, no winner, pullback, defeat, victory and completion threshold)."
