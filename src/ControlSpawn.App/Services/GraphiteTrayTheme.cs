using System.Drawing;
using Forms = System.Windows.Forms;
namespace ControlSpawn.App.Services;

/// <summary>Styles detached menus too, so QA never needs to create a notification icon.</summary>
public static class GraphiteTrayTheme
{
    public static void Apply(Forms.ToolStripDropDown menu)
    {
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(new GraphiteColors()) { RoundedEdges = true };
        menu.BackColor = Color.FromArgb(20, 20, 22);
        menu.ForeColor = Color.FromArgb(244, 244, 245);
        menu.Padding = new Forms.Padding(5);
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = menu.ForeColor;
            if (item is Forms.ToolStripMenuItem child && child.HasDropDownItems) Apply(child.DropDown);
        }
    }
    private sealed class GraphiteColors : Forms.ProfessionalColorTable
    {
        private static Color Panel => Color.FromArgb(20, 20, 22);
        private static Color Hover => Color.FromArgb(65, 34, 27);
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Color.FromArgb(55, 55, 61);
        public override Color MenuItemBorder => Color.FromArgb(255, 77, 26);
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientMiddle => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Color.FromArgb(55, 55, 61);
        public override Color SeparatorLight => Panel;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
