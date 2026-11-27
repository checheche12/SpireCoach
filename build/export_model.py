"""Export the policy and public-information value for one live position."""
import argparse
import hashlib
import json
import sys
from pathlib import Path

import torch
import onnx
from torch import nn
from torch.nn import functional as F


def squash(x):
    x = x.float()
    return x.sign() * torch.log1p(x.abs().clamp(max=10000))


class LivePolicy(nn.Module):
    def __init__(self, net, upstream_model):
        super().__init__()
        self.net = net
        self.split = upstream_model
        self.register_buffer("projected", net._content().detach())

    def embeddings(self, header, ti, raw):
        n, s = self.net, self.split
        kind, ids, zone, owner, aux = ti.unbind(-1)
        values = squash(raw)
        f, f15 = s._split_v15(values[:, :20], kind)
        f, f16 = s._split_v16(f, kind)
        f, f17 = s._split_v17(f, kind)
        f, f18 = s._split_v18(f, kind)
        f, f19 = s._split_v19(f, kind)
        f, f14 = s._split_v14(f, kind)
        f, f13 = s._split_v13(f, kind)
        f, f11 = s._split_v11(f, kind)
        k = n.kind_emb(kind.clamp(0, 15))
        e = (n.id_emb(ids) + n.aux_emb(aux) + k + n.zone_emb(zone.clamp(0, n.cfg.n_zones - 1)) +
             n.feat(f[:, :16]) + n._feat_tail(f, kind) + (self.projected[ids] + self.projected[aux]))
        e = torch.where((kind == 15)[:, None], k, e) + n.feat_v11(f11)
        e = (e + n.feat_v13(f13) + n.feat_v14(f14) + n.feat_v15(f15)
             + n.feat_v16(f16[:, 12:15]) + n.pw_order(f17[:, 7:8]) + n.map_v18(f18[:, 16:19])
             + n.feat_v19(torch.cat([f19[:, 8:12], f19[:, 16:20]], 1)))
        if n.cfg.obs_v27:
            e = e + n.feat_v27(values[:, 20:22])
        gain = raw[:, 15].round().long().clamp(0, 4095)
        e = e + ((kind == 9) & (raw[:, 15] > 0))[:, None] * n.aux_emb(gain) * n.gain_gate

        global_features = (n.boss_emb(header[16:18]).sum(0) + n.boss2_emb(header[17]) +
            n.hist_lin(squash(header[18:21])) + n.hist_n_emb(header[21]) + n.hist_e_emb(header[22]) +
            n.stage_lin(header[24:26].float()) +
            n.draw_lin(torch.stack([header[26].float(), header[27] * (header[11] > 0) / 10])) +
            n.shop_lin(torch.stack([header[28] / 5, header[29] / 300])) +
            n.v18_lin(torch.stack([header[30] / 3, header[31] / 10])) + n.v19_lin(header[32:34] / 10))
        encounters = (n.enc_next(ids) * values[:, 5:6] + n.enc_bag(ids) * values[:, 4:5] +
                      n.enc_seen(ids) * values[:, 3:4])
        global_features = global_features + (encounters * (kind == 16)[:, None]).sum(0)
        e = e + (kind == 1)[:, None] * global_features

        powers = e + n.bind_pw_out(F.gelu(n.bind_pw_in(e)))
        for col, gate in ((19, n.ret_gate), (18, n.bridge_gate), (17, n.shop_gate)):
            powers = torch.where(((kind == 3) & (values[:, col] > 0))[:, None], powers * gate, powers)
        is_power = (kind == 3) | (kind == 15)
        is_move = kind == 10
        attached = (powers * is_power[:, None] + n.move_out(F.gelu(n.move_in(e))) * is_move[:, None])
        rows = torch.arange(ti.shape[0], device=ti.device)
        e = e + (rows[:, None] == owner[None, :]).float() @ attached

        pool = torch.stack([((kind == 4) & (zone == z)) for z in (4, 5, 6, 7)] +
                           [(kind == 5) & (zone == 1)], 0).float()
        core = ~(pool.bool().any(0) | is_power | is_move | (kind == 16) | (kind == 17) | (kind == 20))
        return e, pool, core, rows

    def score(self, header, ci, cf, e, pool, core, extra_pool, extra_cand, mode):
        from sts2env.protocol import DecisionKind
        n = self.net
        counts = pool.sum(1, keepdim=True)
        piles = (pool @ (e + n.bind_pool_out(F.gelu(n.bind_pool_in(e)))) / counts.clamp(min=1)
                 + n.pool_emb + torch.log1p(counts) * n.pool_n + extra_pool)
        core_ids = torch.nonzero(core).flatten()
        x = torch.cat([e[core_ids], piles])
        # Single position, no padding. Explicit attention keeps the sequence
        # dimension dynamic in ONNX for every hand size and enemy count.
        for layer in n.encoder.layers:
            a = layer.self_attn
            norm = layer.norm1(x)
            q, k, v = F.linear(norm, a.in_proj_weight, a.in_proj_bias).reshape(-1, 3, 4, 64).permute(1, 2, 0, 3).unbind(0)
            attended = ((q * 0.125) @ k.transpose(-1, -2)).softmax(-1) @ v
            x = x + a.out_proj(attended.transpose(0, 1).reshape(-1, 256))
            x = x + layer.linear2(F.relu(layer.linear1(layer.norm2(x))))
        x = n.norm(x)
        g = x.mean(0) + n.dec_emb(header[1]) + n.mode_emb(mode)
        core_pos = (core.long().cumsum(0) - 1).clamp(min=0)
        refs = torch.where(core[:, None], x[core_pos], e)

        def select(idx):
            return torch.where((idx >= 0)[:, None], refs[idx.clamp(min=0)], n.none_ref)

        act = (n.act_emb(ci[:, 0]) + n.cand_id_emb(ci[:, 3]) + n.cand_feat(cf[:, :4]) +
               n._cand_tail(cf[:, 4:8], ci[:, 0]) + n.cand_v19(cf[:, 8:12]) + extra_cand)
        if n.cfg.obs_v26:
            act = act + n.cand_v26(cf[:, 12:14])
        z = torch.cat([select(ci[:, 1]), select(ci[:, 2]), act, g.expand_as(act)], 1)
        decision = header[1]
        macro = ((mode == 1) & (header[11] == 0) & (decision != DecisionKind.COMBAT)
                 & (decision != DecisionKind.GAME_OVER) & (decision != DecisionKind.STUCK))
        combat = n.cand_mlp(z)
        if n.cfg.future_head:
            combat = combat + n.fh_res(n.fh_pred(z))
        logits = torch.where(macro, n.cand_mlp_m(z), combat).flatten()
        # With privileged columns cleared, all draw-pile cards have equal weight.
        top = (pool[0] @ e) / pool[0].sum().clamp(min=1)
        hidden = n.priv_proj(top) + n.priv_enc(n.id_emb.weight[0].repeat(2))
        state = torch.cat([g, hidden])
        value = torch.where(macro, n.value_mlp_m(state), n.value_mlp(state))
        if n.cfg.value_hl:
            bins = torch.where(macro, n.value_hl_m(state), n.value_hl(state))
            value = value + n.hl_beta * (bins.softmax(-1) @ n.hl_centers - value)
        return logits, value

    def forward(self, header, ti, tf, ci, raw_cf, mode):
        n = self.net
        cf = squash(raw_cf)
        e, pool, core, rows = self.embeddings(header, ti, tf)
        logits, value = self.score(header, ci, cf, e, pool, core, 0, 0, mode)
        if not n.cfg.recycle:
            return logits.softmax(0), value
        lp = logits.log_softmax(0)
        p = lp.exp()
        ent = -(p * lp).sum()
        # Padding by zero also handles decisions with only one legal action.
        top = torch.cat([p, p.new_zeros(1)]).topk(2).values
        fs = torch.stack([ent, top[0], top[0] - top[1], torch.ones_like(p).sum().log() / 3])
        fc = torch.stack([p, lp.clamp(min=-20) / 8, (logits - logits.max()).clamp(min=-20) / 8,
                          (p >= top[0] - 1e-6).float(), ent.expand_as(p), top[0].expand_as(p)], 1)
        add = ((rows[:, None] == ci[:, 1]).float() @ n.rcy_a(fc) +
               (rows[:, None] == ci[:, 2]).float() @ n.rcy_b(fc))
        logits, value = self.score(header, ci, cf, e + add, pool, core, n.rcy_p(fs), n.rcy_c(fc), mode)
        return logits.softmax(0), value


def main():
    a = argparse.ArgumentParser()
    for key in ("upstream", "checkpoint", "out"):
        a.add_argument(key, type=Path)
    a.add_argument("--game-version", choices=("v0.113.1", "v0.114.0"), default="v0.113.1")
    a.add_argument("--verify", action="store_true", help="Check a cached export against its source manifest")
    opt = a.parse_args()
    models = json.loads(Path(__file__).with_name("models.json").read_text())
    if models.get(opt.out.stem) != opt.checkpoint.name:
        raise ValueError("Checkpoint does not match the requested character")
    sums = {}
    for manifest in (opt.upstream / "weights").glob("SHA256SUMS*.txt"):
        for line in manifest.read_text().splitlines():
            if line.strip():
                digest, name = line.split()
                sums[name] = digest
    source = opt.upstream / "game-v0.114.0" if opt.game_version == "v0.114.0" else opt.upstream
    if (source / "engine/game_version.txt").read_text().strip() != opt.game_version:
        raise ValueError("Incorrect source catalog")
    metadata = {"character": opt.out.stem.upper(), "checkpoint": opt.checkpoint.name,
                "checkpoint_sha256": sums[opt.checkpoint.name], "catalog_version": opt.game_version}
    if opt.verify:
        actual = {m.key: m.value for m in onnx.load(opt.out).metadata_props}
        if not all(actual.get(k) == v for k, v in metadata.items()):
            raise ValueError(f"Cached model does not match {metadata}: {opt.out}")
        return
    if hashlib.sha256(opt.checkpoint.read_bytes()).hexdigest() != sums[opt.checkpoint.name]:
        raise ValueError(f"Checkpoint digest mismatch: {opt.checkpoint.name}")
    sys.path.insert(0, str(source / "python"))
    from sts2rl import model
    from sts2rl.catalog import Catalog
    torch.set_num_threads(2)
    blob = torch.load(opt.checkpoint, map_location="cpu", weights_only=True)
    net = model.PolicyValueNet(model.ModelConfig(**blob["config"]["model"])).eval()
    model.load_state(net, blob["state"], blob.get("content_names"))
    with torch.no_grad():
        vocab_size = Catalog().vocab_size
        old_size = blob.get("vocab_n") or 1873
        if old_size < vocab_size:
            model.zero_new_vocab(net, old_size, vocab_size)
        fmt = blob["obs_format"]
        module = LivePolicy(net, model).eval()
        h = torch.zeros(34, dtype=torch.long)
        h[0], h[1], h[11] = fmt, 1, 1
        ti = torch.tensor([[1, 0, 0, -1, 0], [2, 20, 1, -1, 0], [4, 41, 3, 1, 0], [4, 41, 4, 1, 0]])
        ci = torch.tensor([[2, 2, 1, 0, 0], [1, -1, -1, 0, 0]])
        args = (h, ti, torch.zeros(4, 22 if fmt >= 27 else 20), ci, torch.zeros(2, 14 if fmt >= 26 else 12), torch.tensor(1))
        names = ["header", "ti", "tf", "ci", "cf", "mode"]
        torch.onnx.export(module, args, str(opt.out), input_names=names, output_names=["probabilities", "value"],
            dynamic_axes={k: {0: "tokens" if k in ("ti", "tf") else "candidates"} for k in names[1:5] + ["probabilities"]},
            opset_version=17, dynamo=False, external_data=False)
        graph = onnx.load(opt.out)
        onnx.helper.set_model_props(graph, {**metadata, "obs_format": str(fmt),
                                            "victory_value": str(blob["config"]["victory_reward"] + blob["config"].get("act3_boss2_reward", 0))})
        onnx.save(graph, opt.out)


if __name__ == "__main__":
    main()
