param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'

# Bridge game API changes in the pinned MIT encoder.
function Replace-Reference([string]$sourceText, [string]$before, [string]$after) {
    if ([regex]::Matches($sourceText, [regex]::Escape($before)).Count -ne 1) {
        throw "Unexpected AlphaDefect source for $before"
    }
    return $sourceText.Replace($before, $after)
}

$fool = Get-Content -LiteralPath "$Source/Characters/Fool.cs" -Raw -Encoding UTF8
$fool = Replace-Reference $fool 'a.Status == ArrowStatus.Destroyed' 'a.Status == LegacyDestroyedArrow'
$fool = Replace-Reference $fool 'MegaCrit.Sts2.Core.Commands.MelodyCmd.MelodyCardsPlayedThisTurn(me)' 'CountFinishedMelodyCards(cs, me)'
$gains = Get-Content -LiteralPath "$Source/Gains26.cs" -Raw -Encoding UTF8
$gains = Replace-Reference $gains 'l is FreeCardPower or FreeAttackPower' 'l.GetType() == LegacyFreeCardPower || l is FreeAttackPower'
$gains = Replace-Reference $gains 'FreeCardPower => pw.Amount' 'var power when power.GetType() == LegacyFreeCardPower => pw.Amount'
$cards = Get-Content -LiteralPath "$Source/Cards.cs" -Raw -Encoding UTF8
$cards = Replace-Reference $cards 'copy.UpgradeInternal();' 'UpgradePreview(copy);'

$dir = New-Item -Path $Destination -ItemType Directory -Force
[IO.File]::WriteAllText((Join-Path $dir.FullName 'Fool.cs'), $fool)
[IO.File]::WriteAllText((Join-Path $dir.FullName 'Gains26.cs'), $gains)
[IO.File]::WriteAllText((Join-Path $dir.FullName 'Cards.cs'), $cards)
