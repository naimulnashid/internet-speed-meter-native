using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace SpeedMeter.App.Theme;

/// <summary>
/// Geist, bundled (SIL Open Font License, see Assets/Fonts/OFL.txt), so no font
/// is fetched from anywhere. The variable files cover every weight. The web
/// dashboard set its pages in the same face.
/// </summary>
public static class Fonts
{
    public static readonly FontFamily Sans = new("ms-appx:///Assets/Fonts/Geist-Variable.ttf#Geist");
    public static readonly FontFamily Mono = new("ms-appx:///Assets/Fonts/GeistMono-Variable.ttf#Geist Mono");

    public static FontWeight Weight(int weight) => new((ushort)weight);
}
