using System.Drawing;

namespace MonitorProfileSwitcher;

/// <summary>
/// "Keep these display settings?" shown on EVERY screen at once, answerable from the keyboard
/// (Enter keeps, Esc reverts), reverting by itself when the countdown runs out. Windows shows
/// its prompt on one screen only, which is easy to lose when that screen is the one changing.
/// </summary>
internal static class KeepChangesPrompt
{
    public static Task<bool> AskAsync(int seconds)
    {
        var completion = new TaskCompletionSource<bool>();
        var forms = new List<Form>();
        var countdowns = new List<Label>();
        var remaining = seconds;
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };

        void Finish(bool keep)
        {
            if (completion.Task.IsCompleted) return;
            timer.Stop();
            timer.Dispose();
            completion.TrySetResult(keep);
            foreach (var form in forms)
            {
                if (!form.IsDisposed) form.Close();
            }
        }

        string CountdownText() => $"Reverting to the previous layout in {remaining} second{(remaining == 1 ? "" : "s")}.";

        foreach (var screen in Screen.AllScreens)
        {
            var form = new Form
            {
                Text = "Display Settings",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                ControlBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                KeyPreview = true,
                StartPosition = FormStartPosition.Manual,
                ClientSize = new Size(460, 150),
            };
            form.Location = new Point(
                screen.Bounds.Left + (screen.Bounds.Width - form.Width) / 2,
                screen.Bounds.Top + (screen.Bounds.Height - form.Height) / 2);

            var title = new Label
            {
                Text = "Keep these display settings?",
                Font = new Font(form.Font.FontFamily, 12f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(18, 16),
            };
            var countdown = new Label { Text = CountdownText(), AutoSize = true, Location = new Point(20, 52) };
            var keep = new Button { Text = "Keep changes (Enter)", Width = 170, Height = 32, Location = new Point(84, 100) };
            var revert = new Button { Text = "Revert (Esc)", Width = 120, Height = 32, Location = new Point(262, 100) };
            keep.Click += (_, _) => Finish(true);
            revert.Click += (_, _) => Finish(false);
            form.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; Finish(true); }
                else if (e.KeyCode == Keys.Escape) { e.Handled = true; Finish(false); }
            };
            form.FormClosing += (_, _) => Finish(false); // Alt+F4 counts as "not kept"
            form.AcceptButton = keep;
            form.CancelButton = revert;
            form.Controls.AddRange([title, countdown, keep, revert]);
            forms.Add(form);
            countdowns.Add(countdown);
        }

        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining <= 0) { Finish(false); return; }
            foreach (var label in countdowns) label.Text = CountdownText();
        };

        foreach (var form in forms)
            form.Show();
        // Give keyboard focus to the prompt on the screen the cursor is on.
        var underCursor = forms.FirstOrDefault(form => Screen.FromControl(form).Bounds.Contains(Cursor.Position)) ?? forms.FirstOrDefault();
        underCursor?.Activate();
        timer.Start();
        return completion.Task;
    }
}

/// <summary>Big numbers on each screen, matching the numbers on the arrangement canvas.</summary>
internal static class IdentifyOverlay
{
    public static void Show(IEnumerable<(string GdiDeviceName, int Number, string Name)> displays)
    {
        var screens = Screen.AllScreens;
        var forms = new List<Form>();
        foreach (var (gdiDeviceName, number, name) in displays)
        {
            var screen = screens.FirstOrDefault(s => s.DeviceName.Equals(gdiDeviceName, StringComparison.OrdinalIgnoreCase));
            if (screen == null) continue;

            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(0, 103, 192),
                ClientSize = new Size(260, 220),
            };
            form.Location = new Point(screen.Bounds.Left + 48, screen.Bounds.Bottom - form.Height - 64);
            form.Controls.Add(new Label
            {
                Text = name,
                ForeColor = Color.White,
                Font = new Font(form.Font.FontFamily, 11f, FontStyle.Bold),
                Dock = DockStyle.Bottom,
                Height = 40,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
            });
            form.Controls.Add(new Label
            {
                Text = number.ToString(),
                ForeColor = Color.White,
                Font = new Font(form.Font.FontFamily, 96f, FontStyle.Bold),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
            });
            forms.Add(form);
            form.Show();
        }

        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            foreach (var form in forms) form.Close();
        };
        timer.Start();
    }
}
