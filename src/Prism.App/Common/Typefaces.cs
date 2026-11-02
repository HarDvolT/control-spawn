using System.Windows;
using System.Windows.Media;

namespace Prism.App.Common;

/// <summary>
/// The app's two typefaces. Segoe UI Variable is the native Windows 11 UI font: crisp at small sizes in WPF,
/// real semibold weights and even (tabular) digits. Display is cut for headings and numbers, Text for reading.
/// Windows 10 falls back to Segoe UI.
/// </summary>
public static class Typefaces
{
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static Typeface Number(FontWeight weight) => new(Display, FontStyles.Normal, weight, FontStretches.Normal);
    public static Typeface Body(FontWeight weight) => new(Text, FontStyles.Normal, weight, FontStretches.Normal);
}
