namespace SpireCoach;

// Port of python/sts2rl/tree_live.py (_search_inner, _simulate, _select).
// Eight logical lanes share a tree. Leaf expansion happens after the entire sweep,
// so sequential CPU inference preserves the original virtual visit counts.
internal sealed class InformationSetTree(
    Func<uint,List<int>,(SearchFrame frame,int steps)> simulate,
    Func<SearchFrame,(double[] p,double v)> evaluate, Action checkTime,
    double victoryReward, bool firstActThreeBoss, double act1BossReward = .1)
{
    private sealed class Edge
    {
        public int Visits, Backed;
        public double Sum;
        public readonly Dictionary<string,Node> Outcomes = new();
    }
    private sealed class Node
    {
        public Dictionary<string,double>? Prior;
        public double Value;
        public readonly Dictionary<string,Edge> Edges = new();
        public Edge Get(string key)
        {
            if (!Edges.TryGetValue(key,out var edge)) Edges[key]=edge=new();
            return edge;
        }
    }
    private sealed class Lane(uint seed, string candidate, Edge edge, int action, SearchFrame root)
    {
        public readonly uint Seed=seed;
        public readonly string Candidate=candidate;
        public readonly List<Edge> Path=[edge];
        public readonly List<int> Actions=[action];
        public int Floor=root.H[7], Act=root.H[6];
        public bool BossPaid;
        public double Reward;
    }
    private static Dictionary<string,double> Merge(SearchFrame frame,double[] p)
    {
        var priors=new Dictionary<string,double>();
        for(int i=0;i<p.Length;i++) priors[frame.Keys[i]]=priors.GetValueOrDefault(frame.Keys[i])+p[i];
        return priors;
    }
    private static string Select(Node node,string[] legal)
    {
        var edges=legal.Select(node.Get).ToArray();
        var priors=legal.Select(k=>Math.Max(node.Prior!.GetValueOrDefault(k),1e-6)).ToArray();
        double mass=0, weighted=0;
        int visits=edges.Sum(e=>e.Visits), backed=edges.Sum(e=>e.Backed);
        for(int i=0;i<edges.Length;i++)
            if(edges[i].Backed>0) { mass+=priors[i]; weighted+=priors[i]*edges[i].Sum/edges[i].Backed; }
        double mixed=(node.Value+(mass>0 ? backed*weighted/mass : 0))/(1+backed);
        var q=edges.Select(e=>e.Backed>0 ? e.Sum/e.Backed : mixed).ToArray();
        double low=q.Min(), range=Math.Max(q.Max()-low,1e-8), scale=(50+edges.Max(e=>e.Visits))*.1;
        var logits=q.Select((v,i)=>Math.Log(priors[i])+scale*(v-low)/range).ToArray();
        double max=logits.Max();
        var pi=logits.Select(x=>Math.Exp(x-max)).ToArray();
        double sum=pi.Sum();
        return legal[Enumerable.Range(0,legal.Length).MaxBy(i=>pi[i]/sum-(double)edges[i].Visits/(1+visits))];
    }
    public SearchReply Decide(int id,SearchFrame root,double[] p,uint seed)
    {
        int greedy=Array.IndexOf(p,p.Max());
        string g=root.Keys[greedy];
        var start=new Node { Prior=Merge(root,p) };
        var candidates=start.Prior.Keys.OrderByDescending(k=>start.Prior[k]).Take(4).Where(k=>start.Prior[k]>=.02).ToList();
        if(!candidates.Contains(g)) { candidates.Insert(0,g); if(candidates.Count>4) candidates.RemoveAt(4); }
        if(candidates.Count<2) return new(id,"confident");
        int phases=(int)Math.Ceiling(Math.Log2(candidates.Count)), used=0, count=0;
        var alive=candidates.ToList();
        for(int phase=0;phase<phases;phase++) {
            int per=Math.Max(1,32/phases/alive.Count);
            var queue=new Queue<(uint seed,string key)>(
                from j in Enumerable.Range(0,per) from k in alive select (seed+(uint)(used+j), k));
            used+=per;
            // Keep insertion order independently of slot number (Python's busy dictionary).
            var busy=new List<(int slot,Lane w)>();
            while(queue.Count+busy.Count>0) {
                checkTime();
                for(int j=0;j<8 && queue.Count>0;j++) {
                    if(busy.Any(x=>x.slot==j)) continue;
                    var work=queue.Dequeue();
                    var edge=start.Get(work.key);
                    edge.Visits++;
                    busy.Add((j,new(work.seed,work.key,edge,Array.IndexOf(root.Keys,work.key),root)));
                }
                var leaves=new List<(int slot,Lane w,Node? node,SearchFrame frame)>();
                var posts=new List<(int slot,Lane w,Node? node,SearchFrame frame)>();
                foreach(var (slot,w) in busy.ToArray()) {
                    checkTime();
                    var (frame,steps)=simulate(w.Seed,w.Actions);
                    var h=frame.H;
                    if(h[7]>w.Floor) w.Reward+=(h[7]-w.Floor)*.02;
                    for(int act=w.Act+1;act<=h[6];act++) w.Reward+=act switch {1=>act1BossReward,2=>.5,_=>0};
                    if(firstActThreeBoss && !w.BossPaid && h[4]==0 && (!frame.Combat || h[7]>root.H[7])) {
                        w.Reward+=1.5; w.BossPaid=true;
                    }
                    if(h[4]!=0 && h[5]!=0 && h[15]==0) w.Reward+=victoryReward;
                    w.Floor=Math.Max(w.Floor,h[7]); w.Act=Math.Max(w.Act,h[6]);
                    if(frame.Terminal) { Backup(slot,w,w.Reward); continue; }
                    if(!frame.Combat || steps>600) { posts.Add((slot,w,null,frame)); continue; }
                    var edge=w.Path[^1]; var sig=frame.Signature;
                    if(!edge.Outcomes.TryGetValue(sig,out var child)) edge.Outcomes[sig]=child=new();
                    if(child.Prior==null || w.Path.Count>=200) { leaves.Add((slot,w,child,frame)); continue; }
                    var key=Select(child,frame.Keys.Distinct().ToArray());
                    var next=child.Get(key); next.Visits++;
                    w.Path.Add(next); w.Actions.Add(Array.IndexOf(frame.Keys,key));
                }
                foreach(var (slot,w,node,frame) in leaves.Concat(posts)) {
                    checkTime();
                    var (pr,v)=evaluate(frame);
                    if(node!=null && node.Prior==null) { node.Prior=Merge(frame,pr); node.Value=w.Reward+v; }
                    Backup(slot,w,w.Reward+v);
                }
                void Backup(int slot,Lane w,double value)
                {
                    foreach(var e in w.Path) { e.Backed++; e.Sum+=value; }
                    count++; busy.RemoveAll(x=>x.slot==slot);
                }
            }
            if(alive.Count>2 && phase<phases-1)
                alive=[g,..alive.Where(k=>k!=g).OrderByDescending(k=>start.Get(k).Sum/start.Get(k).Backed)
                    .Take((alive.Count+1)/2-1)];
        }
        string best=candidates.Where(k=>start.Get(k).Backed>=4)
            .OrderByDescending(k=>Math.Log(Math.Max(start.Prior[k],1e-6))+start.Get(k).Sum/start.Get(k).Backed/.06)
            .ThenByDescending(k=>k==g).FirstOrDefault() ?? g;
        return new(id,"tree",best==g ? greedy : Array.IndexOf(root.Keys,best),count);
    }
}
