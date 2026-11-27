"""Compare the distributed policy with AlphaDefect on captured game frames."""
import argparse
import json
from dataclasses import replace
from pathlib import Path
import sys
import numpy as np
import onnxruntime as ort
import torch

def main():
    parser = argparse.ArgumentParser()
    for name in ("upstream", "checkpoint", "model", "frames"):
        parser.add_argument(name, type=Path)
    parser.add_argument("--screens", nargs="+", default=["combat", "after-card", "reward"])
    args = parser.parse_args()
    sys.path.insert(0, str(args.upstream / "python"))
    from sts2env.protocol import ActionKind, DecisionKind, TokenKind, Zone, parse
    from sts2rl.features import Ragged, collate_ragged
    from sts2rl.greedy import _content, equivalence_classes, merged_argmax
    sys.path.insert(0, str(args.upstream / "tools"))
    from ability_test import load_net
    torch.set_num_threads(2)
    net, blob = load_net(str(args.checkpoint))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(args.model), options, providers=["CPUExecutionProvider"])
    worst = 0.0
    with torch.inference_mode():
        for name in args.screens:
            obs = parse((args.frames / (name + ".bin")).read_bytes())
            screen = json.loads((args.frames / (name + ".json")).read_text(encoding="utf-8"))
            if name == "fool-114":
                from sts2rl.catalog import Catalog
                catalog = Catalog()
                assert catalog.version == session.get_modelmeta().custom_metadata_map["catalog_version"] == "v0.114.0"
                hand = obs.tok_ints[:, 2] == Zone.HAND
                ids = obs.tok_ints[:, 1]
                optimism, = np.flatnonzero(hand & (ids == catalog.vocab.index("CARD.OPTIMISM")))
                assert np.count_nonzero(hand & (ids == catalog.vocab.index("CARD.RAPID_SHOT"))) == 1
                assert np.count_nonzero(hand & (ids == catalog.vocab.index("CARD.SADNESS"))) == 2
                play, = np.flatnonzero((obs.cand_ints[:, 0] == ActionKind.PLAY_CARD) & (obs.cand_ints[:, 1] == optimism))
                assert obs.cand_feats[play, 13] == 2
            if name.startswith("potion-"):
                assert obs.decision == DecisionKind.REWARDS
                potions = obs.tok_ints[:, 0] == TokenKind.POTION
                offered, = np.flatnonzero(potions & (obs.tok_ints[:, 2] == Zone.OFFER))
                carried = np.flatnonzero(potions & (obs.tok_ints[:, 2] == Zone.ALLY))
                kinds, items = obs.cand_ints[:, 0], obs.cand_ints[:, 1]
                assert np.sum(kinds == ActionKind.LEAVE) == 1
                assert (offered in items[kinds == ActionKind.TAKE_REWARD]) == (name == "potion-open")
                drops = items[kinds == ActionKind.DISCARD_POTION]
                assert set(drops) == (set() if name == "potion-open" else set(carried))
            if name.startswith("camp-") and name != "camp-cards":
                assert obs.decision == DecisionKind.REST_SITE
                rest = obs.cand_ints[:, 0] == ActionKind.REST_OPTION
                options = obs.cand_ints[rest, 1]
                assert np.all(options >= 0) and np.all(obs.tok_ints[options, 0] == TokenKind.OPTION)
                assert np.all(obs.tok_ints[options, 1] > 0)
                assert sum(obs.cand_ints[:, 0] == ActionKind.LEAVE) == int(name == "camp-again")
                heals = obs.tok_feats[options]
                heals = heals[heals[:, 3] > 0]
                if name == "camp-full":
                    assert len(heals) == 0 and len(options) == 1
                elif name == "camp-extra":
                    assert len(heals) == 1 and heals[0, 2] == 0 and heals[0, 4] > 0
                else:
                    assert len(heals) == 1 and 0 < heals[0, 2] <= heals[0, 3]
            if name == "camp-cards":
                assert obs.decision == DecisionKind.CARD_SELECT
                assert np.all(obs.cand_ints[:, 0] == ActionKind.SELECT_CARD)
                previews = obs.cand_ints[:, 2]
                assert np.all(previews >= 0) and np.all(obs.tok_ints[previews, 2] == Zone.UPGRADE_PREVIEW)
            if name.startswith("select-"):
                effect = name.split("-")[1]
                assert obs.decision == DecisionKind.CARD_SELECT and obs.in_combat == (effect != "newgrid")
                prompt, = np.flatnonzero(obs.tok_ints[:, 0] == TokenKind.SELECT_PROMPT)
                f = obs.tok_feats[prompt]
                hand = effect in ("discard", "multi", "upgrade", "exhaust")
                zone_flag = 12 if hand else 13 if effect in ("pile", "drawpile", "exhaustpile") else 15
                assert f[zone_flag] == f[12:16].sum() == 1
                if hand:
                    assert f[{"discard": 4, "multi": 4, "upgrade": 5, "exhaust": 1}[effect]] == 1
                    assert obs.tok_ints[prompt, 1] > 0
                selected = int(name.endswith("-next"))
                assert f[9] == np.sum(obs.tok_ints[:, 0] == TokenKind.SELECTED_CARD) == selected
                choices = obs.cand_ints[obs.cand_ints[:, 0] == ActionKind.SELECT_CARD]
                assert f[11] == len(choices) + selected
                assert f[8] == (2 if effect in ("multi", "newgrid") else 1)
                assert np.all(choices[:, 1] >= 0)
                if effect == "upgrade":
                    assert np.all(choices[:, 2] >= 0) and np.all(obs.tok_ints[choices[:, 2], 2] == Zone.UPGRADE_PREVIEW)
                if effect == "generate":
                    assert (f[7] == 0) == bool(np.any(obs.cand_ints[:, 0] == ActionKind.CONFIRM_SELECTION))
            if name.startswith("deck-"):
                effect = name.split("-")[1]
                prompt_feature = {"transform": 0, "remove": 2, "enchant": 3, "upgrade": 5}[effect]
                assert obs.decision == DecisionKind.CARD_SELECT
                prompt, = np.flatnonzero(obs.tok_ints[:, 0] == TokenKind.SELECT_PROMPT)
                features = obs.tok_feats[prompt]
                assert features[prompt_feature] == features[14] == 1  # Purpose and deck origin
                selected = int(name.endswith("-next"))
                assert features[9] == np.sum(obs.tok_ints[:, 0] == TokenKind.SELECTED_CARD) == selected
                assert features[7] == features[8] == (2 if effect == "transform" else 1)
                assert features[11] == obs.num_actions + selected
                assert np.all(obs.cand_ints[:, 0] == ActionKind.SELECT_CARD)
                assert np.all(obs.cand_ints[:, 1] >= 0)
                if effect == "upgrade":
                    previews = obs.cand_ints[:, 2]
                    assert np.all(previews >= 0) and np.all(obs.tok_ints[previews, 2] == Zone.UPGRADE_PREVIEW)
                if effect == "enchant":
                    assert obs.tok_ints[prompt, 4] > 0
            assert obs.header[0] == blob["obs_format"] or (
                obs.header[0] == 22 and blob["obs_format"] == 28 and screen["character"] in ("IRONCLAD", "NECROBINDER"))
            assert session.get_modelmeta().custom_metadata_map["character"] == screen["character"]
            for size in sorted({1, max(1, obs.num_actions - 1), obs.num_actions}):
                header = obs.header.copy()
                header[3] = size
                sample = replace(obs, header=header, cand_ints=obs.cand_ints[:size], cand_feats=obs.cand_feats[:size])
                batch = collate_ragged(Ragged.from_obs([sample], np.ones(1, dtype=np.int64))).to("cpu")
                logits, _ = net.forward_infer(batch)
                expected = logits[0, :size].softmax(-1).numpy()
                feed = {"header": header.astype(np.int64), "ti": sample.tok_ints.astype(np.int64),
                        "tf": sample.tok_feats[:, :22 if blob["obs_format"] >= 27 else 20],
                        "ci": sample.cand_ints.astype(np.int64), "cf": sample.cand_feats[:, :14 if blob["obs_format"] >= 26 else 12]}
                if any(i.name == "mode" for i in session.get_inputs()):
                    feed["mode"] = np.array(1, dtype=np.int64)
                    combat_public = replace(sample, tok_priv=np.zeros_like(sample.tok_priv))
                    combat_batch = collate_ragged(Ragged.from_obs([combat_public], np.zeros(1, dtype=np.int64))).to("cpu")
                    combat_logits, combat_value = net.forward_infer(combat_batch)
                    cp, cv = session.run(["probabilities", "value"], dict(feed, mode=np.array(0, dtype=np.int64)))
                    np.testing.assert_allclose(cp, combat_logits[0,:size].softmax(-1).numpy(), atol=1e-4)
                    np.testing.assert_allclose(cv.item(), combat_value.item(), atol=1e-4)
                result, value = session.run(["probabilities", "value"], feed)
                public = replace(sample, tok_priv=np.zeros_like(sample.tok_priv))
                public_batch = collate_ragged(Ragged.from_obs([public], np.ones(1, dtype=np.int64))).to("cpu")
                _, expected_value = net.forward_infer(public_batch)
                assert abs(value.item() - expected_value.item()) < 0.0001, (value.item(), expected_value.item())
                diff = np.max(np.abs(result - expected)).item()
                top, actual_top = merged_argmax(sample, expected), merged_argmax(sample, result)
                print(f"{name} n={size} max_abs={diff:.8f} top={top}/{actual_top}", flush=True)
                display_sample = sample
                upgrade = screen.get("upgrade", False)
                if upgrade:
                    assert obs.decision == DecisionKind.CARD_SELECT
                    refs = sample.cand_ints.copy()
                    children = {}
                    for child in np.flatnonzero(sample.tok_ints[:, 3] >= 0):
                        children.setdefault(sample.tok_ints[child, 3], []).append(child)
                    previews = {}
                    for action in refs:
                        if action[0] == ActionKind.SELECT_CARD and action[2] >= 0:
                            assert sample.tok_ints[action[2], 2] == Zone.UPGRADE_PREVIEW
                            key = _content(sample, action[2], children)
                            action[2] = previews.setdefault(key, action[2])
                    display_sample = replace(sample, cand_ints=refs)
                classes = equivalence_classes(display_sample)
                assert classes[top] == classes[actual_top]
                if size == obs.num_actions:
                    totals = {}
                    counts = {}
                    for index, probability in zip(classes, expected):
                        totals[index] = totals.get(index, 0.) + float(probability)
                        counts[index] = counts.get(index, 0) + 1
                    per_card = {ActionKind.SELECT_CARD} if upgrade else set()
                    if "copyTotals" in screen and obs.decision == DecisionKind.COMBAT:
                        per_card.add(ActionKind.PLAY_CARD)
                    copies = {i: counts[i] if sample.cand_ints[i, 0] in per_card else 1
                              for i in totals}
                    if name.startswith("copies"):
                        assert max(copies.values()) == (3 if name == "copies-after-play" else 4)
                    start = screen.get("page", 0) * 4
                    combined = screen.get("combined", False)
                    scores = totals if combined else {i: totals[i] / copies[i] for i in totals}
                    ordered = sorted(scores, key=scores.get, reverse=True)[start:start + 4]
                    lines = screen["rankings"].splitlines()
                    assert len(lines) == len(ordered)
                    if "copyTotals" in screen:
                        assert len(screen["copyTotals"]) == len(ordered)
                    for offset, index in enumerate(ordered):
                        rank = start + offset + 1
                        label, percent = lines[offset].rsplit("    ", 1)
                        assert label == f"{rank}.  {screen.get('displayNames', screen['names'])[index]}", (label, index)
                        assert abs(float(percent.removesuffix("%")) - 100 * scores[index]) < 0.051
                        if "copyTotals" in screen:
                            detail = screen["copyTotals"][offset]
                            if copies[index] == 1:
                                assert detail == "", (name, rank, detail)
                            else:
                                prefix = (f"{copies[index]}장 · 한 장당 " if combined else f"{copies[index]}장 합계 ")
                                detail_score = totals[index] / copies[index] if combined else totals[index]
                                assert detail.startswith(prefix) and detail.endswith("%"), detail
                                assert abs(float(detail[len(prefix):-1]) - 100 * detail_score) < 0.051
                worst = max(worst, diff)
    assert worst < 1e-4, worst

if __name__ == "__main__":
    main()
