using System.Text.Json;
using Sts2SimServe;

namespace SpireCoach;

// AlphaDefect game-v0.111.0/tools/sim_pimc.py, using the authors' patched sts2-sim.
internal sealed partial class RolloutSearch(MapAnchor anchor, byte[] entry, string vocab, string tables)
{
    partial void RecordRollout(int index, uint seed, double score);
    private readonly string[] names=File.ReadAllLines(vocab);
    private readonly Session sim=Start(anchor);
    private readonly ObsAdapter encoder=new(vocab,tables);
    private readonly List<int> tape=[];
    private bool anchored;

    private static readonly Lazy<bool> registry=new(() => { Sts2Sim.Core.Models.ModelDb.Init(Sts2Sim.Core.Content.ContentRegistry.AllTypes); return true; });
    private static Session Start(MapAnchor anchor)
    {
        _=registry.Value;
        using var save=JsonDocument.Parse(anchor.SaveJson);
        return Session.Start(save.RootElement,anchor.NextRoom,anchor.Col,anchor.Row,anchor.Room);
    }

    public void Initialize()
    {
        encoder.SetRoot(new Frame(entry),sim);
        Anchor();
    }
    private SearchFrame Observe() => new(encoder.Encode(sim));
    private void Anchor()
    {
        if(sim.CanSave) { sim.Save(0); tape.Clear(); anchored=true; }
    }
    private SearchFrame Align(SearchFrame real)
    {
        var mirror=Observe();
        if(mirror.Board!=real.Board && sim.CanSave) {
            sim.Sync(real.PublicSync(names));
            Anchor();
            mirror=Observe();
        }
        return mirror;
    }
    public bool Follow(SearchFrame real,int action)
    {
        if(sim.Over) return false;
        var mirror=Align(real);
        if(mirror.Board!=real.Board) return false;
        var map=SearchFrame.Map(real,mirror);
        if(map==null) return false;
        sim.Step(map[action]);
        tape.Add(map[action]);
        Anchor();
        return true;
    }
    private void Restore()
    {
        sim.Load(0);
        foreach(int action in tape) sim.Step(action);
    }
    public SearchReply Decide(int id, SearchFrame real,double[] priors,uint seedBase,bool defect,
        Func<SearchFrame,int,(double[] p,double v)> evaluate,Action checkTime)
    {
        var mirror=Align(real);
        var map=SearchFrame.Map(real,mirror);
        if(!anchored || mirror.Board!=real.Board || map==null) return new(id,"unmatched");
        var candidates=Enumerable.Range(0,priors.Length).OrderByDescending(i=>priors[i]).Take(4).Where(i=>priors[i]>=.02).ToArray();
        if(candidates.Length<2) return new(id,"confident");
        int greedy=candidates[0], used=0, per=4, trials=0;
        var scores=candidates.ToDictionary(i=>i,i=>new List<double>());
        var alive=candidates;
        try {
            for(int round=0;round<2;round++) {
                foreach(int i in alive) for(int j=0;j<per;j++) {
                    checkTime();
                    Restore();
                    if(defect && sim.Player.PlayerCombatState?.DrawPile is {} pile) pile.KnownTop=0;
                    sim.Reseed(unchecked(seedBase+(uint)(used+j)));
                    int action=map[i];
                    double left=mirror.EnemyLeft();
                    // The root candidate is step zero in sim_pimc._World.
                    for(int step=0;;step++) {
                        checkTime(); sim.Step(action);
                        var f=Observe();
                        if(f.Terminal || !f.Combat) {
                            double enemyLeft=Math.Round(Math.Min(1,left),4,MidpointRounding.ToEven);
                            scores[i].Add(f.H[15]==0 && f.H[8]>0 ? 1.5 : -1+.25*(f.H[23]+1-enemyLeft)); break;
                        }
                        left=f.EnemyLeft();
                        bool leaf=step>=12;
                        var (p,v)=evaluate(f,leaf ? 0 : 1);
                        if(leaf) { scores[i].Add(v); break; }
                        action=Array.IndexOf(p,p.Max());
                    }
                    RecordRollout(i,unchecked(seedBase+(uint)(used+j)),scores[i][^1]);
                    trials++;
                }
                used+=per;
                if(alive.Length>2) {
                    alive=[greedy,alive.Skip(1).OrderByDescending(i=>scores[i].Average()).ThenByDescending(i=>priors[i]).First()];
                    per*=2;
                }
            }
        } finally { Restore(); }
        if(Observe().Board!=mirror.Board) return new(id,"unmatched");
        int best=greedy;
        double bestMean=0;
        foreach(int i in candidates.Skip(1)) {
            var diff=scores[i].Zip(scores[greedy],(a,b)=>a-b).ToArray();
            double mean=diff.Average();
            double se=Math.Sqrt(diff.Sum(d=>(d-mean)*(d-mean))/(diff.Length-1)/diff.Length);
            if(mean>bestMean && mean>2*se) { best=i; bestMean=mean; }
        }
        return new(id,"rollout",best,trials);
    }
}
