namespace BoykisserPet;

internal static class DarkTheme
{
    public static readonly Color Back = Color.FromArgb(28, 29, 35);
    public static readonly Color Panel = Color.FromArgb(38, 40, 48);
    public static readonly Color Input = Color.FromArgb(22, 23, 28);
    public static readonly Color Text = Color.FromArgb(235, 236, 242);
    public static readonly Color Muted = Color.FromArgb(165, 169, 181);
    public static readonly Color Accent = Color.FromArgb(104, 125, 190);

    public static void Apply(Control root)
    {
        root.BackColor = Back; root.ForeColor = Text;
        foreach (Control c in root.Controls)
        {
            if (c is TextBox || c is RichTextBox || c is ComboBox || c is ListBox || c is NumericUpDown) c.BackColor = Input;
            else if (c is Button || c is CheckBox || c is RadioButton || c is TabControl || c is TabPage || c is Panel || c is FlowLayoutPanel || c is GroupBox || c is TrackBar) c.BackColor = Panel;
            else c.BackColor = Back;
            c.ForeColor = c is Label && c.ForeColor == SystemColors.GrayText ? Muted : Text;
            if (c.HasChildren) Apply(c);
        }
    }

    public static void Apply(ToolStrip strip)
    {
        strip.BackColor = Panel; strip.ForeColor = Text;
        strip.Renderer = new ToolStripProfessionalRenderer(new DarkColorTable());
        foreach (ToolStripItem item in strip.Items)
        {
            item.BackColor = Panel; item.ForeColor = Text;
            if (item is ToolStripMenuItem mi && mi.HasDropDownItems) Apply(mi.DropDown);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuItemSelected => Color.FromArgb(58, 64, 82);
        public override Color MenuItemBorder => Accent;
        public override Color MenuBorder => Color.FromArgb(65, 68, 80);
        public override Color SeparatorDark => Color.FromArgb(65, 68, 80);
        public override Color SeparatorLight => Panel;
    }
}
