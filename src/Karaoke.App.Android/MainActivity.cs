using Android.App;
using Avalonia.Android;
using Karaoke.App.Services;
using Karaoke.App.Desktop;

namespace Karaoke.App.Android;

[Activity(
    Label = "Neon Stage Karaoke",
    Icon = "@drawable/app_icon",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation |
                           global::Android.Content.PM.ConfigChanges.ScreenSize |
                           global::Android.Content.PM.ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<Karaoke.App.App>
{
    static MainActivity()
    {
        // Vorgabe für eine frische Installation auf der Ikarao-Box. Eine später
        // in der App gespeicherte Serveradresse hat weiterhin Vorrang.
        AppPreferences.DefaultServerAddress = "http://192.168.178.91:5274";

        // Muss vor dem Aufbau des MainViewModels gesetzt sein. Android und Desktop
        // verwenden damit exakt dieselbe Master/Vocal-Synchronisationslogik.
        AudioPlaybackServiceFactory.Create = () => new LibVlcAudioPlaybackService();
    }
}
