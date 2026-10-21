using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MineHint
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.Run(new ControlForm());
        }
    }

    // ---------- Окно управления ----------
    class ControlForm : Form
    {
        NumericUpDown numCols = new NumericUpDown { Minimum = 4, Maximum = 60, Value = 9 };
        NumericUpDown numRows = new NumericUpDown { Minimum = 4, Maximum = 40, Value = 9 };
        Button btnArea = new Button { Text = "1. Выбрать область поля" };
        Button btnCalib = new Button { Text = "2. Калибровка (поле закрыто)" };
        Button btnRun = new Button { Text = "3. Старт" };
        Label status = new Label { AutoSize = false };

        Rectangle area = Rectangle.Empty;
        Color closedColor = Color.Empty;
        bool running;
        Timer timer = new Timer { Interval = 400 };
        OverlayForm overlay = new OverlayForm();

        public ControlForm()
        {
            Text = "MineHint";
            ClientSize = new Size(300, 250);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            TopMost = true;

            var l1 = new Label { Text = "Столбцов:", Left = 10, Top = 14, Width = 70 };
            numCols.SetBounds(90, 10, 60, 24);
            var l2 = new Label { Text = "Строк:", Left = 160, Top = 14, Width = 50 };
            numRows.SetBounds(215, 10, 60, 24);
            btnArea.SetBounds(10, 50, 280, 32);
            btnCalib.SetBounds(10, 90, 280, 32);
            btnRun.SetBounds(10, 130, 280, 32);
            status.SetBounds(10, 172, 280, 70);
            status.Text = "Сначала укажи размер поля (новичок 9x9, любитель 16x16, эксперт 30x16), " +
                          "затем выбери область.";
            Controls.AddRange(new Control[] { l1, numCols, l2, numRows, btnArea, btnCalib, btnRun, status });

            btnArea.Click += (s, e) => PickArea();
            btnCalib.Click += (s, e) => Calibrate();
            btnRun.Click += (s, e) => ToggleRun();
            timer.Tick += (s, e) => Tick();
            FormClosing += (s, e) => overlay.Close();
        }

        void PickArea()
        {
            Hide();
            using (var sel = new SelectForm())
            {
                if (sel.ShowDialog() == DialogResult.OK) area = sel.Selected;
            }
            Show();
            status.Text = area.IsEmpty ? "Область не выбрана."
                : $"Область: {area.Width}x{area.Height}. Теперь калибровка на закрытом поле.";
        }

        void Calibrate()
        {
            if (area.IsEmpty) { status.Text = "Сначала выбери область."; return; }
            using (var bmp = Capture(area))
            {
                var px = new PixelBuffer(bmp);
                float cw = area.Width / (float)numCols.Value;
                float ch = area.Height / (float)numRows.Value;
                closedColor = RingColor(px, 0, 0, cw, ch);
            }
            status.Text = "Калибровка готова. Жми «Старт» и играй.";
        }

        void ToggleRun()
        {
            if (area.IsEmpty || closedColor.IsEmpty)
            {
                status.Text = "Нужны область и калибровка.";
                return;
            }
            running = !running;
            btnRun.Text = running ? "3. Стоп" : "3. Старт";
            if (running)
            {
                overlay.Setup(area);
                overlay.Show();
                timer.Start();
            }
            else
            {
                timer.Stop();
                overlay.Hide();
            }
        }

        Bitmap Capture(Rectangle r)
        {
            // прячем оверлей, если он не исключается из захвата
            bool hidden = false;
            if (overlay.Visible && !overlay.ExcludedFromCapture) { overlay.Opacity = 0; hidden = true; }
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            if (hidden) overlay.Opacity = 1;
            return bmp;
        }

        void Tick()
        {
            int cols = (int)numCols.Value, rows = (int)numRows.Value;
            int[,] grid = new int[rows, cols];
            float cw = area.Width / (float)cols, ch = area.Height / (float)rows;

            using (var bmp = Capture(area))
            {
                var px = new PixelBuffer(bmp);
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                        grid[r, c] = Classify(px, c, r, cw, ch);
            }

            var res = Solver.Solve(grid, rows, cols);
            overlay.Update(rows, cols, res.Safe, res.Mines);
        }

        // -1 = закрыта, 0..8 = открыта с числом
        int Classify(PixelBuffer px, int c, int r, float cw, float ch)
        {
            Color ring = RingColor(px, c, r, cw, ch);
            if (Dist(ring, closedColor) < 45) return -1;

            // открытая клетка: ищем цвет цифры в центре
            int[] counts = new int[9];
            int x0 = (int)(c * cw + cw * 0.25f), x1 = (int)(c * cw + cw * 0.75f);
            int y0 = (int)(r * ch + ch * 0.25f), y1 = (int)(r * ch + ch * 0.75f);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    Color p = px.Get(x, y);
                    for (int n = 1; n <= 8; n++)
                        if (Dist(p, DigitColors[n]) < 60) counts[n]++;
                }
            int best = 0, bestCount = 3; // минимум 4 пикселя
            for (int n = 1; n <= 8; n++)
                if (counts[n] > bestCount) { bestCount = counts[n]; best = n; }
            return best;
        }

        static readonly Color[] DigitColors =
        {
            Color.Empty,
            Color.FromArgb(0, 0, 255),     // 1
            Color.FromArgb(0, 128, 0),     // 2
            Color.FromArgb(255, 0, 0),     // 3
            Color.FromArgb(0, 0, 128),     // 4
            Color.FromArgb(128, 0, 0),     // 5
            Color.FromArgb(0, 128, 128),   // 6
            Color.FromArgb(0, 0, 0),       // 7
            Color.FromArgb(128, 128, 128), // 8
        };

        static Color RingColor(PixelBuffer px, int c, int r, float cw, float ch)
        {
            long sr = 0, sg = 0, sb = 0; int n = 0;
            int x0 = (int)(c * cw), y0 = (int)(r * ch);
            int w = (int)cw, h = (int)ch;
            for (int i = 2; i < w - 2; i++)
            {
                foreach (int y in new[] { y0 + 2, y0 + h - 3 })
                { var p = px.Get(x0 + i, y); sr += p.R; sg += p.G; sb += p.B; n++; }
            }
            for (int j = 2; j < h - 2; j++)
            {
                foreach (int x in new[] { x0 + 2, x0 + w - 3 })
                { var p = px.Get(x, y0 + j); sr += p.R; sg += p.G; sb += p.B; n++; }
            }
            if (n == 0) return Color.Black;
            return Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
        }

        static double Dist(Color a, Color b)
        {
            int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }
    }

    // ---------- Быстрый доступ к пикселям ----------
    class PixelBuffer
    {
        readonly byte[] data; readonly int w, h, stride;
        public PixelBuffer(Bitmap bmp)
        {
            w = bmp.Width; h = bmp.Height;
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            stride = bd.Stride;
            data = new byte[stride * h];
            Marshal.Copy(bd.Scan0, data, 0, data.Length);
            bmp.UnlockBits(bd);
        }
        public Color Get(int x, int y)
        {
            if (x < 0) x = 0; if (y < 0) y = 0;
            if (x >= w) x = w - 1; if (y >= h) y = h - 1;
            int i = y * stride + x * 4;
            return Color.FromArgb(data[i + 2], data[i + 1], data[i]);
        }
    }

    // ---------- Решатель ----------
    static class Solver
    {
        public class Result { public HashSet<int> Safe = new HashSet<int>(); public HashSet<int> Mines = new HashSet<int>(); }

        public static Result Solve(int[,] g, int R, int C)
        {
            var res = new Result();
            bool changed = true;
            while (changed)
            {
                changed = false;
                var cons = new List<(HashSet<int> unk, int rem)>();

                for (int r = 0; r < R; r++)
                    for (int c = 0; c < C; c++)
                    {
                        int n = g[r, c];
                        if (n <= 0) continue;
                        var unk = new HashSet<int>();
                        int known = 0;
                        for (int dr = -1; dr <= 1; dr++)
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                if (dr == 0 && dc == 0) continue;
                                int rr = r + dr, cc = c + dc;
                                if (rr < 0 || cc < 0 || rr >= R || cc >= C) continue;
                                if (g[rr, cc] != -1) continue;
                                int id = rr * C + cc;
                                if (res.Mines.Contains(id)) known++;
                                else if (!res.Safe.Contains(id)) unk.Add(id);
                            }
                        int rem = n - known;
                        if (unk.Count == 0) continue;
                        if (rem == 0) { foreach (var u in unk) changed |= res.Safe.Add(u); }
                        else if (rem == unk.Count) { foreach (var u in unk) changed |= res.Mines.Add(u); }
                        else cons.Add((unk, rem));
                    }

                // правило подмножеств
                for (int i = 0; i < cons.Count; i++)
                    for (int j = 0; j < cons.Count; j++)
                    {
                        if (i == j) continue;
                        var a = cons[i]; var b = cons[j];
                        if (a.unk.Count >= b.unk.Count || !a.unk.IsSubsetOf(b.unk)) continue;
                        var diff = b.unk.Where(x => !a.unk.Contains(x)).ToList();
                        int rem = b.rem - a.rem;
                        if (rem == 0) foreach (var d in diff) changed |= res.Safe.Add(d);
                        else if (rem == diff.Count) foreach (var d in diff) changed |= res.Mines.Add(d);
                    }
            }
            return res;
        }
    }

    // ---------- Выбор области ----------
    class SelectForm : Form
    {
        Point start; Rectangle rect; bool drag;
        public Rectangle Selected => rect;

        public SelectForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            Bounds = SystemInformation.VirtualScreen;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Opacity = 0.3;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
            MouseDown += (s, e) => { start = e.Location; drag = true; };
            MouseMove += (s, e) =>
            {
                if (!drag) return;
                rect = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y),
                                          Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
                Invalidate();
            };
            MouseUp += (s, e) =>
            {
                drag = false;
                rect.Offset(Bounds.Location);
                DialogResult = rect.Width > 20 && rect.Height > 20 ? DialogResult.OK : DialogResult.Cancel;
            };
            Paint += (s, e) => { if (drag) e.Graphics.DrawRectangle(Pens.Lime, rect); };
        }
    }

    // ---------- Прозрачный оверлей (клики проходят сквозь него) ----------
    class OverlayForm : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        public bool ExcludedFromCapture;
        int rows, cols;
        HashSet<int> safe = new HashSet<int>(), mines = new HashSet<int>();

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { ExcludedFromCapture = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); }
            catch { ExcludedFromCapture = false; }
        }

        public void Setup(Rectangle area) { Bounds = area; }

        public void Update(int r, int c, HashSet<int> s, HashSet<int> m)
        {
            rows = r; cols = c; safe = s; mines = m;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float cw = Width / (float)cols, ch = Height / (float)rows;
            using (var pg = new Pen(Color.Lime, 3))
            using (var pr = new Pen(Color.Red, 3))
            {
                foreach (int id in safe)
                    e.Graphics.DrawRectangle(pg, (id % cols) * cw + 2, (id / cols) * ch + 2, cw - 4, ch - 4);
                foreach (int id in mines)
                    e.Graphics.DrawRectangle(pr, (id % cols) * cw + 2, (id / cols) * ch + 2, cw - 4, ch - 4);
            }
        }
    }
}
