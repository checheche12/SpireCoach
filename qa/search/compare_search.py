"""Compare a QA game's captured boss decision with the pinned AlphaDefect drivers."""
import argparse
import base64
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace

import numpy as np
import torch


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("upstream", "checkpoint", "mod", "vocab", "cache", "root", "dotnet"):
        ap.add_argument(name, type=Path)
    ap.add_argument("--trace-worker", type=Path, help="Tree QA build with -p:SearchParity=true")
    args = ap.parse_args()
    pos = json.loads(args.root.read_text(encoding="utf-8-sig"))
    tree = pos["Character"] in ("IRONCLAD", "FOOL", "NECROBINDER")
    sys.path[:0] = [str(args.upstream / "python"), str(args.upstream / "tools")]
    from ability_test import load_net
    from sts2env.protocol import parse
    from sts2rl import tree_live as T
    from sts2env import sim_client
    torch.set_num_threads(2)
    net, _ = load_net(str(args.checkpoint))
    raw = base64.b64decode(pos["Frame"])
    root = parse(raw)
    with tempfile.TemporaryDirectory(dir=args.cache.parent) as temp:
        temp = Path(temp)
        for f in args.cache.iterdir():
            if f.is_file() and f.name != "SpireCoach.SearchHost.dll":
                os.link(f, temp / f.name)
        host = args.trace_worker or (args.mod / "runtime/search" / (args.cache.name + ".dll"))
        os.link(host, temp / "SpireCoach.SearchHost.dll")
        trace = temp / "worlds.jsonl"
        env = {**os.environ, "COACH_QA_WORLD_TRACE": str(trace), "STS2_GAME_DIR": str(args.cache)}
        env.pop("SENTRY_GODOT_LIB_PATH", None)
        exe = "SpireCoach.SearchHost" + (".exe" if os.name == "nt" else "")
        cmds = [dict(Id=1, Op="prepare", Position=pos), dict(Id=1, Op="search", BudgetMs=30000)]
        # Keep stdin open until the worker has answered. Its parent lifetime is pipe-bound.
        with subprocess.Popen([str(temp / exe), str(args.mod), str(args.vocab)], cwd=args.cache, env=env,
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as worker:
            def receive():
                for line in worker.stdout:
                    if line.startswith(b"@spirecoach "):
                        return json.loads(line[12:])
                raise RuntimeError("worker exited before reply")
            try:
                assert receive()["Status"] == "online"
                for command in cmds:
                    worker.stdin.write(json.dumps(command).encode() + b"\n")
                    worker.stdin.flush()
                    reply = receive()
                    if command["Op"] == "prepare":
                        assert reply["Status"] == "ready", reply
                assert reply["Status"] == ("tree" if tree else "rollout"), reply
            finally:
                worker.stdin.close()
        if tree:
            records = [json.loads(line) for line in trace.read_text().splitlines()]
            worlds = {(r["seed"], tuple(r["actions"])): r for r in records}
            class Slot:
                def send(self, req):
                    self.actions.append(req["action"])
            class Reference(T.TreeSearchAdvisor):
                def _dispatch(self, j, w):
                    slot = self._workers[j]
                    slot.actions = [w["cand"]]
                    w["steps"] = 0
                def _take(self, j, w):
                    r = worlds[(w["seed"], tuple(self._workers[j].actions))]
                    w["frame"] = parse(base64.b64decode(r["frame"]))
                    w["steps"] = r["steps"]
                    return "frame"
            ref = object.__new__(Reference)
            ref._workers = [Slot() for _ in range(8)]
            ref.T = T.TreeParams()
            ref._rw = T.DEFAULT_RW
            ref._ctx = dict(boss3_first=pos["FirstActThreeBoss"], floor0=int(root.floor))
            ref._clock, ref._deadline = time.monotonic, float("inf")
            ref.config = SimpleNamespace(device="cpu")
            ref._get_net = lambda: net
            logits, _ = ref._forward([root])
            p = torch.softmax(logits[0, :root.num_actions].double(), -1).numpy()
            res = ref._search_inner(root, p, pos["Seed"])
            expected = (res["best"], res["sims"])
        else:
            source = subprocess.check_output(["git", "-C", str(args.upstream), "show", "HEAD:game-v0.111.0/tools/sim_pimc.py"])
            source_path = temp / "ref_pimc.py"
            source_path.write_bytes(source)
            spec = importlib.util.spec_from_file_location("ref_pimc", source_path)
            ref = importlib.util.module_from_spec(spec)
            sys.modules[spec.name] = ref
            spec.loader.exec_module(ref)
            sim_client.DOTNET = args.dotnet.resolve()
            simdir = args.upstream / "game-v0.111.0/engine/Sts2SimServe"
            mirror = ref.SimMirror(4, simdir / "bin/Release/net9.0/Sts2SimServe.dll", vocab=args.vocab,
                                   tables=simdir / "ObsPort/sim_tables.json")
            try:
                a = pos["Anchor"]
                m = mirror.reset(json.loads(a["SaveJson"]), a["NextRoom"], [a["Col"], a["Row"]], a["Room"], raw)
                if ref.compare(root, m) is not None:
                    m = mirror._all(ref.sync_req(root, args.vocab.read_text(encoding="utf-8-sig").splitlines()))
                assert ref.compare(root, m) is None
                cmap, _ = ref.cand_map(root, m)
                ev = ref.ModelEvaluator(net)
                scores = {}
                run_worlds = ref.run_worlds
                def traced_worlds(mirror, ev, worlds, *args):
                    run_worlds(mirror, ev, worlds, *args)
                    scores.update({(w.cand, w.seed): w.score for w in worlds})
                ref.run_worlds = traced_worlds
                res = ref.pimc_decide(mirror, ev, root, ev.policy(root), cmap, ref.PimcParams(), pos["Seed"]-7919, 0)
                assert not res.errors and not res.restore_bad
                expected = (res.best, res.rollouts)
                if args.trace_worker:
                    observed = {(r["index"], r["seed"]): r["score"] for r in
                                (json.loads(line) for line in trace.read_text().splitlines())}
                    def card_action(i):
                        c = root.cand_ints[i]
                        t = int(c[1])
                        if t < 0 or root.tok_ints[t, 0] != 4:
                            return ("action", i)
                        # Preserve the target, choice flags, all visible card features, enchantments,
                        # attached effects and candidate features. Only the physical hand slot may vary.
                        ti = root.tok_ints
                        children = sorted((ti[k, [0, 1, 2, 4]].tobytes(), root.tok_feats[k].tobytes())
                                          for k in np.flatnonzero(ti[:, 3] == t))
                        return (tuple(c[[0, 2, 3, 4]]), ti[t, [0, 1, 2, 3, 4]].tobytes(),
                                root.tok_feats[t].tobytes(), tuple(children), root.cand_feats[i].tobytes())
                    def grouped(rows):
                        values = {}
                        for (i, seed), score in rows.items():
                            values.setdefault((card_action(i), seed), []).append(score)
                        return {k: sorted(v) for k, v in values.items()}
                    a_scores, b_scores = grouped(scores), grouped(observed)
                    assert a_scores.keys() == b_scores.keys(), "different sampled actions or seeds"
                    for k, values in a_scores.items():
                        np.testing.assert_allclose(values, b_scores[k], atol=1e-5, rtol=0)
                    if reply["Index"] != res.best:
                        assert card_action(reply["Index"]) == card_action(res.best)
                        print(f"Identical physical copies, upstream index {res.best}, native index {reply['Index']}")
                        expected = (reply["Index"], res.rollouts)
            finally:
                mirror.close()
        actual = (reply["Index"], reply["Trials"])
        assert expected == actual, (expected, actual)
        print(f'{pos["Character"]} {reply["Status"]} action {actual[0]}, {actual[1]} simulations, upstream match')


if __name__ == "__main__":
    main()
