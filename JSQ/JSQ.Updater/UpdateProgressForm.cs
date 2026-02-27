using System;
using System.Drawing;
using System.Windows.Forms;

namespace JSQ.Updater;

/// <summary>
/// Маленькое splash-окно, которое показывается пока апдейтер распаковывает
/// и копирует файлы. Не имеет кнопки закрытия — закрывается автоматически
/// после завершения (успех или ошибка).
/// </summary>
internal sealed class UpdateProgressForm : Form
{
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;

    public UpdateProgressForm(string version)
    {
        var ver = string.IsNullOrWhiteSpace(version) ? string.Empty : $"  {version}";

        // ── Свойства окна ────────────────────────────────────────────────────
        Text             = $"JSQ — Обновление{ver}";
        ClientSize       = new Size(420, 130);
        FormBorderStyle  = FormBorderStyle.FixedSingle;
        StartPosition    = FormStartPosition.CenterScreen;
        MaximizeBox      = false;
        MinimizeBox      = false;
        ControlBox       = false;   // убираем ×, чтобы пользователь не закрыл до конца
        BackColor        = Color.FromArgb(28, 28, 40);

        // ── Заголовок ────────────────────────────────────────────────────────
        var title = new Label
        {
            Text      = $"Установка обновления{ver}",
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(235, 235, 255),
            AutoSize  = false,
            Size      = new Size(380, 28),
            Location  = new Point(20, 14),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };

        // ── Строка текущего шага ─────────────────────────────────────────────
        _statusLabel = new Label
        {
            Text      = "Инициализация...",
            Font      = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(155, 155, 190),
            AutoSize  = false,
            Size      = new Size(380, 20),
            Location  = new Point(20, 52),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent
        };

        // ── Прогресс-бар ─────────────────────────────────────────────────────
        _progressBar = new ProgressBar
        {
            Minimum  = 0,
            Maximum  = 100,
            Value    = 0,
            Style    = ProgressBarStyle.Continuous,
            Size     = new Size(380, 20),
            Location = new Point(20, 84)
        };

        Controls.Add(title);
        Controls.Add(_statusLabel);
        Controls.Add(_progressBar);
    }

    /// <summary>
    /// Обновить текст статуса и значение прогресс-бара.
    /// Можно вызывать из любого потока.
    /// </summary>
    /// <param name="message">Текст текущего шага.</param>
    /// <param name="progress">0–100, или -1 чтобы не менять значение бара.</param>
    public void SetStatus(string message, int progress = -1)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(message, progress)));
            return;
        }

        _statusLabel.Text = message;

        if (progress >= 0)
            _progressBar.Value = Math.Min(Math.Max(progress, 0), _progressBar.Maximum);
    }
}
