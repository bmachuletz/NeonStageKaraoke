# LiveKit-Stack für NeonStage

Der Standard-Stack ist bewusst ein echtes **Single-Host-/Single-Container-Deployment**:
ein LiveKit-Server, kein Redis, kein Kubernetes und kein zusätzlicher Clusterzustand. LiveKit hält
Rooms und Teilnehmer lokal im Speicher. Das passt zu NeonStage, solange nur diese eine Instanz
betrieben wird. Ein Neustart beendet aktive Online-Rooms; gespeicherte Songs und Lyrics betrifft das
nicht.

Der optionale Caddy-L4-Container wird nur benötigt, wenn dieser Stack selbst HTTPS/WSS und
TURN/TLS auf Port 443 bereitstellen soll. Das Startskript wählt dafür automatisch die passende
LiveKit-Konfiguration. Mit einem vorhandenen HTTPS-Reverse-Proxy bleibt es bei genau einem
LiveKit-Container. Linux-Host-Networking vermeidet zusätzliche Docker-NAT für WebRTC.

## Muss LiveKit öffentlich erreichbar sein?

- Nur dasselbe LAN/VPN: nein; eine intern erreichbare Adresse und interne Firewallfreigaben genügen.
- Verschiedene Internetanschlüsse: ja. Signaling und mindestens ein Medienpfad müssen von beiden Stages erreichbar sein.

Öffentliche Produktionsports:

| Port | Protokoll | Zweck |
|---|---|---|
| 443 | TCP | WSS/HTTPS und, beim Managed-TLS-Profil, TURN/TLS per SNI |
| 80 | TCP | automatische TLS-Zertifikate; nur Managed-TLS-Profil |
| 7881 | TCP | WebRTC/ICE-Fallback |
| 3478 | UDP | eingebautes TURN/UDP |
| 7882 | UDP | direkter WebRTC-Medienverkehr über einen UDP-Mux-Port |

Port 7880 bleibt intern und wird nur vom TLS-Reverse-Proxy angesprochen. `LIVEKIT_API_KEY` und `LIVEKIT_API_SECRET` dürfen niemals öffentlich ausgeliefert werden.

## Vorbereitung

Für den vorgeschlagenen Hostnamen zwei DNS-A/AAAA-Einträge auf die öffentliche IP des Servers setzen:

```text
livekit.cloud.hdvtec.de
turn.cloud.hdvtec.de
```

Mit einem vorhandenen Reverse-Proxy genügt anschließend genau ein Befehl:

```bash
deploy/livekit/start.sh
```

Beim ersten Start wird die Vorbereitung automatisch ausgeführt. Sie erzeugt kryptografisch
zufällige Zugangsdaten, schreibt die nicht versionierten Laufzeitdateien unter
`deploy/livekit/runtime/` und trägt dieselben Werte in die ebenfalls ignorierte Root-`.env` für
den NeonStage-Server ein. Vorhandene LiveKit-Zugangsdaten werden wiederverwendet.

## Variante A: dedizierter Host oder freie Ports 80/443

```bash
deploy/livekit/start.sh --managed-tls
docker compose -f deploy/livekit/compose.yaml logs -f edge livekit
```

Caddy bezieht automatisch vertrauenswürdige Zertifikate für beide Domains und verteilt TCP 443 anhand des TLS-SNI zwischen LiveKit-Signaling und TURN/TLS. Diese Variante funktioniert nicht parallel zu einem anderen Dienst, der auf derselben öffentlichen IP bereits TCP 80/443 belegt.

## Variante B: vorhandener Reverse-Proxy auf dem NeonStage-Host

Der oben gezeigte Einzeiler startet diese Variante. Danach den vorhandenen HTTPS-Proxy für `livekit.cloud.hdvtec.de` auf `127.0.0.1:7880` routen. Ein Nginx-Beispiel liegt in `nginx-livekit.conf.example`. Die Ports 7881/TCP, 3478/UDP und 7882/UDP müssen trotzdem direkt durch Host-, Router- und Cloud-Firewall gelangen; ein normaler HTTP-Reverse-Proxy kann WebRTC-Medien nicht transportieren.

Diese Variante bietet direkten UDP/TCP-Medienverkehr und TURN/UDP. Für TURN/TLS auf derselben TCP-443-Adresse braucht der bestehende Edge SNI-fähiges Layer-4-Routing zum unverschlüsselten LiveKit-Port 5349 (`external_tls: true`). Alternativ den Managed-TLS-Stack auf eine eigene öffentliche IP bzw. einen eigenen Host legen.

## NeonStage starten

Nach jeder Änderung an der Root-`.env` auch den Karaoke-Server neu erstellen/starten:

```bash
docker compose up -d --build server
curl -s http://127.0.0.1:5274/api/online/config
```

Die Antwort muss `"enabled":true` und die konfigurierte `wss://`-Adresse enthalten. Danach auf beiden Stages `F8` drücken oder das Online-Symbol anklicken/antippen und denselben Room verwenden. Graue Sendeklammern bedeuten offline; nach erfolgreicher Verbindung leuchten sie farbig. Der Singer sendet die Musik und bis zu zwei vom Betriebssystem gemeldete Mikrofon-Eingänge. Zwei physische Funkmikrofone können von Karaoke-Hardware auch als ein gemeinsamer Mehrkanal-Eingang bereitgestellt werden.

## Firewall-Beispiel mit UFW

Die Befehle verändern die Host-Firewall und werden deshalb bewusst nicht automatisch ausgeführt:

```bash
sudo ufw allow 443/tcp
sudo ufw allow 80/tcp                 # nur Managed TLS
sudo ufw allow 7881/tcp
sudo ufw allow 3478/udp
sudo ufw allow 7882/udp
```

Bei einem Server hinter NAT müssen dieselben Ports im Router auf den Docker-Host weitergeleitet werden. `rtc.use_external_ip` lässt LiveKit die öffentliche Adresse ermitteln; bei komplexem oder symmetrischem NAT ist eine VM mit echter öffentlicher IP zuverlässiger.

## Betrieb

```bash
docker compose -f deploy/livekit/compose.yaml ps
docker compose -f deploy/livekit/compose.yaml logs -f livekit
deploy/livekit/stop.sh
```

Der Stack enthält bewusst weder Redis noch Ingress, Egress, Recording oder SIP. Für den
NeonStage-Audio-MVP werden diese Komponenten nicht benötigt. Erst wenn mehrere LiveKit-Instanzen
parallel laufen sollen, wird Redis für verteiltes Routing relevant. Bei deutlich höherer Last kann
der einzelne UDP-Mux-Port später durch einen Portbereich ersetzt werden.
