using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace MineHint
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
#if !MCSCHECK
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
#endif
            Application.EnableVisualStyles();
            Application.Run(new ControlForm());
        }
    }

    static class Native
    {
        [DllImport("user32.dll")]
        static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        // Windows 10 2004+: окно не попадает в скриншоты (оверлей не мешает распознаванию)
        public static void ExcludeFromCapture(IntPtr handle)
        {
            try { SetWindowDisplayAffinity(handle, 0x11); } catch { }
        }
    }

    // ---------- Главное окно ----------
    class ControlForm : Form
    {
        NumericUpDown numCols = new NumericUpDown();
        NumericUpDown numRows = new NumericUpDown();
        Button btnPick = new Button();
        Button btnRun = new Button();
        Label status = new Label();

        Timer timer = new Timer();
        OverlayForm overlay = new OverlayForm();

        Rectangle area = Rectangle.Empty;
        BoardModel model;
        int modelCols, modelRows;
        bool running;
        bool overlayInCapture;      // true - оверлей виден в снимках, прячем его на время съёмки

        Bitmap capBmp; Graphics capG;
        byte[] capBuf, prevBuf;
        int[,] candidate;
        bool dirty;
        string lastError;

        public ControlForm()
        {
            Text = "MineHint";
            ClientSize = new Size(320, 270);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            TopMost = true;

            numCols.Minimum = 4; numCols.Maximum = 60; numCols.Value = 9;
            numRows.Minimum = 4; numRows.Maximum = 40; numRows.Value = 9;

            var l1 = new Label(); l1.Text = "Столбцов:"; l1.SetBounds(10, 14, 70, 20);
            numCols.SetBounds(85, 10, 60, 24);
            var l2 = new Label(); l2.Text = "Строк:"; l2.SetBounds(165, 14, 50, 20);
            numRows.SetBounds(220, 10, 60, 24);

            btnPick.Text = "1. Указать поле (2 клика)";
            btnPick.SetBounds(10, 48, 300, 36);
            btnRun.Text = "2. Старт";
            btnRun.SetBounds(10, 92, 300, 36);

            status.SetBounds(10, 140, 300, 120);
            status.Text = "Размер поля: новичок 9x9, любитель 16x16, эксперт 30 столбцов x 16 строк.\r\n\r\n" +
                          "Начни НОВУЮ игру (все клетки закрыты), нажми кнопку 1 и кликни в левую верхнюю клетку, " +
                          "а потом в правую нижнюю.";

            Controls.Add(l1); Controls.Add(numCols); Controls.Add(l2); Controls.Add(numRows);
            Controls.Add(btnPick); Controls.Add(btnRun); Controls.Add(status);

            timer.Interval = 250;
            timer.Tick += delegate { Tick(); };
            btnPick.Click += delegate { PickAndCalibrate(); };
            btnRun.Click += delegate { ToggleRun(); };
            FormClosing += delegate { timer.Stop(); overlay.Close(); };
        }

        void Fail(string text)
        {
            lastError = text;
            status.Text = text;
            MessageBox.Show(this, text, "MineHint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---------- шаг 1: клики + калибровка ----------
        void PickAndCalibrate()
        {
            if (running) ToggleRun();
            int cols = (int)numCols.Value, rows = (int)numRows.Value;

            Point p1, p2;
            Hide();
            using (var pick = new PointPickForm())
            {
                if (pick.ShowDialog() != DialogResult.OK) { Show(); return; }
                p1 = pick.P1; p2 = pick.P2;
            }
            Application.DoEvents();
            Thread.Sleep(350);          // дать экрану перерисоваться без затемнения

            int ax, ay, aw, ah;
            double pX, pY;
            if (!BoardModel.AreaFromClicks(p1.X, p1.Y, p2.X, p2.Y, cols, rows, out ax, out ay, out aw, out ah, out pX, out pY))
            {
                Show();
                Fail("Размер клеток получился странным (" + pX.ToString("F1") + " x " + pY.ToString("F1") +
                     " px). Проверь число столбцов/строк в окне и кликни сначала в левую верхнюю клетку, " +
                     "потом в правую нижнюю.");
                return;
            }
            area = new Rectangle(ax, ay, aw, ah);

            Frame f;
            try { f = Grab(); }
            catch (Exception ex) { Show(); Fail("Не удалось сделать снимок экрана: " + ex.Message); return; }

            string err;
            BoardModel m = BoardModel.Calibrate(f, cols, rows, pX, pY, out err);
            Show();
            if (m == null)
            {
                model = null;
                string saved = "";
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "minehint_debug.png");
                    capBmp.Save(path, ImageFormat.Png);
                    saved = "\r\n\r\nСнимок области сохранён на рабочий стол: minehint_debug.png";
                }
                catch { }
                Fail(err + "\r\n\r\nОбласть: " + area.Width + "x" + area.Height + " px, клетка по кликам: " +
                     pX.ToString("F1") + " x " + pY.ToString("F1") + " px, поле " + cols + "x" + rows + "." + saved);
                return;
            }

            lastError = null;
            model = m; modelCols = cols; modelRows = rows;
            status.Text = "Готово: клетка " + m.CellW + "x" + m.CellH + " px, совпадение " + (int)(m.Match * 100) + "%." +
                          (m.Match < 0.9 ? "\r\nПохоже, не все клетки были закрыты. Если так, начни новую игру и повтори шаг 1." : "") +
                          "\r\nТеперь жми «Старт».";
        }

        // ---------- шаг 2: старт / стоп ----------
        void ToggleRun()
        {
            if (running)
            {
                running = false;
                timer.Stop();
                overlay.Hide();
                btnRun.Text = "2. Старт";
                return;
            }
            if (model == null)
            {
                status.Text = lastError != null
                    ? "Шаг 1 не удался, причина:\r\n" + lastError
                    : "Сначала выполни шаг 1.";
                return;
            }
            if ((int)numCols.Value != modelCols || (int)numRows.Value != modelRows)
            { status.Text = "Размер поля изменился, повтори шаг 1."; return; }
            if (Bounds.IntersectsWith(area))
            { status.Text = "Окно MineHint закрывает поле. Передвинь его в сторону и снова жми «Старт»."; return; }

            overlay.Setup(area, model);
            overlay.ShowTest = true;
            overlay.Show();
            overlay.Refresh();
            Application.DoEvents();
            Thread.Sleep(200);
            try
            {
                Frame f = Grab();
                int cx = model.CellX(0) + model.CellW / 2, cy = model.CellY(0) + model.CellH / 2;
                overlayInCapture = Col.Sq(f.Get(cx, cy), OverlayForm.SafeColor.ToArgb() & 0xFFFFFF) < 900;
            }
            catch { overlayInCapture = true; }
            overlay.ShowTest = false;
            overlay.SetMarks(new HashSet<int>(), new HashSet<int>());

            prevBuf = null; candidate = null; dirty = false;
            running = true;
            btnRun.Text = "2. Стоп";
            status.Text = "Работает. Зелёные квадраты - безопасные клетки, красные - мины." +
                          (overlayInCapture ? "\r\n(режим совместимости: подсветка слегка мигает)" : "");
            timer.Start();
        }

        // ---------- снимок экрана ----------
        Frame Grab()
        {
            int w = area.Width, h = area.Height;
            if (capBmp == null || capBmp.Width != w || capBmp.Height != h)
            {
                if (capG != null) capG.Dispose();
                if (capBmp != null) capBmp.Dispose();
                capBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                capG = Graphics.FromImage(capBmp);
                capBuf = new byte[w * h * 4];
                prevBuf = null;
            }
            capG.CopyFromScreen(area.Location, Point.Empty, area.Size);
            BitmapData bd = capBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (bd.Stride == w * 4) Marshal.Copy(bd.Scan0, capBuf, 0, capBuf.Length);
                else for (int y = 0; y < h; y++) Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), capBuf, y * w * 4, w * 4);
            }
            finally { capBmp.UnlockBits(bd); }
            return new Frame(w, h, capBuf);
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int n = a.Length / 8 * 8;
            for (int i = 0; i < n; i += 8)
                if (BitConverter.ToInt64(a, i) != BitConverter.ToInt64(b, i)) return false;
            for (int i = n; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---------- основной цикл ----------
        void Tick()
        {
            Frame f;
            try
            {
                if (overlayInCapture) { overlay.Opacity = 0; Thread.Sleep(40); }
                f = Grab();
            }
            catch { return; }
            finally { if (overlayInCapture) overlay.Opacity = 1; }

            // кадр изменился - распознаём заново и ждём, пока картинка устоится (анимации, нажатия мыши)
            if (prevBuf == null || !SameBytes(capBuf, prevBuf))
            {
                if (prevBuf == null) prevBuf = new byte[capBuf.Length];
                Buffer.BlockCopy(capBuf, 0, prevBuf, 0, capBuf.Length);
                candidate = model.ReadGrid(f);
                dirty = true;
                return;
            }
            if (!dirty) return;         // ничего нового
            dirty = false;

            int open = 0;
            for (int r = 0; r < modelRows; r++)
                for (int c = 0; c < modelCols; c++) if (candidate[r, c] >= 0) open++;
            if (open == 0)
            {
                overlay.SetMarks(new HashSet<int>(), new HashSet<int>());
                status.Text = "Поле закрыто. Открой любую клетку.";
                return;
            }

            SolveResult res = Solver.Solve(candidate, modelRows, modelCols);
            if (res.Inconsistent)
            {
                overlay.SetMarks(new HashSet<int>(), new HashSet<int>());
                status.Text = "Не могу надёжно прочитать поле, подсветка отключена. Возможные причины: " +
                              "игра закончена, окно перекрыто, другие цвета цифр или сдвинулось окно игры " +
                              "(тогда повтори шаг 1).";
                return;
            }
            overlay.SetMarks(res.Safe, res.Mines);
            if (res.Safe.Count == 0 && res.Mines.Count == 0)
                status.Text = "Надёжных ходов нет, нужно угадывать.";
            else
                status.Text = "Безопасных клеток (зелёные): " + res.Safe.Count + "\r\nМин (красные): " + res.Mines.Count;
        }
    }

    // ---------- два клика: левая верхняя и правая нижняя клетки ----------
    class PointPickForm : Form
    {
        public Point P1, P2;
        int clicks;

        public PointPickForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            Opacity = 0.35;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
            MouseDown += delegate (object s, MouseEventArgs e)
            {
                Point sp = PointToScreen(e.Location);
                if (clicks == 0) { P1 = sp; clicks = 1; Invalidate(); }
                else
                {
                    P2 = sp;
                    if (P2.X <= P1.X || P2.Y <= P1.Y) { clicks = 0; Invalidate(); return; }
                    DialogResult = DialogResult.OK;
                }
            };
            Paint += delegate (object s, PaintEventArgs e)
            {
                string msg = clicks == 0
                    ? "Кликни в ЛЕВУЮ ВЕРХНЮЮ клетку поля  (Esc - отмена)"
                    : "Теперь кликни в ПРАВУЮ НИЖНЮЮ клетку поля";
                using (var font = new Font("Segoe UI", 20, FontStyle.Bold))
                {
                    SizeF sz = e.Graphics.MeasureString(msg, font);
                    e.Graphics.DrawString(msg, font, Brushes.White, (ClientSize.Width - sz.Width) / 2, 60);
                }
                if (clicks == 1)
                {
                    Point c = PointToClient(P1);
                    e.Graphics.FillEllipse(Brushes.Lime, c.X - 5, c.Y - 5, 10, 10);
                }
            };
        }
    }

    // ---------- прозрачный оверлей: клики проходят сквозь него ----------
    class OverlayForm : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        public static readonly Color SafeColor = Color.FromArgb(0, 255, 0);
        public static readonly Color MineColor = Color.FromArgb(255, 60, 60);

        public bool ShowTest;
        BoardModel model;
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
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.ExcludeFromCapture(Handle);
        }

        public void Setup(Rectangle area, BoardModel m)
        {
            model = m;
            Bounds = area;
        }

        public void SetMarks(HashSet<int> s, HashSet<int> m)
        {
            safe = s; mines = m;
            Invalidate();
        }

        void Mark(Graphics g, Brush b, int id)
        {
            int r = id / model.Cols, c = id % model.Cols;
            int sx = model.CellX(c), sy = model.CellY(r);
            int w = model.CellW, h = model.CellH;
            g.FillRectangle(b, sx + w / 4, sy + h / 4, Math.Max(4, w / 2), Math.Max(4, h / 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (model == null) return;
            using (var bs = new SolidBrush(SafeColor))
            using (var bm = new SolidBrush(MineColor))
            {
                foreach (int id in safe) Mark(e.Graphics, bs, id);
                foreach (int id in mines) Mark(e.Graphics, bm, id);
                if (ShowTest) Mark(e.Graphics, bs, 0);
            }
        }
    }
}
