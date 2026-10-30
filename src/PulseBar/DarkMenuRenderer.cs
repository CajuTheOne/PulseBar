using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PulseBar;

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
	private sealed class DarkColors : ProfessionalColorTable
	{
		private static readonly Color Bg = Color.FromArgb(43, 43, 43);

		private static readonly Color Hover = Color.FromArgb(61, 61, 61);

		private static readonly Color Line = Color.FromArgb(72, 72, 72);

		public override Color ToolStripDropDownBackground => Bg;

		public override Color ImageMarginGradientBegin => Bg;

		public override Color ImageMarginGradientMiddle => Bg;

		public override Color ImageMarginGradientEnd => Bg;

		public override Color MenuBorder => Line;

		public override Color MenuItemBorder => Hover;

		public override Color MenuItemSelected => Hover;

		public override Color MenuItemSelectedGradientBegin => Hover;

		public override Color MenuItemSelectedGradientEnd => Hover;

		public override Color MenuItemPressedGradientBegin => Hover;

		public override Color MenuItemPressedGradientEnd => Hover;

		public override Color SeparatorDark => Line;

		public override Color SeparatorLight => Line;

		public override Color CheckBackground => Bg;

		public override Color CheckSelectedBackground => Hover;

		public override Color CheckPressedBackground => Hover;

		public override Color ButtonSelectedBorder => Hover;

		public DarkColors()
		{
			base.UseSystemColors = false;
		}
	}

	private static readonly Color Fg = Color.FromArgb(240, 240, 240);

	private static readonly Color FgDisabled = Color.FromArgb(125, 125, 125);

	public DarkMenuRenderer()
		: base(new DarkColors())
	{
		base.RoundedEdges = false;
	}

	protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
	{
		e.TextColor = (e.Item.Enabled ? Fg : FgDisabled);
		base.OnRenderItemText(e);
	}

	protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
	{
		e.ArrowColor = Fg;
		base.OnRenderArrow(e);
	}

	protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
	{
		Graphics graphics = e.Graphics;
		Rectangle imageRectangle = e.ImageRectangle;
		float num = (float)imageRectangle.X + (float)imageRectangle.Width / 2f;
		float num2 = (float)imageRectangle.Y + (float)imageRectangle.Height / 2f;
		float num3 = Math.Min(imageRectangle.Width, imageRectangle.Height);
		SmoothingMode smoothingMode = graphics.SmoothingMode;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		if (e.Item.Tag as string == "radio")
		{
			float num4 = num3 * 0.38f;
			using SolidBrush brush = new SolidBrush(Fg);
			graphics.FillEllipse(brush, num - num4 / 2f, num2 - num4 / 2f, num4, num4);
		}
		else
		{
			using Pen pen = new Pen(Fg, Math.Max(1.5f, num3 / 10f));
			ReadOnlySpan<PointF> points = [new PointF(num - num3 * 0.28f, num2), new PointF(num - num3 * 0.08f, num2 + num3 * 0.2f), new PointF(num + num3 * 0.3f, num2 - num3 * 0.22f)];
			graphics.DrawLines(pen, points);
		}
		graphics.SmoothingMode = smoothingMode;
	}
}
