"""Find games an engine lost on gold although its own search thought it was clearly winning.

    python engine/tools/find_gold_losses.py MATCH.jsonl [MATCH2.jsonl ...] --names A,B [--min-gold 15]
        [--window 40] [--top 5] [--out games.json]

Input: `gg match --out` files (the engine of interest is A). For each game A lost by gold, takes A's
highest search score (from its own point of view) in the last --window plies before the end, and
lists the games with the highest such score. --out writes them for tools/LibraryImport.
"""
import argparse
import json

MATE = 900_000


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('files', nargs='+')
    ap.add_argument('--names', required=True, help='comma-separated engine names, one pair per file: A,B[;A,B]')
    ap.add_argument('--min-gold', type=float, default=15.0)
    ap.add_argument('--window', type=int, default=40)
    ap.add_argument('--top', type=int, default=5)
    ap.add_argument('--out')
    args = ap.parse_args()
    pairs = [p.split(',') for p in args.names.split(';')]
    if len(pairs) == 1:
        pairs = pairs * len(args.files)

    found = []
    totals = []
    for path, (name_a, name_b) in zip(args.files, pairs):
        games = [json.loads(line) for line in open(path)]
        a_gold_losses = 0
        for g in games:
            a_white = g['a_is_white']
            res = g['result']
            if not res.endswith('+g') or (res[0] == 'w') == a_white:
                continue
            a_gold_losses += 1
            sign = 1 if a_white else -1
            n = len(g['moves'])
            best = None
            for k in range(max(0, n - args.window), n):
                if (k % 2 == 0) != a_white:      # ply k+1 was played by B
                    continue
                s = g['scores'][k]
                if abs(s) >= MATE:
                    continue
                s = s * sign / 10                # gold, A's point of view
                if best is None or s > best[0]:
                    best = (s, k + 1)
            if best and best[0] >= args.min_gold:
                found.append((best[0], best[1], path, name_a, name_b, g))
        totals.append((path, len(games), a_gold_losses))

    for path, n, losses in totals:
        print(f'{path}: {n} games, A lost {losses} on gold')
    found.sort(key=lambda t: -t[0])
    print(f'{len(found)} gold losses with A at >= +{args.min_gold:g} gold in the last {args.window} plies')
    picked = found[:args.top]
    out = []
    for score, ply, path, name_a, name_b, g in picked:
        a_white = g['a_is_white']
        white, black = (name_a, name_b) if a_white else (name_b, name_a)
        move_no = (ply + 1) // 2
        title = (f'{white} vs {black}: {name_a} lost on gold after thinking +{score:.0f}g '
                 f'at move {move_no} ({"White" if a_white else "Black"})')
        print(f'  {title}  [{len(g["moves"])} plies, game {g["game"]}, {g["seed"]}]')
        out.append({'title': title, 'seed': g['seed'], 'moves': g['moves'], 'result': g['result'],
                    'white': white, 'black': black})
    if args.out:
        json.dump(out, open(args.out, 'w'), indent=1)
        print(f'wrote {len(out)} games to {args.out}')


if __name__ == '__main__':
    main()
