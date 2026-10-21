using System;
using System.Collections.Generic;

namespace MineHint
{
    public class SolveResult
    {
        public HashSet<int> Safe = new HashSet<int>();
        public HashSet<int> Mines = new HashSet<int>();
        public bool Inconsistent;
    }

    public static class Solver
    {
        class Cons { public int[] Cells; public int Rem; }

        // g[r,c]: -1 = закрыта, 0..8 = открытая клетка с числом, -2 = открыта, число неизвестно (пропускается)
        // Возвращает только 100% надёжные выводы. При противоречии (ошибка распознавания)
        // возвращает Inconsistent = true и пустые множества.
        public static SolveResult Solve(int[,] g, int rows, int cols)
        {
            var res = new SolveResult();
            for (int guard = 0; guard < 2000; guard++)
            {
                List<Cons> cons;
                bool changed;
                if (!BuildConstraints(g, rows, cols, res, out cons, out changed)) return Fail();
                if (!changed && !SubsetRule(cons, res, out changed)) return Fail();
                if (!changed && !Enumerate(cons, res, out changed)) return Fail();
                if (res.Safe.Overlaps(res.Mines)) return Fail();
                if (!changed) break;
            }
            return res;
        }

        static SolveResult Fail()
        {
            return new SolveResult { Inconsistent = true };
        }

        static bool BuildConstraints(int[,] g, int rows, int cols, SolveResult res,
                                     out List<Cons> cons, out bool changed)
        {
            cons = new List<Cons>();
            changed = false;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int n = g[r, c];
                    if (n <= 0) continue;
                    var unk = new List<int>();
                    int known = 0;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            if (dr == 0 && dc == 0) continue;
                            int rr = r + dr, cc = c + dc;
                            if (rr < 0 || cc < 0 || rr >= rows || cc >= cols) continue;
                            if (g[rr, cc] != -1) continue;
                            int id = rr * cols + cc;
                            if (res.Mines.Contains(id)) known++;
                            else if (!res.Safe.Contains(id)) unk.Add(id);
                        }
                    int rem = n - known;
                    if (rem < 0 || rem > unk.Count) return false;
                    if (unk.Count == 0) continue;
                    if (rem == 0)
                    {
                        foreach (int u in unk) if (res.Safe.Add(u)) changed = true;
                    }
                    else if (rem == unk.Count)
                    {
                        foreach (int u in unk) if (res.Mines.Add(u)) changed = true;
                    }
                    else cons.Add(new Cons { Cells = unk.ToArray(), Rem = rem });
                }
            return true;
        }

        static bool IsSubset(int[] a, int[] b)
        {
            int j = 0;
            for (int i = 0; i < a.Length; i++)
            {
                while (j < b.Length && b[j] < a[i]) j++;
                if (j >= b.Length || b[j] != a[i]) return false;
                j++;
            }
            return true;
        }

        static bool SubsetRule(List<Cons> cons, SolveResult res, out bool changed)
        {
            changed = false;
            for (int i = 0; i < cons.Count; i++)
                for (int j = 0; j < cons.Count; j++)
                {
                    if (i == j) continue;
                    Cons a = cons[i], b = cons[j];
                    if (a.Cells.Length >= b.Cells.Length) continue;
                    if (!IsSubset(a.Cells, b.Cells)) continue;
                    int rem = b.Rem - a.Rem;
                    int dn = b.Cells.Length - a.Cells.Length;
                    if (rem < 0 || rem > dn) return false;
                    if (rem != 0 && rem != dn) continue;
                    foreach (int x in b.Cells)
                    {
                        if (Array.BinarySearch(a.Cells, x) >= 0) continue;
                        if (rem == 0) { if (res.Safe.Add(x)) changed = true; }
                        else { if (res.Mines.Add(x)) changed = true; }
                    }
                }
            return true;
        }

        // Полный перебор по связным группам ограничений (ловит то, что простые правила не видят)
        class Enumerator
        {
            public int N;
            public int[] Remain, Unassigned, Assign;
            public int[][] CellCons;
            public long Solutions, Nodes;
            public long[] Ones;
            public bool Aborted;
            const long Budget = 400000;

            public void Rec(int idx)
            {
                if (Aborted) return;
                if (++Nodes > Budget) { Aborted = true; return; }
                if (idx == N)
                {
                    Solutions++;
                    for (int i = 0; i < N; i++) if (Assign[i] == 1) Ones[i]++;
                    return;
                }
                int[] cc = CellCons[idx];
                for (int v = 0; v <= 1; v++)
                {
                    bool ok = true;
                    for (int q = 0; q < cc.Length; q++) { Unassigned[cc[q]]--; Remain[cc[q]] -= v; }
                    for (int q = 0; q < cc.Length; q++)
                    {
                        int k = cc[q];
                        if (Remain[k] < 0 || Remain[k] > Unassigned[k]) { ok = false; break; }
                    }
                    if (ok) { Assign[idx] = v; Rec(idx + 1); }
                    for (int q = 0; q < cc.Length; q++) { Unassigned[cc[q]]++; Remain[cc[q]] += v; }
                    if (Aborted) return;
                }
            }
        }

        static bool Enumerate(List<Cons> cons, SolveResult res, out bool changed)
        {
            changed = false;
            if (cons.Count == 0) return true;

            var cellToCons = new Dictionary<int, List<int>>();
            for (int i = 0; i < cons.Count; i++)
                foreach (int cell in cons[i].Cells)
                {
                    List<int> l;
                    if (!cellToCons.TryGetValue(cell, out l)) { l = new List<int>(); cellToCons[cell] = l; }
                    l.Add(i);
                }

            var visited = new bool[cons.Count];
            for (int s = 0; s < cons.Count; s++)
            {
                if (visited[s]) continue;
                var compCons = new List<int>();
                var compCells = new List<int>();
                var seen = new HashSet<int>();
                var queue = new Queue<int>();
                queue.Enqueue(s); visited[s] = true;
                while (queue.Count > 0)
                {
                    int ci = queue.Dequeue();
                    compCons.Add(ci);
                    foreach (int cell in cons[ci].Cells)
                    {
                        if (seen.Add(cell)) compCells.Add(cell);
                        foreach (int nj in cellToCons[cell])
                            if (!visited[nj]) { visited[nj] = true; queue.Enqueue(nj); }
                    }
                }
                if (compCells.Count > 32) continue;

                var localIdx = new Dictionary<int, int>();
                for (int i = 0; i < compCells.Count; i++) localIdx[compCells[i]] = i;
                var cellCons = new List<int>[compCells.Count];
                for (int i = 0; i < cellCons.Length; i++) cellCons[i] = new List<int>();
                var remain = new int[compCons.Count];
                var unassigned = new int[compCons.Count];
                for (int k = 0; k < compCons.Count; k++)
                {
                    Cons cn = cons[compCons[k]];
                    remain[k] = cn.Rem;
                    unassigned[k] = cn.Cells.Length;
                    foreach (int cell in cn.Cells) cellCons[localIdx[cell]].Add(k);
                }

                var en = new Enumerator();
                en.N = compCells.Count;
                en.Remain = remain; en.Unassigned = unassigned;
                en.Assign = new int[en.N];
                en.Ones = new long[en.N];
                en.CellCons = new int[en.N][];
                for (int i = 0; i < en.N; i++) en.CellCons[i] = cellCons[i].ToArray();
                en.Rec(0);
                if (en.Aborted) continue;
                if (en.Solutions == 0) return false;
                for (int i = 0; i < en.N; i++)
                {
                    if (en.Ones[i] == 0) { if (res.Safe.Add(compCells[i])) changed = true; }
                    else if (en.Ones[i] == en.Solutions) { if (res.Mines.Add(compCells[i])) changed = true; }
                }
            }
            return true;
        }
    }
}
