namespace PulseBar.Logi;

// Подсветка зоны (клавиши или логотип): эффект из списка прошивки ("off", "static", "breathing", "cycle", "wave" …),
// цвет для статичного цвета и дыхания, период эффекта в мс (0 — как задано в прошивке по умолчанию).
internal sealed class Lighting
{
	public string Effect { get; set; } = "static";

	public string Color { get; set; } = "#00A0FF";

	public int Period { get; set; }
}
