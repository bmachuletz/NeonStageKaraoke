# Online-Karaoke (MVP)

Der erste Online-Modus verbindet mehrere Stage-Programme über einen selbst gehosteten LiveKit-Server. Genau ein Standort ist **Singer**, alle anderen sind **Listener**. Lyrics und das lokal vorhandene Songvideo werden mit dem empfangenen Live-Audio synchronisiert; standortübergreifende Duette und das Übertragen eines Videos über LiveKit sind noch nicht Bestandteil dieses MVP.

## Audioweg

- Singer: Instrumental, Originalvocal-Stem und bis zu zwei lokale Mikrofon-Eingänge werden als drei getrennt benannte LiveKit-Audiotracks veröffentlicht. Vor der Übertragung hebt eine senderseitige Sprach-AGC leise Mikrofone dynamisch bis Faktor 12 an; eine sanfte Rauschschwelle und ein Peak-Limiter schützen dabei Leerlauf und laute Passagen. Der Unity-Mikrofon-Ringpuffer startet rund 35 ms hinter seinem Schreibkopf und wird bei Clock-Drift neu verankert. Instrumental und Originalvocals werden um lokale Audioausgabelatenz plus Mikrofonpuffer gehalten: Der Sänger reagiert schließlich auf das Signal, das am Lautsprecher ankommt, nicht auf den früheren Unity-DSP-Block. Dieser Ausrichtungswert wird zusammen mit der Timeline übertragen. Zwei unabhängige Mikrofone werden mit Equal-Power-Schutz in den gemeinsamen Live-Mikrofontrack gemischt; einen Hardware-Duomix übernimmt die Stage vollständig.
- Listener: hörbar sind ausschließlich die empfangenen LiveKit-Audiotracks. Die lokale Songspur läuft stumm als Medienquelle weiter. Eigene Regler mischen Musik, Originalvocals und Live-Mikrofon unabhängig; Originalvocals und Live-Mikro liegen dabei direkt untereinander auf derselben Seite der Bedienleiste. Aktiviert der aktuelle Singer auf der mobilen Songseite **Listener-Mix sperren**, werden diese Regler deaktiviert und seine Mikrofonlautstärke verbindlich übernommen. Video und Lyrics folgen Zeitmarken der Singer-Stage; senderseitige Musik-/Mikrofon-Ausrichtung, WebRTC-Jitter, Netzwerk-Roundtrip, LiveKit-Puffer und die lokale Audioausgabe werden dabei kompensiert.
- Remote-Audio besitzt keinen Eingang in `OnlineBroadcastMixer`. Damit kann es weder erneut gesendet werden noch eine Feedback-Schleife bilden.
- Das Interface `IOnlineAudioTransport` trennt Rollen-/Audiologik vom LiveKit-Adapter.

Beim Start zeigt Unity einen touchfähigen Launcher mit der permanenten **Direkt-Stage** und allen veröffentlichten Stages. Ein beim Erstellen hochgeladenes Stage-Bild und der Name bilden jeweils eine Karte. Mehrere Geräte können dabei unterschiedliche Stages desselben Servers wählen; Queue, Playback, Controller-Lease, Gäste-QR und LiveKit-Raum bleiben vollständig getrennt. Nach Auswahl einer Online-Stage öffnet sich deren Online-Panel bereits vorausgewählt. `F8` oder ein Klick/Tipp auf das Statusfeld mit Neon-Mikrofon öffnet es später erneut. Das Feld schreibt den Zustand dauerhaft als **OFFLINE**, **VERBINDEN**, **FEHLER** oder **ONLINE** aus. Online zeigt eine zweite Zeile zusätzlich **SINGER · SENDET** oder **LISTENER · EMPFÄNGT**; ein deutlich wachsender Außenring signalisiert die aktive beziehungsweise gerade aufgebaute Verbindung auch aus größerer Entfernung. Kennwort und frei wählbaren Standortnamen eingeben und beitreten. Das erfolgreiche Kennwort wird nur lokal auf diesem Gerät für diese Stage gemerkt. Eine Stage, die einem laufenden Online-Raum noch nicht beigetreten ist, lädt dessen Song, Video und Lyrics nicht.

Neue Events/Stages sind standardmäßig **offline**. Beim Erstellen im Editor kann ein PNG-, JPEG- oder WebP-Launcherbild bis 5 MB gewählt werden. Mit **Im Launcher veröffentlichen** wird die Stage zusätzlich zur Direkt-Stage sichtbar; andere veröffentlichte Stages bleiben aktiv. Für eine **Online-Stage** sind Stagename und ein Kennwort mit mindestens vier Zeichen erforderlich. Das Kennwort wird serverseitig ausschließlich als gesalzener PBKDF2-Hash gespeichert. Nur veröffentlichte Online-Stages können betreten werden.

Optional kann beim Erstellen **Alle dürfen sich unterhalten** aktiviert werden. Solange noch kein Song läuft, der aktuelle Titel gestoppt oder pausiert ist oder die Warteliste bereits beendet wurde, veröffentlichen dann alle verbundenen Standorte ausschließlich ihre Mikrofonspur. Das Online-Symbol und die Bedienleiste zeigen **PAUSEN-GESPRÄCH · MIKRO AN** deutlich an. Sobald Play/Resume ausgeführt wird, entfernen alle Listener ihren Mikrofontrack wieder; Musik, Originalvocals, Timeline und die laufenden Singer-Mikrofone kommen dann erneut nur vom Singer-Standort. Ohne diese Eventoption bleiben Listener-Tokens vollständig ohne Publish-Berechtigung. Gesprächs-Listener erhalten nur die Berechtigung für Audiopublishing, niemals für Timeline-Daten.

Da der gegenwärtige Unity-Mikrofonpfad keine WebRTC-Echounterdrückung verwendet, sollten gegenüberstehende Lautsprecher nicht direkt in die Mikrofone strahlen. Bei hohen Raumlautstärken sind Headsets oder eine entsprechend eingestellte Hardware-Echounterdrückung sinnvoll.

Jede Stage besitzt eine dauerhaft lokal gespeicherte Standort-GUID. Der auf dieser Stage angezeigte Gäste-QR-Code trägt die GUID im Link, ohne dass der Gast sie eingeben oder kennen muss. Nach dem Scan gibt jede Sängerin und jeder Sänger nur den eigenen Namen an. Ein Queue-Eintrag speichert Anzeigename und Standort-GUID getrennt. Schon der erste, noch gestoppte Queue-Eintrag macht genau seine Stage zum Singer und gibt dort die Wiedergabesteuerung frei; der Titel startet erst nach dem Play-Klick. Alle anderen verbundenen Standorte bleiben Listener. Beim nächsten Queue-Eintrag wandert die Rolle automatisch weiter. Mehrere Personen können so unter ihren eigenen Namen am selben Standort singen. Der Server stellt weiterhin atomar sicher, dass es zu jedem Zeitpunkt höchstens einen Singer gibt.

Ein allgemein geteilter Event-Link und der Event-QR-Code enthalten absichtlich keine Standort-GUID. Bei einer Online-Stage sind Song-Buttons darüber gesperrt: Wünsche werden ausschließlich über den QR-Code eines tatsächlich verbundenen Standorts angenommen. Der Server prüft die aktive Standortanmeldung zusätzlich und vertraut nicht allein auf den URL-Parameter. Offline-Events funktionieren unverändert auch über den allgemeinen Link.

## Server und LiveKit konfigurieren

Im Desktop-Editor befindet sich unter **Einstellungen → Online-Karaoke** die bevorzugte
Konfiguration. Dort lassen sich Aktivierung, LiveKit-URL, API-Key, API-Secret und Room-Präfix
serverseitig speichern. **LiveKit-Verbindung testen** ruft authentifiziert die LiveKit-Room-API
auf und prüft damit DNS/TLS, Erreichbarkeit sowie Key und Secret. Ein gespeichertes Secret wird
dem Editor nur noch als „vorhanden“ gemeldet und nie zurückübertragen. Änderungen gelten für
neu ausgestellte Tokens sofort; ein Serverneustart ist nicht erforderlich.

Alternativ kann die Konfiguration weiterhin über Umgebungsvariablen vorgegeben werden:

```env
ONLINE_ENABLED=true
LIVEKIT_URL=wss://livekit.example.com
LIVEKIT_API_KEY=...
LIVEKIT_API_SECRET=...
LIVEKIT_ROOM_PREFIX=neonstage-
```

Ist Online-Karaoke per Umgebung aktiviert oder sind URL/Zugangsdaten als LiveKit-/`Online__`-
Umgebungsvariablen gesetzt, sind sie maßgeblich. Der Editor zeigt die
Felder dann schreibgeschützt an, kann die wirksame Verbindung aber weiterhin testen. Beim Zugriff
auf einen entfernten Karaoke-Server dürfen Secrets nur über HTTPS übertragen werden; lokal ist
eine Loopback-Adresse zulässig.

`LIVEKIT_API_KEY` und `LIVEKIT_API_SECRET` bleiben ausschließlich auf dem Karaoke-Server. Die Stage erhält über `/api/online/rooms/join` nur ein kurzlebiges, roomgebundenes Token. Listener-Tokens dürfen abonnieren, aber nicht veröffentlichen; Singer-Tokens dürfen die lokalen Karaoke-Audiotracks veröffentlichen.

Für LiveKit werden TLS/WSS, ein erreichbarer WebRTC-Portbereich und TURN für restriktive Netze empfohlen. Der Online-Modus bleibt deaktiviert, solange URL, Key oder Secret fehlen.

Ein einsatzbereiter Single-Node-Docker-Stack samt DNS-, Firewall-, TLS- und Reverse-Proxy-Anleitung liegt unter [`deploy/livekit/`](../deploy/livekit/README.md).

Verwendet wird das offizielle LiveKit Unity SDK 2.0.0. Dessen Paket liefert native FFI/WebRTC-Bibliotheken für Windows x64, Linux x64 sowie macOS ARM64 und x86_64; der Apple-Silicon-Build benötigt daher kein Rosetta. Opus, ICE, STUN/TURN, Jitter Buffer und Transportverschlüsselung liegen im LiveKit/WebRTC-Stack. Das zusätzliche Unity-Modul `com.unity.modules.screencapture` ist nur eine Compile-Abhängigkeit des vollständigen SDKs; NeonStage veröffentlicht weder Kamera noch Bildschirm.

Für Android übernimmt der Build vorübergehend LiveKits nach 2.0.0 veröffentlichten `ContextUtils`-Fallback. Ohne ihn kann die native JNI-Klassensuche trotz korrekt eingebettetem WebRTC-JAR die Meldung `Android context init failed; PlatformAudio will not work` ausgeben. `scripts/linux/build-unity-stage-android.sh` spielt den Patch automatisch und wiederholbar in den Unity-Paketcache ein; sobald ein LiveKit-Release den Fix enthält, kann diese Kompatibilitätsschicht entfallen.

## Grenzen des MVP

- die serverseitige Queue ist die gemeinsame Song-/Rollenquelle; die Singer-Stage sendet zusätzlich fünfmal pro Sekunde ihre Songposition über den LiveKit-Datenkanal. Fehlen auf einem Gerät WebRTC-Statistiken, startet die Kompensation sicher mit 180 ms und übernimmt Messwerte, sobald sie verfügbar sind
- keine Duette und kein zweiter Sender
- kein Kamera-/Videostream
- Mikrofonaufnahme über Unity Audio; LiveKits nativer Android-ADM bleibt bis zu einem SDK-Upgrade deaktiviert, weil dessen Initialisierung auf einzelnen Karaoke-Geräten blockieren kann; Kopfhörer sind für den Singer empfohlen
- Rollen-/Teilnehmerzustand liegt zunächst im Speicher des Karaoke-Servers

Logs sind mit `[Online]` markiert und enthalten Room/Rolle/Status, aber weder API-Secret noch Access-Token.
