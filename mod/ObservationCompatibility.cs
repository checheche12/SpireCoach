using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Headless;

public sealed partial class Observation
{
    // v0.114 added an optional silent argument to the game's upgrade method.
    private static readonly Action<CardModel> UpgradePreview = BindUpgradePreview();
    private static Action<CardModel> BindUpgradePreview()
    {
        var type = typeof(CardModel);
        if (type.GetMethod(nameof(CardModel.UpgradeInternal), [typeof(bool)]) is { } method)
        {
            var upgrade = method.CreateDelegate<Action<CardModel, bool>>();
            return card => upgrade(card, false);
        }
        return type.GetMethod(nameof(CardModel.UpgradeInternal), Type.EmptyTypes)!.CreateDelegate<Action<CardModel>>();
    }

    // These states only exist through game v0.113.1. Preserve the model's input slots on both versions.
    private static readonly ArrowStatus? LegacyDestroyedArrow =
        Enum.TryParse<ArrowStatus>("Destroyed", out var status) ? status : null;
    private static readonly Type? LegacyFreeCardPower =
        typeof(PowerModel).Assembly.GetType("MegaCrit.Sts2.Core.Models.Powers.FreeCardPower");

    private static int CountFinishedMelodyCards(ICombatState combat, Player player) =>
        CombatManager.Instance.History.CardPlaysFinished.Count(play =>
            play.CardPlay.Player == player && play.HappenedThisTurn(combat) && IsMelody(play.CardPlay.Card));
}
