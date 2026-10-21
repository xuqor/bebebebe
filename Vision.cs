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
        // hintOx/hintOy - примерное положение левого верхнего угла поля в кадре, hintPx/hintPy - примерный размер клетки
        public static BoardModel Calibrate(Frame f, int cols, int rows,
                                           double hintOx, double hintOy, double hintPx, double hintPy,
                                           out string error)
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
            Fit(colE, W, cols, hintPx, hintOx, out px, out ox);
            Fit(rowE, H, rows, hintPy, hintOy, out py, out oy);

            var m = new BoardModel();
            m.Cols = cols; m.Rows = rows;
            m.PitchX = px; m.OriginX = ox; m.PitchY = py; m.OriginY = oy;
            m.CellW = (int)Math.Floor(px + 1e-6);
            m.CellH = (int)Math.Floor(py + 1e-6);
            m.FrameW = W; m.FrameH = H;
            if (m.CellW < 8 || m.CellH < 8)
            {
                error = "Клетки слишком маленькие, проверь, что кликнул в центры клеток.";
                return null;
            }
            if (m.CellX(0) < 0 || m.CellY(0) < 0 ||
                m.CellX(cols - 1) + m.CellW > W || m.CellY(rows - 1) + m.CellH > H)
            {
                error = "Поле не помещается в область. Проверь число столбцов/строк и клики по центрам угловых клеток.";
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
                error = "Поле не похоже на полностью закрытое или область выбрана неточно. " +
                        "Начни новую игру, не открывай клетки и выдели поле плотнее по краям.";
                return null;
            }
            return m;
        }

        static void Fit(double[] E, int len, int n, double hintP, double hintO,
                        out double bestPitch, out double bestOrigin)
        {
            double best = double.MinValue;
            bestPitch = hintP; bestOrigin = hintO;
            double pSpan = Math.Max(0.5, 7.0 / Math.Max(1, n - 1));
            double oSpan = 6.0;
            for (double p = hintP - pSpan; p <= hintP + pSpan + 1e-9; p += 0.05)
            {
                if (p < 6) continue;
                for (double o = hintO - oSpan; o <= hintO + oSpan + 1e-9; o += 0.5)
                {
                    double sum = 0;
                    for (int k = 0; k < n; k++)
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
