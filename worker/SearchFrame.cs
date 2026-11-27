using System.Runtime.InteropServices;
using System.Text.Json;

namespace SpireCoach;

// Public frame fields used by AlphaDefect's sim_pimc.cand_keys and check_items.
internal sealed class SearchFrame
{
    public byte[] Bytes { get; }
    public int[] H { get; }
    private readonly int[] ti, ci;
    private readonly float[] tf;
    private readonly int width;
    private readonly int[] enemies;
    public string[] Keys { get; }
    public int Count => H[3];
    public bool Terminal => H[4] != 0 || H[15] != 0 || Count == 0;
    public bool Combat => H[11] != 0;
    public string Board { get; }
    public string Signature => Board + "|" + string.Join(';', Keys.Order(StringComparer.Ordinal));
    public int ActionKind(int i) => ci[i*5];
    public string[] LooseKeys() => MakeKeys(true);

    public SearchFrame(byte[] frame)
    {
        Bytes = frame;
        var ints = MemoryMarshal.Cast<byte,int>(frame.AsSpan());
        H = ints[..34].ToArray();
        int n = H[2];
        width = H[0] >= 27 ? 22 : 20;
        ti = ints.Slice(34, n*5).ToArray();
        tf = MemoryMarshal.Cast<int,float>(ints.Slice(34+n*5,n*width)).ToArray();
        ci = ints.Slice(34+n*(7+width), Count*5).ToArray();
        var rows = Enumerable.Range(0,n).ToArray();
        enemies = rows.Where(t => ti[t*5]==2 && ti[t*5+2]==2 && F(t,3)!=0).ToArray();
        int me = Array.FindIndex(rows,t => ti[t*5]==2 && F(t,5)!=0);
        int g = Array.FindIndex(rows,t => ti[t*5]==1);
        var hand = rows.Where(t => ti[t*5]==4 && ti[t*5+2]==3).Select(t => $"{ti[t*5+1]},{F(t,3)}").Order(StringComparer.Ordinal);
        var foes = enemies.Select(t => $"{ti[t*5+1]},{F(t,0)},{F(t,2)}");
        Board = $"{H[1]}|{H[8]}|{F(me,2)}|{F(g,6)}|{string.Join(';',hand)}|{string.Join(';',foes)}";
        Keys = MakeKeys(false);
    }
    private int F(int t,int i) => t < 0 ? -1 : (int)tf[t*width+i];
    public double EnemyLeft()
    {
        double hp=0, max=0;
        for(int t=0;t<H[2];t++)
            if(ti[t*5]==2 && F(t,5)==0 && tf[t*width+3]>0) { hp+=tf[t*width]; max+=tf[t*width+1]; }
        return max>0 ? hp/max : 0;
    }
    // sim_pimc.sync_req copies only the piles and combat statistics visible to the player.
    public JsonElement PublicSync(string[] vocab)
    {
        var rows = Enumerable.Range(0,H[2]).ToArray();
        int me = Array.FindIndex(rows,t => ti[t*5]==2 && F(t,5)!=0);
        int global = Array.FindIndex(rows,t => ti[t*5]==1);
        object[][] Cards(int from,int to) => rows.Where(t => ti[t*5]==4 && ti[t*5+2]>=from && ti[t*5+2]<=to)
            .Select(t => new object[]{vocab[ti[t*5+1]], F(t,3)}).ToArray();
        var top = Cards(16,18);
        return JsonSerializer.SerializeToElement(new {
            player = new { hp=H[8], block=me<0 ? 0 : F(me,2), energy=global<0 ? 0 : F(global,6) },
            piles = new { hand=Cards(3,3), discard=Cards(5,5), exhaust=Cards(6,6), draw=top.Concat(Cards(4,4)), draw_top=top.Length },
            enemies=enemies.Select(t => new object[]{vocab[ti[t*5+1]],F(t,0),F(t,2)})
        });
    }
    private string[] MakeKeys(bool loose)
    {
        string Desc(int t)
        {
            if(t<0 || t>=H[2]) return "-";
            int k=ti[t*5], id=ti[t*5+1], z=ti[t*5+2];
            return k switch {
                4 when loose => $"4,{id},{F(t,3)},{z}",
                4 => $"4,{id},{F(t,3)},{ti[t*5+4]},{F(t,0)},{z}" + (H[0]>=20 && z==3 ? $",{F(t,2)}" : ""),
                2 when F(t,5)!=0 => "2,P",
                2 => $"2,E,{Array.IndexOf(enemies,t)},{id}",
                6 => $"6,{id},{F(t,0)}",
                _ => $"{k},{id},{z}"
            };
        }
        var result = Enumerable.Range(0,Count).Select(i => {
            int b=ci[i*5+2];
            return $"{ci[i*5]}/{ci[i*5+3]}/{Desc(ci[i*5+1])}/{Desc(b>=0 && b<H[2] && ti[b*5+2]==12 ? -1 : b)}";
        }).ToArray();
        // The paper's key merges equal-cost copies even when only one retains.
        // Refine a collision only when the visible card effects actually differ.
        foreach (var group in Enumerable.Range(0, Count).GroupBy(i => result[i]).ToArray()) {
            var variants = group.Select(i => {
                int t=ci[i*5+1];
                if(t<0 || ti[t*5]!=4) return (i, tag:"");
                string flags=string.Join(',', new[] { 9,10,11,12,13,14,15,18,19 }.Select(j=>F(t,j)));
                var effects=Enumerable.Range(0,H[2]).Where(k=>ti[k*5+3]==t && ti[k*5]==3)
                    .Select(k=>$"{ti[k*5+1]}:{F(k,0)}:{F(k,3)}").Order(StringComparer.Ordinal);
                return (i, tag:flags+"/"+string.Join(';',effects));
            }).ToArray();
            if(variants.Select(v=>v.tag).Distinct().Skip(1).Any())
                foreach(var (i,tag) in variants) result[i]+="~"+tag;
        }
        return result;
    }
    public static int[]? Map(SearchFrame real, SearchFrame mirror)
    {
        static int[]? Match(string[] a,string[] b)
        {
            var slots=b.Select((k,i)=>(k,i)).GroupBy(x=>x.k).ToDictionary(g=>g.Key,g=>new Queue<int>(g.Select(x=>x.i)));
            var indices=new int[a.Length];
            for(int i=0;i<a.Length;i++)
                if(!slots.TryGetValue(a[i],out var q) || !q.TryDequeue(out indices[i])) return null;
            return indices;
        }
        var strict=Match(real.Keys,mirror.Keys);
        if(strict!=null) return strict;
        var realLoose=real.LooseKeys();
        var mirrorLoose=mirror.LooseKeys();
        static bool Ambiguous(string[] loose,string[] full) =>
            full.Any(k=>k.Contains('~')) ||
            Enumerable.Range(0,loose.Length).GroupBy(i=>loose[i]).Any(g=>g.Select(i=>full[i]).Distinct().Skip(1).Any());
        if(Ambiguous(realLoose,real.Keys) || Ambiguous(mirrorLoose,mirror.Keys)) return null;
        return Match(realLoose,mirrorLoose) ??
            (real.Count==mirror.Count && Enumerable.Range(0,real.Count).All(i=>real.ActionKind(i)==mirror.ActionKind(i))
                ? Enumerable.Range(0,real.Count).ToArray() : null);
    }
}
