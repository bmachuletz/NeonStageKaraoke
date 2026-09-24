using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NeonStage.PortableLauncher;

internal static class Program
{
    private const string PayloadResource = "NeonStage.Payload.zip";
    private const string LocalServer = "http://127.0.0.1:5274";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(item => item.Key, item => item.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
            var profile = Required(metadata, "NeonStage.Launcher.Profile");
            var entryPoint = Required(metadata, "NeonStage.Launcher.EntryPoint");
            var version = SanitizePathPart(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString() ?? "unknown");
            var profileKey = SanitizePathPart(profile.ToLowerInvariant());
            var extractionRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NeonStage", "portable", profileKey, version);

            using var mutex = new Mutex(false, MutexName(profileKey, version));
            var ownsMutex = false;
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromMinutes(2));
            }
            catch (AbandonedMutexException)
            {
                // Der vorherige Starter wurde während des Entpackens beendet.
                // Das unvollständige Verzeichnis besitzt keinen Marker und
                // wird im nächsten Schritt sicher neu aufgebaut.
                ownsMutex = true;
            }
            if (!ownsMutex)
                throw new TimeoutException("Das portable NeonStage-Paket wird bereits vorbereitet.");
            try
            {
                EnsureExtracted(assembly, extractionRoot);
            }
            finally
            {
                mutex.ReleaseMutex();
            }

            return StartPayload(profile, entryPoint, extractionRoot, args);
        }
        catch (Exception exception)
        {
            ShowError("NeonStage konnte nicht gestartet werden.\n\n" + exception.Message);
            return 1;
        }
    }

    private static void EnsureExtracted(Assembly assembly, string target)
    {
        var marker = Path.Combine(target, ".neonstage-payload-complete");
        if (File.Exists(marker)) return;

        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var temporary = target + ".extracting-" + Environment.ProcessId;
        if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        Directory.CreateDirectory(temporary);
        try
        {
            using var payload = assembly.GetManifestResourceStream(PayloadResource)
                ?? throw new InvalidDataException("Das eingebettete Programmpaket fehlt.");
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
            var root = Path.GetFullPath(temporary) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(temporary,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Das Programmpaket enthält einen ungültigen Dateipfad.");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
            File.WriteAllText(Path.Combine(temporary, ".neonstage-payload-complete"), "ok\n");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(temporary, target);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    private static int StartPayload(string profile, string entryPoint, string root, string[] args)
    {
        var executable = Path.GetFullPath(Path.Combine(root, entryPoint));
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new FileNotFoundException("Der Programmeinstieg fehlt im portablen Paket.", executable);

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false
        };
        start.Environment["PATH"] = root + Path.PathSeparator +
                                    (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        ConfigureProfile(profile, start);
        foreach (var argument in args) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Der NeonStage-Prozess konnte nicht erzeugt werden.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static void ConfigureProfile(string profile, ProcessStartInfo start)
    {
        if (profile.Equals("server", StringComparison.OrdinalIgnoreCase))
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NeonStage", "standalone-server");
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrWhiteSpace(music))
                music = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
            var library = Path.Combine(music, "NeonStage");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(library);
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            start.Environment["ASPNETCORE_URLS"] = LocalServer;
            start.Environment["Karaoke__PublicBaseUrl"] = LocalServer;
            start.Environment["Karaoke__LibraryPath"] = library;
            start.Environment["Karaoke__DatabasePath"] = Path.Combine(data, "karaoke.db");
            start.Environment["Usdb__CachePath"] = Path.Combine(data, "usdb-cache");
            start.Environment["Online__Enabled"] = "false";
            return;
        }

        // Lokaler Erstwert, aber kein Zwang: Eine später im Editor oder in der
        // Stage bewusst gespeicherte Serveradresse bleibt wirksam.
        start.Environment["NEONSTAGE_DEFAULT_SERVER_URL"] = LocalServer;
    }

    private static string Required(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Launcher-Metadatum fehlt: {key}");

    private static string MutexName(string profile, string version)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile + "|" + version)))[..20];
        return "Local\\NeonStagePortable-" + hash;
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static void ShowError(string message)
    {
        if (OperatingSystem.IsWindows())
            MessageBox(IntPtr.Zero, message, "NeonStage", 0x10);
        else
            Console.Error.WriteLine(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
