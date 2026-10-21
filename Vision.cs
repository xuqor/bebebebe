using System;
using System.Collections.Generic;

namespace MineHint
{
    // Снимок экрана: BGRA, stride = W * 4
    public class Frame
    {
        public readonly int W, H;
        public readonly byte[] Data;
        public Frame(int w, int h, byte[] data) { W = w; H = h; Data = data; }

        // Цвет пикселя как 0xRRGGBB (за границами берётся ближайший пиксель)
        public int Get(int x, int y)
        {
            if (x < 0) x = 0; else if (x >= W) x = W - 1;
            if (y < 0) y = 0; else if (y >= H) y = H - 1;
            int i = (y * W + x) * 4;
            return (Data[i + 2] << 16) | (Data[i + 1] << 8) | Data[i];
        }
    }

    static class Col
    {
        public static int Sq(int a, int b)
        {
            int dr = ((a >> 16) & 255) - ((b >> 16) & 255);
            int dg = ((a >> 8) & 255) - ((b >> 8) & 255);
            int db = (a & 255) - (b & 255);
            return dr * dr + dg * dg + db * db;
        }
        public static int Abs(int a, int b)
        {
            return Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255))
                 + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255))
                 + Math.Abs((a & 255) - (b & 255));
        }
    }

    // Геометрия поля + эталоны закрытых клеток. Создаётся калибровкой на полностью закрытом поле.
    public class BoardModel
    {
        public int Cols, Rows;
        public double OriginX, OriginY, PitchX, PitchY;
        public int CellW, CellH;
        public double Match;               // 0..1, насколько однородно закрытое поле (проверка калибровки)
        public int FrameW, FrameH;         // размер области, на которой калибровали

        int[] bandDx, bandDy;
        int[][] templates;

        // цвета цифр классического сапёра (1..8)
        static readonly int[] Palette =
        {
            0,
            0x0000FF, // 1 синий
            0x008000, // 2 зелёный
            0xFF0000, // 3 красный
            0x000080, // 4 тёмно-синий
            0x800000, // 5 бордовый
            0x008080, // 6 бирюзовый
            0x000000, // 7 чёрный
            0x808080, // 8 серый
        };

        static int Rnd(double v) { return (int)Math.Floor(v + 0.5); }

        public int CellX(int c) { return Rnd(OriginX + c * PitchX); }
        public int CellY(int r) { return Rnd(OriginY + r * PitchY); }

        // ---------- калибровка ----------
        // Область по двум кликам (левая верхняя и правая нижняя клетки; можно кликать куда угодно в них).
        // Берём запас вокруг, чтобы поле точно поместилось.
        public static bool AreaFromClicks(int x1, int y1, int x2, int y2, int cols, int rows,
                                          out int ax, out int ay, out int aw, out int ah,
                                          out double pEstX, out double pEstY)
        {
            pEstX = (x2 - x1) / (double)cols;
            pEstY = (y2 - y1) / (double)rows;
            int mx = (int)(Math.Abs(pEstX) * 1.5) + 12, my = (int)(Math.Abs(pEstY) * 1.5) + 12;
            ax = x1 - mx; ay = y1 - my;
            aw = (x2 - x1) + 2 * mx; ah = (y2 - y1) + 2 * my;
            return pEstX >= 6 && pEstY >= 6 && pEstX <= 80 && pEstY <= 80;
        }

        // pEstX/pEstY - грубая оценка размера клетки (точную находим сами по границам клеток)
        public static BoardModel Calibrate(Frame f, int cols, int rows, double pEstX, double pEstY, out string error)
        {
            error = null;
            int W = f.W, H = f.H;
            var lum = new int[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int p = f.Get(x, y);
                    lum[y * W + x] = (((p >> 16) & 255) * 30 + ((p >> 8) & 255) * 59 + (p & 255) * 11) / 100;
                }

            // энергия границ по столбцам и строкам: у закрытого поля она периодична
            var colE = new double[W];
            var rowE = new double[H];
            for (int y = 1; y < H; y++)
                for (int x = 1; x < W; x++)
                {
                    int i = y * W + x;
                    colE[x] += Math.Abs(lum[i] - lum[i - 1]);
                    rowE[y] += Math.Abs(lum[i] - lum[i - W]);
                }

            double px, ox, py, oy;
            Fit(colE, W, cols, pEstX, out px, out ox);
            Fit(rowE, H, rows, pEstY, out py, out oy);

            var m = new BoardModel();
            m.Cols = cols; m.Rows = rows;
            m.PitchX = px; m.OriginX = ox; m.PitchY = py; m.OriginY = oy;
            m.CellW = (int)Math.Floor(px + 1e-6);
            m.CellH = (int)Math.Floor(py + 1e-6);
            m.FrameW = W; m.FrameH = H;
            if (m.CellW < 8 || m.CellH < 8)
            {
                error = "Клетки слишком маленькие. Проверь число столбцов и строк в окне.";
                return null;
            }
            if (m.CellX(0) < 0 || m.CellY(0) < 0 ||
                m.CellX(cols - 1) + m.CellW > W || m.CellY(rows - 1) + m.CellH > H)
            {
                error = "Сетка не поместилась в область. Проверь число столбцов и строк и кликни в левую верхнюю и правую нижнюю клетки.";
                return null;
            }

            // «рамка» клетки: всё, что вне центрального квадрата (там цифры/флаги)
            var bx = new List<int>(); var by = new List<int>();
            int loX = (int)(m.CellW * 0.3), hiX = m.CellW - loX;
            int loY = (int)(m.CellH * 0.3), hiY = m.CellH - loY;
            for (int dy = 0; dy < m.CellH; dy++)
                for (int dx = 0; dx < m.CellW; dx++)
                {
                    bool center = dx >= loX && dx < hiX && dy >= loY && dy < hiY;
                    if (!center) { bx.Add(dx); by.Add(dy); }
                }
            m.bandDx = bx.ToArray(); m.bandDy = by.ToArray();

            // эталон для каждой клетки (свой на каждую позицию - нет проблем с округлением)
            m.templates = new int[rows * cols][];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int sx = m.CellX(c), sy = m.CellY(r);
                    var t = new int[m.bandDx.Length];
                    for (int k = 0; k < t.Length; k++) t[k] = f.Get(sx + m.bandDx[k], sy + m.bandDy[k]);
                    m.templates[r * cols + c] = t;
                }

            // проверка: все закрытые клетки одной чётности должны быть похожи друг на друга
            int ok = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int[] proto = m.templates[(r & 1) * cols + (c & 1)];
                    if (m.BandMatches(m.templates[r * cols + c], proto)) ok++;
                }
            m.Match = ok / (double)(rows * cols);
            if (m.Match < 0.5)
            {
                error = "Поле не похоже на полностью закрытое (совпадение клеток " + (int)(m.Match * 100) +
                        "%, клетка " + m.CellW + "x" + m.CellH + " px). Начни новую игру, не открывай клетки " +
                        "и кликни в левую верхнюю и правую нижнюю клетки.";
                return null;
            }
            return m;
        }

        // Ищем размер клетки и положение сетки: сумма «энергии границ» в узлах сетки должна быть максимальной.
        // Берём n+1 узлов (обе внешние границы поля), так сетку не сдвинуть на целую клетку.
        static void Fit(double[] E, int len, int n, double pEst, out double bestPitch, out double bestOrigin)
        {
            double best = double.MinValue;
            bestPitch = pEst; bestOrigin = 0;
            for (double p = pEst * 0.65; p <= pEst * 1.6 + 1e-9; p += 0.05)
            {
                if (p < 6) continue;
                double oMax = len - 2 - n * p;      // всё поле должно поместиться в кадр
                if (oMax < 1) continue;
                for (double o = 0; o <= oMax + 1e-9; o += 0.5)
                {
                    double sum = 0;
                    for (int k = 0; k <= n; k++)
                    {
                        int x = Rnd(o + k * p);
                        if (x < 1 || x >= len) continue;
                        sum += E[x];
                    }
                    // при равенстве предпочитаем «круглые» значения (обычно клетка = целое число пикселей)
                    sum -= 1e-3 * Math.Abs(p - Math.Round(p)) + 1e-4 * Math.Abs(o - Math.Round(o));
                    if (sum > best) { best = sum; bestPitch = p; bestOrigin = o; }
                }
            }
            double rp = Math.Round(bestPitch);
            if (Math.Abs(bestPitch - rp) < 0.04) bestPitch = rp;
        }

        bool BandMatches(int[] a, int[] b)
        {
            int diff = 0;
            for (int k = 0; k < a.Length; k++)
                if (Col.Abs(a[k], b[k]) > 60) diff++;
            return diff * 100 <= a.Length * 12;
        }

        // ---------- распознавание ----------
        // -1 закрыта, 0 открыта пустая, 1..8 число
        int Classify(Frame f, int r, int c)
        {
            int sx = CellX(c), sy = CellY(r);
            int[] t = templates[r * Cols + c];
            int diff = 0;
            for (int k = 0; k < t.Length; k++)
                if (Col.Abs(f.Get(sx + bandDx[k], sy + bandDy[k]), t[k]) > 60) diff++;
            if (diff * 100 <= t.Length * 12) return -1;

            var counts = new int[9];
            int x0 = sx + (int)(CellW * 0.2), x1 = sx + (int)(CellW * 0.8);
            int y0 = sy + (int)(CellH * 0.2), y1 = sy + (int)(CellH * 0.8);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int p = f.Get(x, y);
                    for (int n = 1; n <= 8; n++)
                        if (Col.Sq(p, Palette[n]) <= 2304) { counts[n]++; break; }
                }
            int best = 0, bestCount = 2;
            for (int n = 1; n <= 8; n++)
                if (counts[n] > bestCount) { bestCount = counts[n]; best = n; }
            return best;
        }

        public int[,] ReadGrid(Frame f)
        {
            var g = new int[Rows, Cols];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    g[r, c] = Classify(f, r, c);

            // Настоящий «0» никогда не граничит с закрытой клеткой (он раскрывает соседей сам).
            // Такая «пустая» клетка - это нажатая/подсвеченная закрытая, считаем её закрытой.
            var fixedG = (int[,])g.Clone();
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    if (g[r, c] != 0) continue;
                    for (int dr = -1; dr <= 1; dr++)
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            int rr = r + dr, cc = c + dc;
                            if (rr < 0 || cc < 0 || rr >= Rows || cc >= Cols) continue;
                            if (g[rr, cc] == -1) fixedG[r, c] = -1;
                        }
                }
            return fixedG;
        }
    }
}
