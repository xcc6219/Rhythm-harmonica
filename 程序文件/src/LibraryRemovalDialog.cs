using System.Drawing;
using System.Windows.Forms;

namespace HarmonicaStudio
{
    internal sealed class LibraryRemovalDialog : Form
    {
        private readonly CheckBox removeFiles;
        internal bool RemoveFiles { get { return removeFiles.Checked; } }
        internal LibraryRemovalDialog(SongEntry song)
        {
            Text = "删除曲目"; ClientSize = new Size(490, 214); AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            Font = Theme.PixelFont(10); BackColor = Theme.Background; ForeColor = Theme.Text;
            Controls.Add(new Label { Text = "从我的曲库删除：" + song.Title, Location = new Point(22, 20),
                Size = new Size(446, 52), AutoEllipsis = true });
            removeFiles = new CheckBox { Text = "同时删除软件曲目文件夹中的音频和 MIDI", Location = new Point(22, 82),
                Size = new Size(446, 28), Checked = false };
            Controls.Add(removeFiles);
            Controls.Add(new Label { Text = song.Builtin ? "内置曲目可通过曲库右键菜单恢复。" : "保留其他曲目共用的文件及你导入的原始文件。",
                Location = new Point(22, 118), Size = new Size(446, 32), ForeColor = Theme.Muted });
            var accept = new SoftButton("删除曲目", true) { Location = new Point(236, 165), Width = 116, DialogResult = DialogResult.OK };
            var cancel = new SoftButton("取消", false) { Location = new Point(364, 165), Width = 104, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { accept, cancel }); CancelButton = cancel;
        }
    }
}
