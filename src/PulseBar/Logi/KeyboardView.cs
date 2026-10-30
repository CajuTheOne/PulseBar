using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace PulseBar.Logi;

internal sealed class KeyboardView : Control
{
	private new const float Top = -1.25f;

	private const float Width_ = 24f;

	private const float Height_ = 7.5f;

	private readonly KeyDef[] _keys = KeyLayout.G915;

	private readonly float _u;

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public HashSet<byte> Selected { get; } = new HashSet<byte>();

	[DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
	public Func<byte, Color> ColorOf { get; set; } = (byte _) => Color.Gray;

	public bool AllSelected => _keys.All((KeyDef k) => Selected.Contains(k.Id));

	public event Action? SelectionChanged;

	public KeyboardView(float unitPx)
	{
		_u = unitPx;
		DoubleBuffered = true;
		base.Size = new Size((int)Math.Ceiling(24f * _u) + 2, (int)Math.Ceiling(7.5f * _u) + 2);
		Font = new Font("Segoe UI", Math.Max(6.5f, unitPx * 0.24f));
		Cursor = Cursors.Hand;
	}

	private RectangleF Rect(KeyDef k)
	{
		return new RectangleF(k.X * _u + 1f, (k.Y - -1.25f) * _u + 1f, k.W * _u - 3f, k.H * _u - 3f);
	}

	public void SelectAll()
	{
		KeyDef[] keys = _keys;
		foreach (KeyDef keyDef in keys)
		{
			Selected.Add(keyDef.Id);
		}
		Invalidate();
		this.SelectionChanged?.Invoke();
	}

	public void ClearSelection()
	{
		Selected.Clear();
		Invalidate();
		this.SelectionChanged?.Invoke();
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		base.OnMouseDown(e);
		KeyDef[] keys = _keys;
		for (int i = 0; i < keys.Length; i++)
		{
			KeyDef k = keys[i];
			if (Rect(k).Contains(e.Location))
			{
				if (!Selected.Remove(k.Id))
				{
					Selected.Add(k.Id);
				}
				Invalidate();
				this.SelectionChanged?.Invoke();
				break;
			}
		}
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		Graphics graphics = e.Graphics;
		graphics.SmoothingMode = SmoothingMode.AntiAlias;
		graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
		using StringFormat format = new StringFormat
		{
			Alignment = StringAlignment.Center,
			LineAlignment = StringAlignment.Center,
			Trimming = StringTrimming.None,
			FormatFlags = StringFormatFlags.NoWrap
		};
		using Pen pen = new Pen(Color.White, 2.5f);
		using Pen pen2 = new Pen(Color.FromArgb(70, 70, 70), 1f);
		KeyDef[] keys = _keys;
		for (int i = 0; i < keys.Length; i++)
		{
			KeyDef k = keys[i];
			RectangleF rectangleF = Rect(k);
			Color color = ColorOf(k.Id);
			using GraphicsPath path = Round(rectangleF, _u * 0.15f);
			using (SolidBrush brush = new SolidBrush(Color.FromArgb(38, 38, 38)))
			{
				graphics.FillPath(brush, path);
			}
			using (SolidBrush brush2 = new SolidBrush(Color.FromArgb((color.R != 0 || color.G != 0 || color.B != 0) ? 90 : 0, color)))
			{
				graphics.FillPath(brush2, path);
			}
			graphics.DrawPath(Selected.Contains(k.Id) ? pen : pen2, path);
			using SolidBrush brush3 = new SolidBrush((color.R + color.G + color.B < 60) ? Color.FromArgb(110, 110, 110) : color);
			graphics.DrawString(k.Label, Font, brush3, rectangleF, format);
		}
	}

	private static GraphicsPath Round(RectangleF r, float rad)
	{
		GraphicsPath graphicsPath = new GraphicsPath();
		float num = rad * 2f;
		graphicsPath.AddArc(r.X, r.Y, num, num, 180f, 90f);
		graphicsPath.AddArc(r.Right - num, r.Y, num, num, 270f, 90f);
		graphicsPath.AddArc(r.Right - num, r.Bottom - num, num, num, 0f, 90f);
		graphicsPath.AddArc(r.X, r.Bottom - num, num, num, 90f, 90f);
		graphicsPath.CloseFigure();
		return graphicsPath;
	}
}
