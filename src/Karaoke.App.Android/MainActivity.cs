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
        // Heute verwendeter externer Server. KARAOKE_SERVER hat im gemeinsamen
        // ViewModel Vorrang vor einer eventuell noch gespeicherten lokalen IP,
        // sodass auch ein App-Update sofort den erreichbaren Server verwendet.
        const string serverAddress = "http://cloud.hdvtec.de:5274";
        AppPreferences.DefaultServerAddress = serverAddress;
        Environment.SetEnvironmentVariable("KARAOKE_SERVER", serverAddress);

        // Muss vor dem Aufbau des MainViewModels gesetzt sein. Android und Desktop
        // verwenden damit exakt dieselbe Master/Vocal-Synchronisationslogik.
        AudioPlaybackServiceFactory.Create = () => new LibVlcAudioPlaybackService();
    }
}
