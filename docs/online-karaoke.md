# Online-Karaoke (MVP)

Der erste Online-Modus verbindet mehrere Stage-Programme über einen selbst gehosteten LiveKit-Server. Genau ein Standort ist **Singer**, alle anderen sind **Listener**. Duett-, Video- und Zeitsynchronisation sind bewusst nicht Bestandteil dieses MVP.

## Audioweg

- Singer: lokale Musik + bis zu zwei lokale Mikrofon-Eingänge → `OnlineBroadcastMixer` → ein LiveKit-Audiotrack. Meldet Android zwei Geräte, werden beide separat aufgenommen und mit Clipping-Schutz gemischt. Meldet eine Karaoke-Box ihre beiden Funkmikrofone bereits als einen gemeinsamen Mehrkanal-Eingang, übernimmt die Stage diesen Eingang vollständig.
- Listener: ausschließlich der empfangene LiveKit-Audiotrack; die lokale Song-Wiedergabe wird gestoppt.
- Remote-Audio besitzt keinen Eingang in `OnlineBroadcastMixer`. Damit kann es weder erneut gesendet werden noch eine Feedback-Schleife bilden.
- Das Interface `IOnlineAudioTransport` trennt Rollen-/Audiologik vom LiveKit-Adapter.

In der Stage öffnet `F8` oder ein Klick/Tipp auf das Neon-Mikrofon mit Broadcast-Wellen das kleine Online-Panel. Graue Sendeklammern zeigen an, dass die Stage keinem Room verbunden ist; nach erfolgreichem Beitritt leuchten sie farbig. Das Online-Symbol bleibt auch dann sichtbar, wenn die Online-Konfiguration nicht erreichbar ist, damit die Fehlermeldung auf Geräten ohne Tastatur geprüft werden kann. Room und Standortname eintragen und als Listener oder Singer beitreten. Der Server vergibt die Singer-Rolle atomar; ein zweiter Singer erhält einen Konflikt. Mit **Broadcast stoppen** wird die Rolle wieder frei.

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

`LIVEKIT_API_KEY` und `LIVEKIT_API_SECRET` bleiben ausschließlich auf dem Karaoke-Server. Die Stage erhält über `/api/online/rooms/join` nur ein kurzlebiges, roomgebundenes Token. Listener-Tokens dürfen abonnieren, aber nicht veröffentlichen; Singer-Tokens dürfen genau einen lokalen Audiotrack veröffentlichen.

Für LiveKit werden TLS/WSS, ein erreichbarer WebRTC-Portbereich und TURN für restriktive Netze empfohlen. Der Online-Modus bleibt deaktiviert, solange URL, Key oder Secret fehlen.

Ein einsatzbereiter Single-Node-Docker-Stack samt DNS-, Firewall-, TLS- und Reverse-Proxy-Anleitung liegt unter [`deploy/livekit/`](../deploy/livekit/README.md).

Verwendet wird das offizielle LiveKit Unity SDK 2.0.0. Dessen Paket liefert native FFI/WebRTC-Bibliotheken für Windows x64, Linux x64 sowie macOS ARM64 und x86_64; der Apple-Silicon-Build benötigt daher kein Rosetta. Opus, ICE, STUN/TURN, Jitter Buffer und Transportverschlüsselung liegen im LiveKit/WebRTC-Stack. Das zusätzliche Unity-Modul `com.unity.modules.screencapture` ist nur eine Compile-Abhängigkeit des vollständigen SDKs; NeonStage veröffentlicht weder Kamera noch Bildschirm.

## Grenzen des MVP

- keine gemeinsame Transport-/Lyrics-Synchronisation zwischen Standorten
- keine Duette und kein zweiter Sender
- kein Kamera-/Videostream
- lokale Mikrofonaufnahme über Unity Audio; Kopfhörer sind für den Singer empfohlen
- Rollen-/Teilnehmerzustand liegt zunächst im Speicher des Karaoke-Servers

Logs sind mit `[Online]` markiert und enthalten Room/Rolle/Status, aber weder API-Secret noch Access-Token.
