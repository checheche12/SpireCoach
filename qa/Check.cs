using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.CardSelection;
using Cards = MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace SpireCoachCheck;

// Only compiled into the separate test mod. Refuse to run outside the disposable profile.
[ModInitializer(nameof(Initialize))]
public static class Check
{
    public static void Initialize()
    {
        string character = CommandLineHelper.GetValue("coach-character") ?? "IRONCLAD";
        string profile = Path.GetFileName(OS.GetUserDataDir());
        if ((profile != "SpireCoach-QA" && profile != "SpireCoach-QA-" + character && profile != "SpireCoach-QA-" + character + "-Stable"
                && profile != "SpireCoach-QA-114-" + character)
            || CommandLineHelper.GetValue("force-steam") != "off") return;
        Callable.From(() => { _ = Run(CommandLineHelper.HasArg("coach-rollout-check") ? "SILENT" : character); }).CallDeferred();
    }
    private static async Task Run(string character)
    {
        var tree = (SceneTree)Godot.Engine.GetMainLoop();
        async Task Wait(Func<bool> done, string name)
        {
            ulong end = Time.GetTicksMsec() + 60000;
            while (!done()) { if (Time.GetTicksMsec() > end) throw new TimeoutException(name); await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame); }
        }
        try
        {
            await Wait(() => NGame.Instance?.MainMenu?.IsVisibleInTree() == true, "menu");
            if (CommandLineHelper.HasArg("coach-deck-size"))
            {
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetSize(new Vector2I(1280, 800));
                await tree.ToSignal(tree.CreateTimer(.5), SceneTreeTimer.SignalName.Timeout);
                if (DisplayServer.WindowGetSize() != new Vector2I(1280, 800)) throw new Exception("Deck window size");
                GD.Print("[CoachCheck] 1280x800 window");
            }
            var hud = tree.Root.GetNode("SpireCoachHUD");
            // A QA process may run beside the player's active game and search helper.
            var runtime = hud.GetNode("CoachControls").GetType().Assembly.GetType("SpireCoach.SearchRuntime")!;
            runtime.GetProperty("CacheRoot", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, Path.Combine(OS.GetUserDataDir(), "search-cache"));
            var panel = hud.GetNode<PanelContainer>("Recommendations");
            var setup = (Button)panel.FindChild("Download", true, false)!;
            if (setup.IsVisibleInTree())
            {
                setup.EmitSignal(BaseButton.SignalName.Pressed);
                await Wait(() => !setup.IsVisibleInTree(), "first-run vocabulary download");
            }
            SaveManager.Instance.SetFtuesEnabled(false);
            string seed = CommandLineHelper.GetValue("coach-seed") ?? "SPIRECOACH";
            var acts = ActModel.GetRandomList(new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection"), UnlockState.all, false).ToList();
            var model = ModelDb.AllCharacters.First(c => c.Id.Entry == character);
#if STS_STABLE
            var state = await NGame.Instance!.StartNewSingleplayerRun(model, false, acts, [], seed, GameMode.Standard, 10);
            var me = state.Players.Single();
#else
            var me = Player.CreateForNewRun(model, UnlockState.all, 1);
            var state = await NGame.Instance!.StartNewSingleplayerRun(me, false, acts, [], seed, GameMode.Standard, false, 10);
#endif
            GD.Print("[CoachCheck] run started");
            var run = RunManager.Instance;
            string Rankings()
            {
                var lines = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    var row = (PanelContainer)panel.FindChild($"Choice{i}", true, false)!;
                    if (!row.IsVisibleInTree()) continue;
                    string Field(string field) => ((Label)row.FindChild(field, true, false)!).Text;
                    lines.Add($"{Field("Rank")}.  {Field("ChoiceName")}    {Field("Probability")}");
                }
                return string.Join('\n', lines);
            }
            if (CommandLineHelper.HasArg("coach-search-check") || CommandLineHelper.HasArg("coach-rollout-check"))
            {
                var controls=hud.GetNode("CoachControls");
                controls.GetType().GetMethod("SetCombatSearch",BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controls,[true]);
                var speed=(Button)hud.FindChild("SearchBudget",true,false)!;
                for(int count=0;count<4 && !speed.Text.Contains("논문");count++) speed.EmitSignal(BaseButton.SignalName.Pressed);
                if(!speed.Text.Contains("논문")) throw new Exception("Missing paper budget");
                bool roll=character is "SILENT" or "REGENT" or "DEFECT";
                if(roll) {
                    await run.EnterAct(2,false);
                    // Enter through a real final map edge, as sts2-sim validates legal travel.
                    var beforeBoss=state.Map.GetAllMapPoints().First(pt=>pt.Children.Contains(state.Map.BossMapPoint));
                    state.AddVisitedMapCoord(beforeBoss.coord);
                }
                await run.EnterMapCoord(state.Map.BossMapPoint.coord);
                var label=(Label)panel.FindChild("SearchPick",true,false)!;
                var adapter=controls.GetType().Assembly;
                var read=adapter.GetType("SpireCoach.LiveBoard")!.GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Static)!;
                object? live=null;
                await Wait(()=>Rankings().Contains('%') && (live=read.Invoke(null,null))!=null
                    && live.GetType().GetProperty("Kind")!.GetValue(live)!.ToString()=="Combat", "boss recommendations");
                var raw=(byte[])live!.GetType().GetProperty("Frame")!.GetValue(live)!;
                var replay=(MegaCrit.Sts2.Core.Multiplayer.Replay.CombatReplay)run.CombatReplayWriter.GetType()
                    .GetField("_replay",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(run.CombatReplayWriter)!;
                var sw=new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter(); sw.Write(replay.serializableRun);
                var ew=new MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter(); ew.WriteList(replay.events);
                var anchor=adapter.GetType("SpireCoach.CombatAnchor")!.GetProperty("Current",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null);
                uint world=(uint)adapter.GetType("SpireCoach.CombatLookahead")!.GetMethod("DecisionSeed",BindingFlags.NonPublic|BindingFlags.Static)!
                    .Invoke(null,[replay.serializableRun.SerializableRng.Seed,(long)replay.nextActionId])!;
                File.WriteAllText(Path.Combine(OS.GetUserDataDir(),"paper-root.json"), System.Text.Json.JsonSerializer.Serialize(new {
                    Save=sw.Buffer[..sw.BytePosition],Events=ew.Buffer[..ew.BytePosition],Frame=raw,Character=character,
                    Seed=roll ? world+7919u : world, FirstActThreeBoss=roll, Anchor=anchor
                }));
                var name=roll ? "롤아웃" : "트리 탐색";
                await Wait(()=>label.IsVisibleInTree() && label.Text.StartsWith(name),"boss search "+character);
                GD.Print("[CoachCheck] Search PASS "+character+" "+label.Text);
                tree.Quit(); return;
            }
            if (CommandLineHelper.HasArg("coach-position-check"))
            {
                await run.EnterMapCoord(state.Map.StartingMapPoint.Children.OrderBy(c=>c.coord.col).First().coord);
                await Wait(()=>me.PlayerCombatState?.Hand.Cards.Count>0 && Rankings().Contains('%'),"combat policy");
                var hand=me.PlayerCombatState!.Hand;
                var combat=me.Creature.CombatState!;
                await CardPileCmd.Add(hand.Cards.ToList(),PileType.Discard);
                var wound=combat.CreateCard(ModelDb.Card<Cards.Wound>(),me);
                var normal=combat.CreateCard(ModelDb.Card<Cards.StrikeIronclad>(),me);
                var free=combat.CreateCard(ModelDb.Card<Cards.StrikeIronclad>(),me);
                free.EnergyCost.SetThisTurn(0);
                var retained=combat.CreateCard(ModelDb.Card<Cards.StrikeIronclad>(),me);
                retained.GiveSingleTurnRetain();
                await CardPileCmd.Add(new[]{wound,normal,free,retained},PileType.Hand);
                var toggle=(Button)hud.FindChild("CardNumbers",true,false)!;
                if(toggle.Text=="카드 번호 켜기") toggle.EmitSignal(BaseButton.SignalName.Pressed);
                async Task Verify(bool numbered)
                {
                    await tree.ToSignal(tree.CreateTimer(.65),SceneTreeTimer.SignalName.Timeout);
                    var next=(Button)panel.FindChild("NextPage",true,false)!;
                    string rows="";
                    for(int page=0;page<12;page++) {
                        rows+=Rankings()+"\n";
                        if(!next.IsVisibleInTree()) break;
                        next.EmitSignal(BaseButton.SignalName.Pressed);
                        if(next.Text.StartsWith("1/")) break;
                    }
                    var retainName=new MegaCrit.Sts2.Core.Localization.LocString("card_keywords","RETAIN.title").GetFormattedText();
                    foreach(var pair in new[]{(normal,"1코스트"),(free,"0코스트"),(retained,"1코스트, 이번 턴 "+retainName)}) {
                        int pos=MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance!.ActiveHolders.ToList().FindIndex(h=>h.CardNode?.Model==pair.Item1)+1;
                        if(pos<=1 || !rows.Contains((numbered ? $"손패 {pos}번 · " : "")+pair.Item1.Title+" ("+pair.Item2+")"))
                            throw new Exception("Incorrect card label: "+rows);
                    }
                    if(!numbered && rows.Contains("손패 ")) throw new Exception("Number toggle failed");
                    var cfg=new ConfigFile(); cfg.Load("user://spirecoach-controls.cfg");
                    if(cfg.GetValue("view","card_numbers",true).AsBool()!=numbered) throw new Exception("Number setting persistence");
                    GD.Print("[CoachCheck] Position "+numbered+" "+rows);
                }
                await Verify(true); toggle.EmitSignal(BaseButton.SignalName.Pressed); await Verify(false);
                toggle.EmitSignal(BaseButton.SignalName.Pressed); await Verify(true);
                var order=(Button)hud.FindChild("CombatOrder",true,false)!;
                order.EmitSignal(BaseButton.SignalName.Pressed); await Verify(true);
                order.EmitSignal(BaseButton.SignalName.Pressed);
                if(CommandLineHelper.HasArg("coach-deck-size")) {
                    var controls=hud.GetNode("CoachControls");
                    controls.GetType().GetMethod("OpenSettings",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(controls,[true]);
                    await tree.ToSignal(tree.CreateTimer(.3),SceneTreeTimer.SignalName.Timeout);
                    var point=toggle.GetGlobalTransformWithCanvas()*toggle.Size;
                    if(!toggle.IsVisibleInTree() || !toggle.GetViewport().GetVisibleRect().HasPoint(point))
                        throw new Exception("Number toggle outside the viewport");
                }
                GD.Print("[CoachCheck] Position PASS"); tree.Quit();return;
            }
            if (CommandLineHelper.HasArg("coach-enchant-check"))
            {
                await run.EnterMapCoord(state.Map.StartingMapPoint.Children.OrderBy(c => c.coord.col).First().coord);
                await Wait(() => Rankings().Contains('%') && me.PlayerCombatState?.Hand.Cards.Count > 0, "combat hand");
                var combat = me.Creature.CombatState!;
                await CardPileCmd.Add(me.PlayerCombatState!.Hand.Cards.ToList(), PileType.Discard);
                var strikes = Enumerable.Range(0, 3).Select(_ => combat.CreateCard(ModelDb.Card<Cards.StrikeIronclad>(), me)).ToArray();
                CardCmd.Upgrade(strikes[2]);
                CardCmd.Enchant<MegaCrit.Sts2.Core.Models.Enchantments.Sharp>(strikes[1], 3);
                CardCmd.Enchant<MegaCrit.Sts2.Core.Models.Enchantments.Sharp>(strikes[2], 5);
                await CardPileCmd.Add(strikes, PileType.Hand);
                var lang = MegaCrit.Sts2.Core.Localization.LocManager.Instance;
                string initialLanguage = lang.Language;
                async Task VerifyLabels(string phase)
                {
                    await tree.ToSignal(tree.CreateTimer(.7), SceneTreeTimer.SignalName.Timeout);
                    await Wait(() => panel.IsVisibleInTree() && Rankings().Contains('%'), phase);
                    string enchant = strikes[1].Enchantment!.Title.GetFormattedText();
                    string[] expected = [strikes[0].Title, $"{strikes[1].Title} [{enchant} 3]", $"{strikes[2].Title} [{enchant} 5]"];
                    var labels = new HashSet<string>();
                    var next = (Button)panel.FindChild("NextPage", true, false)!;
                    for (int page = 0; page < 10; page++)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            var row = (PanelContainer)panel.FindChild($"Choice{i}", true, false)!;
                            if (row.IsVisibleInTree())
                                labels.Add(((Label)row.FindChild("ChoiceName", true, false)!).Text.Split(" → ")[0]);
                        }
                        if (!next.IsVisibleInTree()) break;
                        next.EmitSignal(BaseButton.SignalName.Pressed);
                        if (next.Text.StartsWith("1/")) break;
                    }
                    if (!expected.All(labels.Contains)) throw new Exception(phase + ": " + string.Join(", ", labels));
                    GD.Print($"[CoachCheck] Enchantment {phase} {lang.Language}: {string.Join(", ", expected)}");
                }
                try
                {
                    foreach (string language in new[] { "kor", "eng", "jpn" })
                    {
                        lang.SetLanguage(language);
                        await VerifyLabels("combat");
                    }
                    var discard = combat.CreateCard(ModelDb.Card<Cards.Survivor>(), me);
                    await CardPileCmd.Add(discard, PileType.Hand);
                    if (!discard.TryManualPlay(null)) throw new Exception("Discard prompt could not open");
                    await Wait(() => NPlayerHand.Instance?.IsInCardSelection == true && ((Label)panel.FindChild("Title",true,false)!).Text == "카드 선택 추천", "discard prompt");
                    await VerifyLabels("discard");
                }
                finally { lang.SetLanguage(initialLanguage); }
                GD.Print("[CoachCheck] Enchantment PASS"); tree.Quit(); return;
            }
            if (CommandLineHelper.GetValue("coach-select-test") is { } selection)
            {
                bool Ranked(string title) => panel.Visible && ((Label)panel.FindChild("Title", true, false)!).Text == title && Rankings().Contains('%');
                var privateMethod = BindingFlags.NonPublic | BindingFlags.Instance;
                if (selection == "newgrid")
                {
                    await run.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<RoomFullOfCheese>());
                    await Wait(() => Ranked("이벤트 추천"), "cheese event");
                    var room = NEventRoom.Instance!;
                    var buttons = room.Layout!.OptionButtons.ToList();
                    int index = buttons.FindIndex(b => b.Option.TextKey.EndsWith(".GORGE") && b.IsEnabled);
                    room.OptionButtonClicked(buttons[index].Option, index);
                    await Wait(() => NOverlayStack.Instance?.Peek() is NSimpleCardSelectScreen && Ranked("카드 선택 추천"), "generated event grid");
                    var grid = ((Control)NOverlayStack.Instance!.Peek()!).GetNode<NCardGrid>("%CardGrid");
                    if (!grid.CurrentlyDisplayedCards.Any(c => !me.Deck.Cards.Contains(c))) throw new Exception("Expected a non-deck grid");
                    var names = await Snapshot("select-newgrid");
                    foreach (var card in grid.CurrentlyDisplayedCards)
                        if (!names.Contains(card.Title)) throw new Exception("Missing event option "+card.Title);
                }
                else
                {
                    await run.EnterRoomDebug(RoomType.Monster);
                    await Wait(() => Ranked("지금 할 행동") && me.PlayerCombatState?.Hand.Cards.Any() == true, "combat before choice");
                    var hand = me.PlayerCombatState!.Hand;
                    var combat = me.Creature.CombatState!;
                    if (selection == "exhaustpile")
                    {
                        await CardPileCmd.Add(hand.Cards.Take(3).ToList(), PileType.Exhaust);
                        var pile = PileType.Exhaust.GetPile(me);
                        var prefs = new CardSelectorPrefs(new MegaCrit.Sts2.Core.Localization.LocString("gameplay_ui", "CHOOSE_CARD_HEADER"), 1);
                        var screen = NCombatPileCardSelectScreen.Create(pile, prefs, null);
                        NOverlayStack.Instance!.Push(screen);
                        var waiting = screen.CardsSelected();
                        await Wait(() => Ranked("카드 선택 추천"), "exhaust pile recommendations");
                        if (!SameCards(await Snapshot("select-exhaustpile"), pile.Cards)) throw new Exception("Exhaust pile choices");
                        screen.GetType().GetMethod("OnCardClicked", privateMethod)!.Invoke(screen,[pile.Cards[0]]);
                        await waiting;
                        await Wait(() => Ranked("지금 할 행동"), "return from exhaust pile");
                        GD.Print("[CoachCheck] Selection PASS exhaustpile"); tree.Quit(); return;
                    }
                    CardModel template = selection switch {
                        "discard" => ModelDb.Card<Cards.Survivor>(), "upgrade" => ModelDb.Card<Cards.Armaments>(),
                        "exhaust" => ModelDb.Card<Cards.TrueGrit>(), "pile" => ModelDb.Card<Cards.Hologram>(),
                        "generate" => ModelDb.Card<Cards.Discovery>(), "multi" => ModelDb.Card<Cards.Prepared>(),
                        "drawpile" => ModelDb.Card<Cards.SecretTechnique>(),
                        _ => throw new ArgumentException(selection)
                    };
                    if (selection == "pile") await CardPileCmd.Add(hand.Cards.Take(3).ToList(), PileType.Discard);
                    if (selection == "drawpile")
                        foreach (var templateCard in new CardModel[] { ModelDb.Card<Cards.Armaments>(), ModelDb.Card<Cards.Survivor>(), ModelDb.Card<Cards.TrueGrit>() })
                            await CardPileCmd.Add(combat.CreateCard(templateCard, me), PileType.Draw);
                    if (selection == "upgrade") CardCmd.Upgrade(hand.Cards.First(c => c.IsUpgradable));
                    var action = combat.CreateCard(template, me);
                    if (selection is "multi" or "exhaust") CardCmd.Upgrade(action);
                    await CardPileCmd.Add(action, PileType.Hand);
                    if (!action.TryManualPlay(null)) throw new Exception("Could not play " + action.Title);
                    await Wait(() => Ranked("카드 선택 추천"), selection + " card rankings");
                    var names = await Snapshot("select-" + selection);
                    if (selection is "pile" or "drawpile")
                    {
                        var screen = (NCombatPileCardSelectScreen)NOverlayStack.Instance!.Peek()!;
                        var pile = screen.GetNode<NCardGrid>("%CardGrid").CurrentlyDisplayedCards.ToArray();
                        if (!SameCards(names, pile)) throw new Exception("Pile choices");
                        if (selection == "drawpile" && pile.Any(c => c.Type != CardType.Skill)) throw new Exception("Draw pile filter");
                        screen.GetType().GetMethod("OnCardClicked", privateMethod)!.Invoke(screen,[pile[0]]);
                    }
                    else if (selection == "generate")
                    {
                        var screen = (NChooseACardSelectionScreen)NOverlayStack.Instance!.Peek()!;
                        var holders = screen.GetNode<Control>("CardRow").GetChildren().OfType<NCardHolder>().ToArray();
                        var skip = screen.GetNode<NChoiceSelectionSkipButton>("SkipButton");
                        if (!SameCards(names.Where(n => n != "건너뛰기").ToArray(), holders.Select(h => h.CardModel!))
                            || names.Contains("건너뛰기") != (skip.IsVisibleInTree() && skip.IsEnabled)) throw new Exception("Generated choices");
                        await tree.ToSignal(tree.CreateTimer(.4), SceneTreeTimer.SignalName.Timeout);
                        screen.GetType().GetMethod("SelectHolder", privateMethod)!.Invoke(screen,[holders[0]]);
                    }
                    else
                    {
                        var screen = NPlayerHand.Instance!;
                        var legal = hand.Cards.Where(c => selection != "upgrade" || c.IsUpgradable).ToArray();
                        if (!SameCards(names, legal)) throw new Exception("Hand choices: "+string.Join(", ",names));
                        void Pick() => screen.GetType().GetMethod("OnHolderPressed", privateMethod)!.Invoke(screen,[screen.ActiveHolders[0]]);
                        var picked = screen.ActiveHolders[0].CardModel!;
                        Pick();
                        if (selection == "multi")
                        {
                            await tree.ToSignal(tree.CreateTimer(.7), SceneTreeTimer.SignalName.Timeout);
                            var remaining = await Snapshot("select-multi-next");
                            if (!SameCards(remaining, legal.Where(c => c != picked).ToArray())) throw new Exception("Already-selected discard offered");
                            Pick();
                        }
                        if (screen.IsInCardSelection && screen.GetType().GetField("_selectModeConfirmButton",privateMethod)!.GetValue(screen) is NConfirmButton {IsEnabled:true} confirm)
                            screen.GetType().GetMethod("OnSelectModeConfirmButtonPressed",privateMethod)!.Invoke(screen,[confirm]);
                    }
                    await Wait(() => Ranked("지금 할 행동") && !run.ActionExecutor.IsRunning, "combat after selection");
                }
                GD.Print("[CoachCheck] Selection PASS "+selection); tree.Quit(); return;
            }
            if (CommandLineHelper.HasArg("coach-potion-check"))
            {
                await run.EnterRoomDebug(RoomType.RestSite);
                PotionModel Potion(string id) => ModelDb.AllPotions.Single(p => p.Id.Entry == id).ToMutable();
                foreach (var potion in me.Potions.ToArray()) await PotionCmd.Discard(potion);
                var stock = new[] { "BLOCK_POTION", "ENERGY_POTION", "STRENGTH_POTION" };
                for (int i = 0; i < me.PotionSlots.Count; i++)
                    await PotionCmd.TryToProcure(Potion(stock[i % stock.Length]), me);
                var offer = new PotionReward(Potion("FIRE_POTION"), me);
                var gold = new GoldReward(9, me);
                _ = new RewardsSet(me).WithCustomRewards([gold, offer]).Offer();
                bool Ready() => ((Label)panel.FindChild("Title", true, false)!).Text == "포션 보상 추천" && Rankings().Contains('%');
                await Wait(() => Ready() && !me.HasOpenPotionSlots, "full potion recommendations");
                var screen = (NRewardsScreen)NOverlayStack.Instance!.Peek()!;
                async Task Claim(Reward reward)
                {
                    var button = screen.FindChildren("*", "", true, false).OfType<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>()
                        .Single(b => b.Reward == reward && b.IsVisibleInTree());
                    await (Task)button.GetType().GetMethod("GetReward", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null)!;
                }
                await Claim(offer);
                await Wait(Ready, "failed claim leaves potion visible");
                var full = await Snapshot("potion-full");
                if (full.Count(n => n.StartsWith("버리기 ·")) != me.Potions.Count() || full.Any(n => n.StartsWith("받기 ·"))
                    || !full.Contains("골드 9 받기") || !full.Contains("남은 보상 두고 가기")) throw new Exception("Full potion candidate mask");
                await Claim(gold);
                await Wait(() => Ready() && Rankings().Contains("새 포션 두고 가기"), "only potion remains");
                await Snapshot("potion-only");
                await PotionCmd.Discard(me.Potions.First());
                await Wait(() => Ready() && Rankings().Contains("받기 ·"), "open potion slot");
                var open = await Snapshot("potion-open");
                if (open.Any(n => n.StartsWith("버리기 ·")) || !open.Contains("받기 · " + offer.Potion!.Title.GetFormattedText()))
                    throw new Exception("Open potion candidate mask");
                await Claim(offer);
                await Wait(() => offer.SuccessfullySelected && !Ready(), "claimed potion leaves recommendations");
                GD.Print("[CoachCheck] Potion rewards PASS"); tree.Quit(); return;
            }
            if (CommandLineHelper.HasArg("coach-copy-check"))
            {
                await RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.Vajra>(me);
                await RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.OrnamentalFan>(me);
                await RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.Gorget>(me);
                await run.EnterRoomDebug(RoomType.Monster, model: ModelDb.Encounter<MegaCrit.Sts2.Core.Models.Encounters.RubyRaidersNormal>().ToMutable());
                await Wait(() => Rankings().Contains('%') && me.PlayerCombatState?.Hand.Cards.Any() == true, "combat hand ready");
                var combat = me.Creature.CombatState!;
                var hand = me.PlayerCombatState!.Hand;
                await CardPileCmd.Add(hand.Cards.ToList(), PileType.Discard);
                var setupStrike = combat.CreateCard(ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.SetupStrike>(), me);
                var wave = combat.CreateCard(ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.IronWave>(), me);
                CardCmd.Upgrade(wave);
                await CardPileCmd.Add(new[] { setupStrike, wave }, PileType.Hand);
                for (int i = 0; i < 4; i++)
                    await CardPileCmd.Add(combat.CreateCard(ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.StrikeIronclad>(), me), PileType.Hand);
                var nextPage = (Button)panel.FindChild("NextPage", true, false)!;
                await tree.ToSignal(tree.CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
                await Wait(() => nextPage.IsVisibleInTree() && Rankings().Contains('%'), "ranked card copies");
                if (((Label)panel.FindChild("Title", true, false)!).Text != "지금 할 행동") throw new Exception("Combat heading");
                int page = 0;
                do
                {
                    await Snapshot(page == 0 ? "copies" : $"copies-page-{page + 1}", page);
                    page++;
                    nextPage.EmitSignal(BaseButton.SignalName.Pressed);
                } while (!nextPage.Text.StartsWith("1/") && page < 8);
                if (page < 2 || page >= 8) throw new Exception("Combat pagination");
                var settings = hud.GetNode<PanelContainer>("SettingsPanel");
                void ToggleOrder()
                {
                    ((Button)panel.FindChild("Settings", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
                    ((Button)settings.FindChild("CombatOrder", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
                    ((Button)settings.FindChild("Close", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
                }
                ToggleOrder();
                await Wait(() => ((Label)panel.FindChild("Legend", true, false)!).Text == "카드 1장 기준" && Rankings().Contains('%'), "individual card ranking");
                await Snapshot("copies-individual");
                ToggleOrder();
                await Wait(() => !((Label)panel.FindChild("Legend", true, false)!).IsVisibleInTree() && Rankings().Contains('%'), "combined ranking without redundant caption");
                var strike = hand.Cards.First(c => c.Id.Entry == "STRIKE_IRONCLAD");
                if (!strike.TryManualPlay(combat.Enemies.First(e => e.IsAlive))) throw new Exception("Cannot play Strike");
                await Wait(() => !hand.Cards.Contains(strike) && !run.ActionExecutor.IsRunning, "Strike resolved");
                await tree.ToSignal(tree.CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
                await Wait(() => Rankings().Contains('%') && nextPage.Text.StartsWith("1/"), "updated hand rankings");
                await Snapshot("copies-after-play");
                GD.Print($"[CoachCheck] Card copies PASS {character}, {page} pages");
                tree.Quit(); return;
            }
            if (CommandLineHelper.HasArg("coach-camp-check"))
            {
                bool relics = CommandLineHelper.HasArg("coach-camp-relics");
                if (relics)
                {
                    await RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.Shovel>(me);
                    await RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.MiniatureTent>(me);
                    await RelicCmd.Obtain<MegaCrit.Sts2.Core.Models.Relics.DreamCatcher>(me);
                }
                me.Creature.SetCurrentHpInternal(me.Creature.MaxHp);
                await run.EnterRoomDebug(RoomType.RestSite);
                var room = NRestSiteRoom.Instance!;
                var heal = room.Options.OfType<HealRestSiteOption>().Single();
                var smith = room.Options.OfType<SmithRestSiteOption>().Single();
                string LabelOf(RestSiteOption o) => o.Title.GetFormattedText();
                bool Ready(string heading) => panel.Visible && ((Label)panel.FindChild("Title", true, false)!).Text == heading && Rankings().Contains('%');
                void Choose(RestSiteOption o)
                {
                    var b = room.GetButtonForOption(o)!;
                    b.GetType().GetMethod("OnRelease", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(b, null);
                }
                await Wait(() => Ready("모닥불 추천"), "camp suggestions at full HP");
                var full = await Snapshot(relics ? "camp-extra" : "camp-full");
                if (full.Contains(LabelOf(heal)) != relics || !full.Contains(LabelOf(smith))) throw new Exception("Full HP rest mask");
                int injured = me.Creature.MaxHp / 3;
                me.Creature.SetCurrentHpInternal(injured);
                // The names can stay the same with Dream Catcher, so wait for the new inference.
                await tree.ToSignal(tree.CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
                await Wait(() => Ready("모닥불 추천") && Rankings().Contains(LabelOf(heal)), "injured camp suggestions");
                var choices = await Snapshot("camp-injured");
                if (!choices.Order().SequenceEqual(room.Options.Where(o => o.IsEnabled).Select(LabelOf).Order())) throw new Exception("Camp choices differ from game");
                Choose(smith);
                await Wait(() => Ready("카드 선택 추천") && NOverlayStack.Instance?.Peek() is NDeckUpgradeSelectScreen, "camp upgrade card probabilities");
                var cards = await Snapshot("camp-cards");
                if (!cards.Order().SequenceEqual(me.Deck.Cards.Where(c => c.IsUpgradable).Select(c => c.Title).Order()))
                    throw new Exception("Camp smith candidates differ from deck");
                var deckScreen = (NDeckUpgradeSelectScreen)NOverlayStack.Instance!.Peek()!;
                var back = deckScreen.GetNode<NBackButton>("%Close");
                back.EmitSignal("Released", back);
                await Wait(() => Ready("모닥불 추천"), "camp suggestions restored after cancelling smith");
                if (relics)
                {
                    Choose(smith);
                    await Wait(() => Ready("카드 선택 추천"), "camp smith reopened");
                    deckScreen = (NDeckUpgradeSelectScreen)NOverlayStack.Instance!.Peek()!;
                    var grid = deckScreen.GetNode<NCardGrid>("%CardGrid");
                    var holder = grid.CurrentlyDisplayedCardHolders.First();
                    grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
                    var confirm = deckScreen.FindChildren("*", "", true, false).OfType<NConfirmButton>().Single(b => b.IsEnabled && b.IsVisibleInTree());
                    confirm.EmitSignal("Released", confirm);
                    await Wait(() => Ready("모닥불 추천") && Rankings().Contains("떠나기"), "remaining camp options after smith");
                    var remaining = await Snapshot("camp-again");
                    if (remaining.Contains(LabelOf(smith)) || !remaining.Contains(LabelOf(heal)) || !remaining.Contains("떠나기"))
                        throw new Exception("Stale multiple-action camp options");
                }
                else
                {
                    Choose(heal);
                    await Wait(() => room.ProceedButton.IsEnabled && me.Creature.CurrentHp > injured && !panel.Visible, "finished camp closes recommendations");
                }
                GD.Print($"[CoachCheck] Camp PASS {character}, relics={relics}");
                tree.Quit(); return;
            }
            if (CommandLineHelper.GetValue("coach-ancient") is { } ancientName)
            {
                EventModel ancient = ancientName switch {
                    "Darv" => ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.Darv>(),
                    "Pael" => ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.Pael>(),
                    _ => ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.Neow>() };
                await run.EnterRoomDebug(RoomType.Event, model: ancient);
                await Wait(() => NEventRoom.Instance?.Layout?.OptionButtons.Any() == true, "ancient choices created");
                var layout = NEventRoom.Instance!.Layout!;
                var nextLine = layout.GetNode<Control>("%DialogueHitbox");
                for (int n = 0; n < 80 && nextLine.IsVisibleInTree(); n++)
                {
                    if (((Label)panel.FindChild("Title",true,false)!).Text == "이벤트 추천" && panel.Visible)
                        throw new Exception("Revealed ancient choices before dialogue ended");
                    nextLine.EmitSignal("Released", nextLine);
                    await tree.ToSignal(tree.CreateTimer(.2), SceneTreeTimer.SignalName.Timeout);
                }
                await Wait(() => panel.Visible && ((Label)panel.FindChild("Title",true,false)!).Text == "이벤트 추천" && Rankings().Contains('%'), "ancient recommendations");
                var offered = layout.OptionButtons.Where(b => !b.Option.IsLocked && b.Option.Relic != null)
                    .Select(b => b.Option.Relic!.Title.GetFormattedText()).ToArray();
                var names = await Snapshot("ancient");
                if (offered.Length < 2 || offered.Any(label => !names.Contains(label))) throw new Exception("Ancient relic labels");
                GD.Print($"[CoachCheck] Ancient PASS {ancientName}, {offered.Length} rewards");
                tree.Quit(); return;
            }
            if (CommandLineHelper.GetValue("coach-deck-event") is { } effect)
            {
                (EventModel ev, string option, Type screenType) = effect switch
                {
                    "upgrade" => ((EventModel)ModelDb.Event<SapphireSeed>(), ".EAT", typeof(NDeckUpgradeSelectScreen)),
                    "enchant" => (ModelDb.Event<SapphireSeed>(), ".PLANT", typeof(NDeckEnchantSelectScreen)),
                    "remove" => (ModelDb.Event<ZenWeaver>(), ".EMOTIONAL_AWARENESS", typeof(NDeckCardSelectScreen)),
                    "transform" => (ModelDb.Event<MorphicGrove>(), ".GROUP", typeof(NDeckTransformSelectScreen)),
                    _ => throw new ArgumentException(effect)
                };
                await PlayerCmd.SetGold(500, me);
                await run.EnterRoomDebug(RoomType.Event, model: ev);
                string Heading() => ((Label)panel.FindChild("Title", true, false)!).Text;
                bool CardRanks() => panel.Visible && Heading() == "카드 선택 추천" && Rankings().Contains('%');
                await Wait(() => Heading() == "이벤트 추천" && Rankings().Contains('%'), "first event decision");
                var room = NEventRoom.Instance!;
                var buttons = room.Layout!.OptionButtons.ToList();
                int index = buttons.FindIndex(b => b.Option.TextKey.EndsWith(option) && b.IsEnabled);
                room.OptionButtonClicked(buttons[index].Option, index);
                await Wait(() => NOverlayStack.Instance?.Peek()?.GetType() == screenType && CardRanks(), effect + " card probabilities");
                var screen = (NCardGridSelectionScreen)NOverlayStack.Instance!.Peek()!;
                var grid = screen.GetNode<NCardGrid>("%CardGrid");
                var cards = grid.CurrentlyDisplayedCardHolders.Select(h => h.CardModel).ToList();
                var names = await Snapshot("deck-" + effect);
                if (cards.Count < 2 || cards.Except(me.Deck.Cards).Any() || !SameCards(names, cards))
                    throw new Exception("Recommendations do not match the offered deck cards: " + effect);
                var firstCard = cards.GroupBy(c => c.Title).OrderBy(group => group.Count()).First().First();
                void Click(CardModel card) => grid.EmitSignal(NCardGrid.SignalName.HolderPressed, grid.GetCardHolder(card)!);
                Click(firstCard);
                if (effect == "transform")
                {
                    var remaining = cards.Where(c => c != firstCard).ToList();
                    var coach = hud.GetNode("CoachControls").GetType().Assembly.GetType("SpireCoach.Coach")!;
                    var current = coach.GetField("current", BindingFlags.Static | BindingFlags.NonPublic)!;
                    await Wait(() => CardRanks() && current.GetValue(null) is { } capture
                        && SameCards((string[])capture.GetType().GetProperty("Names")!.GetValue(capture)!, remaining),
                        "updated second card probabilities");
                    if (!SameCards(await Snapshot("deck-transform-next"), remaining)) throw new Exception("Selected card still offered");
                    Click(remaining.First());
                }
                await Wait(() => !panel.Visible, "card recommendations close during preview");
                // Going back must restore a fresh recommendation for every card.
                var preview = screen.FindChildren("*", "", true, false).OfType<NBackButton>().Single(b => b.IsEnabled && b.IsVisibleInTree());
                preview.EmitSignal("Released", preview);
                await Wait(CardRanks, "rankings restored after returning to grid");
                if (!SameCards(await Snapshot("deck-" + effect + "-back"), cards)) throw new Exception("Stale selection after returning");
                Click(firstCard);
                if (effect == "transform") Click(cards.First(c => c != firstCard));
                var confirm = screen.FindChildren("*", "", true, false).OfType<NConfirmButton>().Single(b => b.IsEnabled && b.IsVisibleInTree());
                confirm.EmitSignal("Released", confirm);
                await Wait(() => NOverlayStack.Instance?.Peek() != screen && !panel.Visible, "confirmed event selection closed");
                GD.Print($"[CoachCheck] Deck event PASS {effect}, {cards.Count} candidates");
                tree.Quit(); return;

            }
            if (CommandLineHelper.HasArg("coach-event-check"))
            {
                await run.EnterRoomDebug(RoomType.Event, model: ModelDb.Event<MegaCrit.Sts2.Core.Models.Events.Trial>());
                bool EventReady() => ((Label)panel.FindChild("Title", true, false)!).Text == "이벤트 추천" && Rankings().Contains('%');
                await Wait(EventReady, "event suggestions");
                var initial = await Snapshot("event");
                if (initial.Length < 2 || initial.Distinct().Count() != initial.Length) throw new Exception("Missing event choices");
                var room = NEventRoom.Instance!;
                var accept = room.Layout!.OptionButtons.Single(b => b.Option.TextKey.EndsWith(".ACCEPT"));
                room.OptionButtonClicked(accept.Option, 0);
                await Wait(() => EventReady() && !initial.Any(n => Rankings().Contains(n)), "updated event options");
                var nextPage = await Snapshot("event-next");
                if (nextPage.Intersect(initial).Any()) throw new Exception("Stale event options");
                var ruling = room.Layout.OptionButtons.First();
                room.OptionButtonClicked(ruling.Option, 0);
                await Wait(() => !panel.Visible, "finished event closes recommendations");
                GD.Print("[CoachCheck] Event PASS");
                tree.Quit();
                return;
            }
            if (CommandLineHelper.HasArg("coach-route-check"))
            {
                var fork = state.Map.GetAllMapPoints().First(p => p.coord.row > 0 && p.Children.Count >= 2);
                await run.EnterMapCoordDebug(fork.coord, RoomType.Shop);
                var shop = NMerchantRoom.Instance!;
                shop.OpenInventory();
                var inventory = shop.Inventory.Inventory!;
                await PlayerCmd.SetGold(1000, me);
                bool Ready(string screen) => ((Label)panel.FindChild("Title", true, false)!).Text == screen + " 추천" && Rankings().Contains('%');
                await Wait(() => Ready("상점"), "shop recommendations");
                var offers = await Snapshot("shop");
                var hudBounds = panel.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, panel.Size);
                foreach (var slot in shop.Inventory.GetAllSlots().Where(s => s.IsVisibleInTree() && s.Entry?.IsStocked == true))
                {
                    var hitbox = slot.Hitbox;
                    var bounds = hitbox.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, hitbox.Size);
                    if (hudBounds.Intersects(bounds)) throw new Exception($"HUD covers merchandise: {hudBounds} / {bounds}");
                }
                var card = inventory.AllEntries.OfType<MerchantCardEntry>().First(e => e.IsStocked && e.Cost > 0);
                string purchase = $"{card.CreationResult!.Card.Title} · {card.Cost} G";
                if (!offers.Contains(purchase) || !offers.Contains("상점 나가기") || !offers.Any(n => n.StartsWith("카드 제거 · ")))
                    throw new Exception("Missing shop choices");
                await PlayerCmd.SetGold(card.Cost, me);
#if STS_STABLE
                bool purchased = await card.OnTryPurchaseWrapper(inventory);
#else
                bool purchased = await card.OnTryPurchaseUnsynchronized(inventory);
#endif
                if (!purchased || me.Gold != 0) throw new Exception("QA purchase failed");
                await Wait(() => Ready("상점") && Rankings().Contains("상점 나가기") && !Rankings().Contains(" G"), "affordability refresh");
                var after = await Snapshot("after-buy");
                if (after.Any(n => n.EndsWith(" G"))) throw new Exception("Unaffordable purchase recommended");
                shop.Inventory.Call("Close");
                await Wait(() => !panel.Visible, "shop panel closes");
                var map = NMapScreen.Instance!;
                map.SetTravelEnabled(false); map.Open();
                await tree.ToSignal(tree.CreateTimer(.6), SceneTreeTimer.SignalName.Timeout);
                if (panel.Visible) throw new Exception("Read-only map shows recommendations");
                map.SetTravelEnabled(true);
                await Wait(() => Ready("지도"), "map recommendations");
                var routes = await Snapshot("map");
                int destinations = MapTravel.GetTravelablePointsFrom(state, fork).Count();
                if (destinations < 2 || routes.Count(n => !n.StartsWith("사용 · ")) != destinations ||
                    routes.Distinct().Count() != routes.Length) throw new Exception("Wrong map destinations");
                map.Close(false); await Wait(() => !panel.Visible, "map panel closes");
                GD.Print("[CoachCheck] Map and shop PASS");
                tree.Quit();
                return;
            }
            await run.EnterRoomDebug(RoomType.Monster);
            await Wait(() => Rankings().Contains('%'), "combat rankings");
            if (CommandLineHelper.HasArg("coach-input-check"))
            {
                void KeyPress(Key key)
                {
                    foreach (bool down in new[] { true, false })
                        tree.Root.PushInput(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = down });
                }
                void PadPress(JoyButton button)
                {
                    foreach (bool down in new[] { true, false })
                        tree.Root.PushInput(new InputEventJoypadButton { ButtonIndex = button, Pressed = down });
                }
                var settings = hud.GetNode<PanelContainer>("SettingsPanel");
                void Click(string name) => ((Button)settings.FindChild(name, true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
                void Tap(Button button)
                {
                    var position = button.GetGlobalTransformWithCanvas() * (button.Size / 2);
                    foreach (bool down in new[] { true, false })
                        tree.Root.PushInput(new InputEventScreenTouch { Index = 0, Position = position, Pressed = down }, true);
                }
                bool mouseEmulation = Input.EmulateMouseFromTouch;
                try
                {
                    Input.EmulateMouseFromTouch = false;
                    Tap((Button)panel.FindChild("Settings", true, false)!);
                    await Wait(() => settings.Visible, "settings via touch");
                    await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                    Tap((Button)settings.FindChild("Close", true, false)!);
                    await Wait(() => !settings.Visible, "close via touch");
                }
                finally { Input.EmulateMouseFromTouch = mouseEmulation; }
                KeyPress(Key.F10); await Wait(() => settings.Visible, "settings via key");
                Click("Defaults"); Click("PanelKey"); KeyPress(Key.F11);
                Click("PanelPad"); PadPress(JoyButton.Paddle1);
                Click("DetailsKey"); KeyPress(Key.F5);
                if (((Button)settings.FindChild("PanelKey", true, false)!).Text != "F11") throw new Exception("Binding capture");
                Click("Close"); await Wait(() => Rankings().Contains('%'), "close settings");
                KeyPress(Key.F11); await Wait(() => !panel.Visible, "rebound hide");
                KeyPress(Key.F8); if (panel.Visible) throw new Exception("Old binding active");
                PadPress(JoyButton.Paddle1); await Wait(() => Rankings().Contains('%'), "rebound pad show");
                var note = (Label)panel.FindChild("Preview", true, false)!;
                KeyPress(Key.F5); await Wait(() => !note.Visible && panel.Visible, "hide only details");
                KeyPress(Key.F5); await Wait(() => note.Visible, "show details");
                var cfg = new ConfigFile();
                if (cfg.Load("user://spirecoach-controls.cfg") != Error.Ok ||
                    cfg.GetValue("Panel", "key").AsInt64() != (long)Key.F11 ||
                    cfg.GetValue("Panel", "pad").AsInt64() != (long)JoyButton.Paddle1) throw new Exception("Binding save");
                KeyPress(Key.F10); Click("Defaults"); Click("Close");
                await Wait(() => Rankings().Contains('%'), "restored rankings");
                GD.Print("[CoachCheck] Input PASS");
            }
            static bool SameCards(string[] labels, IEnumerable<CardModel> options) =>
                labels.Order().SequenceEqual(options.Select(c => c.Title).Order());

            async Task<string[]> Snapshot(string name, int page = 0)
            {
                bool deckSelection = name.StartsWith("select-") || name is "deck-transform" or "deck-upgrade" or "deck-remove" or "deck-enchant";
                if (CommandLineHelper.HasArg("coach-stage-check") && (deckSelection || name is "combat" or "reward" or "map" or "shop" or "event" or "ancient" or "camp-injured" or "camp-cards" or "potion-full"))
                {
                    string stage = name switch { _ when deckSelection => "CardReward", "combat" => "Combat", "reward" or "camp-cards" => "CardReward", "camp-injured" => "RestSite", "potion-full" => "Rewards", "map" => "Map", "shop" => "Shop", _ => "Event" };
                    var settings = hud.GetNode<PanelContainer>("SettingsPanel");
                    void Press(Node root, string id) => ((Button)root.FindChild(id, true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
                    Press(panel, "Settings");
                    var checks = new[]{"Combat","Map","Event","CardReward","Shop","RestSite","Rewards"}.ToDictionary(id=>id,id=>(CheckBox)settings.FindChild("Stage"+id,true,false)!);
                    foreach (var check in checks.Values) check.ButtonPressed = false;
                    if (deckSelection) checks[name.StartsWith("select-") ? "Combat" : "Event"].ButtonPressed = true;
                    if (name == "camp-cards") checks["RestSite"].ButtonPressed = true;
                    Press(settings, "Close");
                    await tree.ToSignal(tree.CreateTimer(.8), SceneTreeTimer.SignalName.Timeout);
                    if (panel.Visible) throw new Exception("Disabled stage visible: "+stage);
                    var config = new ConfigFile();
                    if (config.Load("user://spirecoach-controls.cfg") != Error.Ok || config.GetValue("stages", stage, true).AsBool())
                        throw new Exception("Stage filter not persisted");
                    Press(hud,"CoachSettings");
                    foreach(var item in checks) item.Value.ButtonPressed = item.Key == stage;
                    var liveControls = hud.GetNode("CoachControls");
                    using (var reloaded = (Node)Activator.CreateInstance(liveControls.GetType())!)
                    {
                        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        var type = reloaded.GetType();
                        type.GetMethod("LoadSettings", flags)!.Invoke(reloaded, null);
                        var allows = type.GetMethod("Allows", flags)!;
                        foreach (string id in checks.Keys)
                        {
                            var kind = Enum.Parse(allows.GetParameters()[0].ParameterType, id);
                            if ((bool)allows.Invoke(reloaded, [kind])! != (id == stage)) throw new Exception("Reload failed for " + id);
                        }
                        reloaded.Free();
                    }
                    if (name == "ancient" && DisplayServer.GetName() != "headless")
                    {
                        await tree.ToSignal(tree.CreateTimer(.5), SceneTreeTimer.SignalName.Timeout);
                        using var image = tree.Root.GetTexture().GetImage();
                        image.SavePng(Path.Combine(OS.GetUserDataDir(), "stage-settings.png"));
                    }
                    Press(settings,"Close");
                    await Wait(()=>panel.Visible && Rankings().Contains('%'),"only "+stage+" enabled");
                    Press(panel,"Settings");
                    foreach(var check in checks.Values) check.ButtonPressed=true;
                    Press(settings,"Close");
                    await Wait(()=>panel.Visible && Rankings().Contains('%'),"all stages enabled");
                    GD.Print("[CoachCheck] Stage PASS "+stage);
                }
                if (DisplayServer.GetName() != "headless")
                    await tree.ToSignal(tree.CreateTimer(name is "map" or "shop" ? 6 : 1.5), SceneTreeTimer.SignalName.Timeout);
                var board = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("SpireCoach.LiveBoard")).Single(t => t != null);
                var capture = board!.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,null)!;
                var bytes = (byte[])capture.GetType().GetProperty("Frame")!.GetValue(capture)!;
                if ((string?)capture.GetType().GetProperty("Character")!.GetValue(capture) != character)
                    throw new InvalidDataException("Wrong character snapshot");
                var inventory = NRun.Instance!.GlobalUi.RelicInventory;
                var panelTop = panel.GetGlobalTransformWithCanvas().Origin.Y;
                var right = panel.GetGlobalRect().End.X;
                var width = panel.GetViewportRect().Size.X;
                if (right > width || panel.Position.X < 0 || (name is not ("event" or "event-next" or "ancient") && panel.Position.X < width * .5f))
                    throw new Exception($"Panel placement {right} / {width}");
                foreach (var relic in inventory.RelicNodes.Where(r => r.IsVisibleInTree()))
                {
                    var bottom = (relic.GetGlobalTransformWithCanvas() * relic.Size).Y;
                    if (panelTop <= bottom) throw new InvalidOperationException($"HUD covers a relic: {panelTop} <= {bottom}");
                    GD.Print($"[CoachCheck] relic gap {panelTop - bottom:F0}");
                }
                File.WriteAllBytes(Path.Combine(OS.GetUserDataDir(), name + ".bin"), bytes);
                var names = (string[])capture.GetType().GetProperty("Names")!.GetValue(capture)!;
                var copyTotals = Enumerable.Range(0, 4)
                    .Select(i => (PanelContainer)panel.FindChild($"Choice{i}", true, false)!)
                    .Where(row => row.IsVisibleInTree())
                    .Select(row => (Label)row.FindChild("CopyTotal", true, false)!)
                    .Select(label => label.IsVisibleInTree() ? label.Text : "").ToArray();
                File.WriteAllText(Path.Combine(OS.GetUserDataDir(), name + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(new { character, names, page, copyTotals,
                        displayNames=Enumerable.Range(0,names.Length).Select(i=>capture.GetType().GetMethod("NameAt",BindingFlags.Instance|BindingFlags.NonPublic)!
                            .Invoke(capture,[i,((Button)hud.FindChild("CardNumbers",true,false)!).Text=="카드 번호 끄기"])).ToArray(),
                        combined = capture.GetType().GetProperty("Kind")!.GetValue(capture)!.ToString() == "Combat"
                            && !((Label)panel.FindChild("Legend", true, false)!).IsVisibleInTree(),
                        upgrade = (bool)capture.GetType().GetProperty("IsUpgrade")!.GetValue(capture)!, rankings = Rankings() }));
                GD.Print($"[CoachCheck] {name}: {Rankings().Replace("\n"," | ")}");
                if (DisplayServer.GetName() != "headless")
                {
                    using var frame = tree.Root.GetTexture().GetImage();
                    frame.SavePng(Path.Combine(OS.GetUserDataDir(), name + ".png"));
                    if (name == "combat" && CommandLineHelper.HasArg("coach-preview"))
                    {
                        var bounds = tree.Root.GetFinalTransform() * panel.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, panel.Size);
                        int side = Math.Clamp((int)(bounds.Size.X * 1.28f), 1, Math.Min(frame.GetWidth(), frame.GetHeight()));
                        int top = Math.Clamp((int)(bounds.Position.Y - side * .13f), 0, frame.GetHeight() - side);
                        using var cover = frame.GetRegion(new Rect2I(frame.GetWidth() - side, top, side, side));
                        cover.SavePng(Path.Combine(OS.GetUserDataDir(), "workshop-preview.png"));
                    }
                }
                return names;
            }
            if (CommandLineHelper.HasArg("coach-114-check"))
            {
                var combat = me.Creature.CombatState!;
                await CardPileCmd.Add(me.PlayerCombatState!.Hand.Cards.ToList(), PileType.Discard);
                foreach (var id in new[] { "OPTIMISM", "SADNESS", "SADNESS", "TWIN_SHOT" })
                {
                    var card = combat.CreateCard(ModelDb.GetById<CardModel>(new("CARD", id)), me);
                    await CardPileCmd.Add(card, PileType.Hand);
                }
                var optimism = me.PlayerCombatState.Hand.Cards.First(c => c.Id.Entry == "OPTIMISM");
                await Wait(() => Rankings().Contains(optimism.Title), "v0.114 Fool recommendations");
                await Snapshot("fool-114");
                var settings = hud.GetNode<PanelContainer>("SettingsPanel");
                ((Button)panel.FindChild("Settings", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
                if (!((Label)settings.FindChild("Title", true, false)!).Text.Contains("v0.114.0") ||
                    !((Label)settings.FindChild("Hint", true, false)!).Text.Contains("fool_ema_v24181.pt"))
                    throw new Exception("Missing active v0.114 model in settings");
                GD.Print("[CoachCheck] Fool v0.114 PASS"); tree.Quit(); return;
            }
            await Snapshot("combat");
            var first = Rankings();
            var defend = me.PlayerCombatState!.Hand.Cards.FirstOrDefault(c => c.Id.Entry.StartsWith("DEFEND") && c.CanPlay(out _,out _) && c.CanPlayTargeting(null));
            if (defend != null) defend.TryManualPlay(null);
            else
            {
                var enemy = me.Creature.CombatState!.Enemies.First();
                me.PlayerCombatState.Hand.Cards.First(c => c.CanPlay(out _,out _) && c.CanPlayTargeting(enemy)).TryManualPlay(enemy);
            }
            await Wait(() => Rankings().Contains('%') && Rankings() != first, "updated hand");
            await Snapshot("after-card");
            await CreatureCmd.Kill(me.Creature.CombatState!.Enemies.ToList(), true);
            await CombatManager.Instance.CheckWinCondition();
            await Wait(() => NOverlayStack.Instance?.Peek() is NRewardsScreen, "post-combat rewards");
            await Wait(() => !panel.Visible, "previous combat advice hidden");
            var rewards = (RewardsSet)typeof(NRewardsScreen).GetField("_rewardsSet", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(NOverlayStack.Instance!.Peek())!;
            _ = rewards.Rewards.OfType<CardReward>().First().SelectUnsynchronized();
            await Wait(() => Rankings().Contains('%'), "card reward rankings");
            await Snapshot("reward");
            GD.Print("[CoachCheck] PASS");
            tree.Quit();
        }
        catch (Exception ex) { GD.PushError("[CoachCheck] FAIL " + ex); tree.Quit(1); }
    }
}
