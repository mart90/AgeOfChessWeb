"""Compare two `gg selfplay --out` runs (e.g. standard rules vs a rule variant on the same seeds).

    python engine/tools/compare_selfplay.py engine/testdata/qd_base.jsonl engine/testdata/qd_var.jsonl
"""
import json
import statistics as st
import sys

KINDS = ['Q', 'R', 'B', 'N', 'P']


def load(path):
    return [json.loads(l) for l in open(path, encoding='utf-8') if l.strip()]


def pct(n, d):
    return f'{100 * n / d:5.1f}%' if d else '   - '


def summary(games):
    n = len(games)
    res = [g['result'] for g in games]
    out = {}
    out['games'] = n
    out['white wins'] = pct(sum(r.startswith('w') for r in res), n)
    out['black wins'] = pct(sum(r.startswith('b') for r in res), n)
    out['draws (ply cap)'] = pct(sum(r == 'draw' for r in res), n)
    for code, name in [('c', 'checkmate'), ('g', 'gold victory'), ('s', 'stalemate')]:
        out[f'  by {name}'] = pct(sum(r.endswith('+' + code) for r in res), n)
    plies = [g['plies'] for g in games]
    out['plies: mean'] = f'{st.mean(plies):.0f}'
    out['plies: median'] = f'{st.median(plies):.0f}'
    out['plies: 10th / 90th pct'] = f'{sorted(plies)[n // 10]} / {sorted(plies)[n * 9 // 10]}'
    bk = [[sum(g['bought_by_kind'][c][i] for c in range(2)) for g in games] for i in range(5)]
    out['pieces bought / game'] = f"{st.mean(sum(g['pieces_bought']) for g in games):.1f}"
    for i, k in enumerate(KINDS):
        out[f'  {k} / game'] = f'{st.mean(bk[i]):.2f}'
    out['games with a queen bought'] = pct(sum(1 for g in games if g['bought_by_kind'][0][0] + g['bought_by_kind'][1][0] > 0), n)
    fq = [p for g in games for p in g['first_queen_ply'] if p > 0]
    out['first queen: median ply'] = f'{st.median(fq):.0f}' if fq else '-'
    out['queens lost / game'] = f"{st.mean(sum(g['queens_lost']) for g in games):.2f}"
    qd = sum(sum(g['queen_drops']) for g in games)
    nonpawn = sum(bk[i][j] for i in range(4) for j in range(n))
    out['queen-adjacent drops / game'] = f'{qd / n:.2f}'
    out['  share of R/B/N/Q buys'] = pct(qd, nonpawn)
    out['checks / game'] = f"{st.mean(sum(g['checks']) for g in games):.1f}"
    out['captures / game'] = f"{st.mean(sum(g['captures']) for g in games):.1f}"
    # captures per 100 plies: a rough "how much fighting" measure independent of game length
    out['captures / 100 plies'] = f"{100 * sum(sum(g['captures']) for g in games) / sum(plies):.1f}"
    out['checks / 100 plies'] = f"{100 * sum(sum(g['checks']) for g in games) / sum(plies):.1f}"
    out['final gold (winner side avg)'] = f"{st.mean(max(g['final_gold']) for g in games):.0f}"
    out['mines owned at end (both)'] = f"{st.mean(sum(g['final_mines']) for g in games):.1f}"
    # Games decided by checkmate before anyone got close to the gold threshold: attacks that worked
    early_mates = sum(1 for g in games if g['result'].endswith('+c') and max(g['final_gold']) < 120)
    out['checkmates with < 120 gold on both sides'] = pct(early_mates, n)
    return out


def main(a_path, b_path):
    a, b = load(a_path), load(b_path)
    sa, sb = summary(a), summary(b)
    w = max(len(k) for k in sa)
    print(f"{'':{w}}  {'A: ' + a_path.split('/')[-1]:>22}  {'B: ' + b_path.split('/')[-1]:>22}")
    for k in sa:
        print(f'{k:{w}}  {sa[k]:>22}  {sb[k]:>22}')
    # Paired by seed: same board under both rule sets
    by_seed = {g['seed']: g for g in a}
    same = changed = 0
    for g in b:
        o = by_seed.get(g['seed'])
        if o:
            if o['result'][0] == g['result'][0]:
                same += 1
            else:
                changed += 1
    print(f'\npaired boards: winner unchanged {same}, changed {changed}')


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
