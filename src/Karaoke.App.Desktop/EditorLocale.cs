using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

namespace Karaoke.App.Desktop;

public static class EditorLocale
{
    public static bool German => (Environment.GetEnvironmentVariable("NEONSTAGE_LOCALE") ??
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName).StartsWith("de", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["＋ Neues Lied"]="＋ New song", ["Wünsche"]="Requests", ["Verwaltung ▾"]="Management ▾",
        ["Wünsche, Events und Admin-Portal"]="Requests, events and admin portal",
        ["Wünsche und Importe"]="Requests and imports", ["Wünsche und Downloads"]="Requests and downloads",
        ["MP3-Ordner importieren …"]="Import MP3 folder …", ["Events verwalten …"]="Manage events …",
        ["Songpakete"]="Song packages", ["Ausgewählten Song exportieren …"]="Export selected song …",
        ["Mehrere Songs exportieren …"]="Export multiple songs …",
        ["Ein Songpaket importieren …"]="Import one song package …",
        ["Mehrere Songpakete importieren …"]="Import multiple song packages …",
        ["Admin-Webseite öffnen"]="Open admin website", ["Rückgängig (Strg+Z)"]="Undo (Ctrl+Z)",
        ["Wiederholen (Strg+Shift+Z)"]="Redo (Ctrl+Shift+Z)", ["Alignment ▾"]="Alignment ▾",
        ["GPU-Alignment starten"]="Start GPU alignment", ["Ausgewählten Song neu alignen"]="Realign selected song",
        ["Gesamte Bibliothek neu alignen …"]="Realign entire library …", ["Speichern"]="Save",
        ["Aktuellen Arbeitsstand lokal und auf dem Server sichern"]="Save current work locally and on the server",
        ["◴ Versionen"]="◴ Versions", ["Gespeicherte Lyrics-Stände verwalten"]="Manage saved lyrics versions",
        ["Gespeicherte Lyrics-Stände dieses Songs"]="Saved lyrics versions for this song",
        ["Versionsliste aktualisieren"]="Refresh version list",
        ["Beim Laden bleibt der gespeicherte Stand unverändert. Erst „Speichern“ legt daraus eine neue Revision an."]="Loading never changes the saved version. A new revision is created only when you save.",
        ["Laden"]="Load", ["Diesen Lyrics-Stand als neuen Arbeitsstand öffnen"]="Open this lyrics version as a new working state",
        ["Diesen archivierten Stand löschen"]="Delete this archived version",
        ["Die veröffentlichte Stage-Version ist vor dem Löschen geschützt."]="The published Stage version is protected from deletion.",
        ["Lyrics-Version löschen"]="Delete lyrics version", ["Lyrics-Version laden"]="Load lyrics version",
        ["Abbrechen"]="Cancel", ["Version endgültig löschen"]="Delete version permanently",
        ["Als Arbeitsstand laden"]="Load as working state", ["LYRICS-STAND LÖSCHEN?"]="DELETE LYRICS VERSION?",
        ["LYRICS-STAND LADEN?"]="LOAD LYRICS VERSION?",
        ["Nur dieser gespeicherte Lyrics-Stand wird gelöscht. Audio, Song und andere Versionen bleiben erhalten."]="Only this saved lyrics version will be deleted. Audio, song, and all other versions remain intact.",
        ["Der aktuelle Editorinhalt wird durch diesen Stand ersetzt. Die gewählte Version bleibt unverändert; mit „Speichern“ entsteht anschließend eine neue Revision."]="The current editor content will be replaced by this version. The selected version remains unchanged; saving creates a new revision.",
        ["✓ Song freigeben"]="✓ Release song", ["🗑 Song löschen"]="Delete song", ["SONGS"]="SONGS",
        ["Titel oder Interpret suchen …"]="Search title or artist …", ["COVER · KLICK / DROP"]="COVER · CLICK / DROP",
        ["Klicken oder Bilddatei hier ablegen"]="Click or drop an image here", ["SPUREN"]="TRACKS",
        ["Instrumental synchron zumischen"]="Mix synchronized instrumental", ["UNITY-STAGE LIVE-VORSCHAU"]="UNITY STAGE LIVE PREVIEW",
        ["Zurück"]="Back", ["▶ Grenze"]="▶ Boundary", ["300 ms vor und nach der ausgewählten Grenze als Loop"]="Loop 300 ms before and after the selected boundary",
        ["Bereich mit Shift + Ziehen markieren"]="Select a range with Shift + drag", ["INSPEKTOR"]="INSPECTOR",
        ["⟷ Synchronisieren"]="⟷ Synchronize",
        ["Mehrfachauswahl: Strg + Klick. Waveform-Bereich: Shift + Ziehen. Danach exakt einpassen."]="Multi-select: Ctrl + click. Waveform range: Shift + drag. Then fit exactly.",
        ["Bitte zuerst einen Song laden."]="Please load a song first.",
        ["Bitte zuerst mit Shift + Ziehen einen Waveform-Bereich markieren."]="First select a waveform range with Shift + drag.",
        ["Die Änderungshistorie ist noch nicht bereit."]="The edit history is not ready yet.",
        ["Bitte zuerst mindestens eine Zeile, ein Wort oder eine Silbe auswählen."]="First select at least one line, word, or syllable.",
        ["Der markierte Bereich darf nicht vor dem Song beginnen."]="The selected range cannot start before the song.",
        ["Der markierte Bereich besitzt keine gültige Dauer."]="The selected range has no valid duration.",
        ["Die Auswahl enthält kein bearbeitbares Lyrics-Segment."]="The selection contains no editable lyric segment.",
        ["Die ausgewählten Segmente besitzen keine gültige Dauer."]="The selected segments have no valid duration.",
        ["Der markierte Bereich ist für die ausgewählten Segmente zu kurz."]="The selected range is too short for the selected segments.",
        ["Der markierte Bereich würde ein Untersegment auf null verkürzen."]="The selected range would reduce a child segment to zero duration.",
        ["Der Zielbereich kollidiert mit einem nicht ausgewählten Segment."]="The target range collides with an unselected segment.",
        ["Zeilen-, Wort- oder Silbentext"]="Line, word, or syllable text", ["STAGE-DARSTELLUNG"]="STAGE PRESENTATION",
        ["Haltezeit nach der Zeile (Sekunden, leer = automatisch)"]="Hold after line (seconds; empty = automatic)",
        ["automatisch"]="automatic", ["Darstellung übernehmen"]="Apply presentation", ["← verschieben"]="← move",
        ["verschieben →"]="move →", ["Ende −"]="End −", ["Ende +"]="End +", ["+ Zeile"]="+ Line",
        ["⧉ Zeile"]="⧉ Line", ["+ Wort"]="+ Word", ["+ Silbe"]="+ Syllable", ["Löschen"]="Delete",
        ["PRÜFFILTER"]="REVIEW FILTERS", ["Nur ungeprüfte Segmente"]="Unreviewed segments only",
        ["Niedrige Konfidenz"]="Low confidence", ["Timing-Konflikte"]="Timing conflicts", ["Manuell korrigiert"]="Manually adjusted",
        ["Nächster unsicherer Bereich  N"]="Next uncertain region  N", ["Als geprüft markieren  R"]="Mark reviewed  R",
        ["▶ ALLE EVENTS ABARBEITEN"]="▶ PROCESS ALL EVENTS", ["Spotify: Titel oder Interpret suchen …"]="Spotify: search title or artist …",
        ["⌕ SUCHEN"]="⌕ SEARCH", ["✓ Lyrics gefunden"]="✓ Lyrics found", ["IMPORTIEREN →"]="IMPORT →",
        ["▶ Verarbeiten"]="▶ Process", ["EVENTVERWALTUNG"]="EVENT MANAGEMENT",
        ["Sessions planen, teilen und auf die Bühne schalten"]="Plan, share and activate sessions on stage",
        ["↻ Aktualisieren"]="↻ Refresh", ["⚡ Sofort-Session"]="⚡ Instant session",
        ["＋ Event planen"]="＋ Plan event", ["EVENTS"]="EVENTS", ["Noch keine Session ausgewählt"]="No session selected",
        ["Einladungslink"]="Invitation link", ["Link kopieren"]="Copy link", ["WhatsApp"]="WhatsApp",
        ["E-Mail"]="Email", ["QR speichern"]="Save QR", ["Auf Bühne aktivieren"]="Activate on stage",
        ["Aktive Bühne beenden"]="Deactivate stage", ["Wünsche abarbeiten"]="Process requests",
        ["Session löschen"]="Delete session", ["Schließen"]="Close", ["NEUES EVENT"]="NEW EVENT",
        ["Event planen"]="Plan event", ["NAME"]="NAME", ["START"]="START", ["ENDE (OPTIONAL)"]="END (OPTIONAL)",
        ["BESCHREIBUNG (OPTIONAL)"]="DESCRIPTION (OPTIONAL)", ["Event erstellen"]="Create event",
        ["Format: JJJJ-MM-TT HH:mm"]="Format: YYYY-MM-DD HH:mm",
        ["WÜNSCHE / DOWNLOAD"]="REQUESTS / DOWNLOAD", ["MP3-ORDNER"]="MP3 FOLDER",
        ["MP3s AUS EINEM ORDNER VERARBEITEN"]="PROCESS MP3s FROM A FOLDER",
        ["ID3 lesen → Sidecar/ID3/LRCLIB-Lyrics → GPU-Separation → Wort-/Silbenalignment → Review"]="Read ID3 → sidecar/ID3/LRCLIB lyrics → GPU separation → word/syllable alignment → review",
        ["Ordnerpfad auf dem Neon-Stage-Server …"]="Folder path on the Neon Stage server …",
        ["ORDNER WÄHLEN …"]="SELECT FOLDER …", ["Unterordner rekursiv durchsuchen"]="Search subfolders recursively",
        ["Die Originaldateien bleiben unverändert. Nur technisch vollständige Songs gelangen in die Bibliothek."]="Original files remain unchanged. Only technically complete songs enter the library.",
        ["▶ IMPORT STARTEN"]="▶ START IMPORT", ["Mehrere Songs exportieren"]="Export multiple songs",
        ["SONGPAKET ZUSAMMENSTELLEN"]="BUILD SONG PACKAGE",
        ["Alle Projektdateien und Lyrics-Versionen der ausgewählten Songs werden übernommen."]="All project files and lyric versions of the selected songs are included.",
        ["Alle sichtbaren"]="Select visible", ["Auswahl löschen"]="Clear selection",
        ["Ausgewählte exportieren"]="Export selected", ["Songpaket exportieren"]="Export song package",
        ["Mehrere Neon-Stage-Songpakete importieren"]="Import multiple Neon Stage song packages",
        ["Neon-Stage-Songpaket importieren"]="Import Neon Stage song package"
    };

    public static string Text(string value)
    {
        if (German || string.IsNullOrWhiteSpace(value)) return value;
        if (English.TryGetValue(value, out var translated)) return translated;
        return value.Replace("Bibliothek", "library", StringComparison.OrdinalIgnoreCase)
            .Replace("Song konnte nicht", "Could not", StringComparison.OrdinalIgnoreCase)
            .Replace("fehlgeschlagen", "failed", StringComparison.OrdinalIgnoreCase)
            .Replace("wird geladen", "is loading", StringComparison.OrdinalIgnoreCase)
            .Replace("bereit zur Prüfung", "ready for review", StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(Window window)
    {
        if (German) return;
        window.Title = Text(window.Title ?? string.Empty);
        foreach (var control in window.GetLogicalDescendants().OfType<Control>())
        {
            if (control is ContentControl { Content: string content } contentControl) contentControl.Content = Text(content);
            if (control is TextBlock textBlock) textBlock.Text = Text(textBlock.Text ?? string.Empty);
            if (control is TextBox { Watermark: string watermark } textBox) textBox.Watermark = Text(watermark);
            if (ToolTip.GetTip(control) is string tip) ToolTip.SetTip(control, Text(tip));
            if (control is Button { Flyout: MenuFlyout flyout })
                ApplyMenuItems(flyout.Items.OfType<MenuItem>());
        }
    }

    private static void ApplyMenuItems(IEnumerable<MenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.Header is string header) item.Header = Text(header);
            ApplyMenuItems(item.Items.OfType<MenuItem>());
        }
    }
}
