(function () {
  const language = (localStorage.neonStageLocale || navigator.language || 'en').toLowerCase().startsWith('de') ? 'de' : 'en';
  const en = {
    'Aktive Bühne':'Active stage','Name ändern':'Change name','JETZT AUF DER BÜHNE':'NOW ON STAGE',
    'Live-Reaktion senden':'Send live reaction','Herz senden':'Send heart','Lächeln senden':'Send smile',
    'Daumen hoch senden':'Send thumbs up','Applaus senden':'Send applause','Flamme senden':'Send fire',
    'Warteliste':'Queue','Wünsche':'Requests','FRISCH AUF DER BÜHNE':'FRESH ON STAGE','Neu':'New',
    'Titel, Interpret oder Album':'Title, artist or album','Entdecke deinen nächsten Song.':'Discover your next song.',
    'Deine Songs kannst du verschieben oder entfernen.':'You can reorder or remove your songs.',
    'NOCH NICHT DABEI?':'NOT IN THE LIBRARY?','Wünsch dir einen Song':'Request a song',
    'Spotify verbinden':'Connect Spotify','Bei Spotify suchen':'Search Spotify','Bei Spotify oder Qobuz suchen':'Search Spotify or Qobuz',
    'Titel mit synchronisierten Lyrics können später importiert werden.':'Tracks with synchronized lyrics can be imported later.',
    'Aktuelle Wünsche':'Current requests','WILLKOMMEN AUF DER BÜHNE':'WELCOME TO THE STAGE',
    'Wie heißt du?':'What is your name?','Unter diesem Namen erscheinen deine Songwünsche.':'Your song requests will use this name.',
    'Dein Name':'Your name','Gästeportal':'Guest portal','EVENTS VERWALTEN':'MANAGE EVENTS',
    'Deine nächste Karaoke-Nacht beginnt hier.':'Your next karaoke night starts here.',
    'Event anlegen, Link teilen und die Warteliste schon vor der Party füllen lassen.':'Create an event, share its link, and let guests fill the queue before the party.',
    'Sofort-Session':'Instant session','Event planen':'Plan event',
    'Lokaler Verwaltungszugang · Vor einer öffentlichen Freigabe wird dieser Bereich mit einer Anmeldung geschützt.':'Local administration access · Protect this area with authentication before exposing it publicly.',
    'Events werden geladen …':'Loading events …','Schließen':'Close','NEUES EVENT':'NEW EVENT','Party planen':'Plan a party',
    'Name':'Name','Start':'Start','Ende (optional)':'End (optional)','Beschreibung':'Description','Event erstellen':'Create event',
    'Teilen':'Share','Kopieren':'Copy','E-Mail':'Email','QR speichern':'Save QR','Auf Bühne aktivieren':'Activate on stage',
    'Wünsche abarbeiten':'Process requests','Suche …':'Searching …','Webservice nicht erreichbar.':'Web service unavailable.',
    'Keine Songs gefunden.':'No songs found.','Song steht auf der Warteliste ✦':'Song added to the queue ✦',
    'Song konnte nicht hinzugefügt werden.':'Could not add the song.','Song entfernt':'Song removed',
    'Die Bühne wartet auf den ersten Song.':'The stage is waiting for the first song.',
    'Spotify ist auf dem Server noch nicht konfiguriert.':'Spotify is not configured on the server.',
    'Spotify und Qobuz sind auf dem Server noch nicht konfiguriert.':'Spotify and Qobuz are not configured on the server.',
    'Spotify und LRCLIB werden durchsucht …':'Searching Spotify and LRCLIB …','Keine synchronisierten Lyrics':'No synchronized lyrics',
    'Spotify, Qobuz und LRCLIB werden durchsucht …':'Searching Spotify, Qobuz, and LRCLIB …',
    'In Qobuz öffnen ↗':'Open in Qobuz ↗','Katalogsuche fehlgeschlagen. Sind Spotify oder Qobuz konfiguriert?':'Catalog search failed. Are Spotify or Qobuz configured?',
    '✓ Synchronisierte Lyrics':'✓ Synchronized lyrics','In Spotify öffnen ↗':'Open in Spotify ↗',
    'Suche fehlgeschlagen. Ist Spotify verbunden?':'Search failed. Is Spotify connected?',
    'Musikwunsch gespeichert ✦':'Song request saved ✦','Wunsch konnte nicht gespeichert werden.':'Could not save the request.',
    'Noch keine Musikwünsche.':'No song requests yet.','Wunsch entfernt':'Request removed',
    'Wunsch konnte nicht entfernt werden.':'Could not remove the request.','Einladung ungültig':'Invalid invitation',
    'Ein neuer Song ist jetzt verfügbar ✦':'A new song is now available ✦','Noch kein Event angelegt.':'No event created yet.',
    'Sofort-Session ist bereit':'Instant session is ready','Event wurde angelegt':'Event created',
    'Wunschlisten-Worker gestartet':'Request worker started','Einladungslink kopiert':'Invitation link copied'
  };
  const translate = value => language === 'de' ? value : (en[value] || value);
  const dynamic=value => language==='de' ? value : value
    .replace(/^(\d+) passende Titel$/, '$1 matching tracks')
    .replace(/^(\d+) Spotify-Titel gefunden$/, '$1 Spotify tracks found')
    .replace(/^(\d+) Katalog-Titel gefunden$/, '$1 catalog tracks found')
    .replace(/^gewünscht von /, 'requested by ')
    .replace(/^für /, 'for ')
    .replace(/ Events$/, ' events');
  function apply(root=document) {
    if(root.nodeType===Node.TEXT_NODE){const raw=root.nodeValue,trim=raw.trim(),translated=en[trim]||dynamic(trim);if(trim&&translated!==trim)root.nodeValue=raw.replace(trim,translated);return}
    const walker=document.createTreeWalker(root,NodeFilter.SHOW_TEXT); let node;
    while(node=walker.nextNode()){const raw=node.nodeValue,trim=raw.trim(),translated=en[trim]||dynamic(trim);if(trim&&translated!==trim)node.nodeValue=raw.replace(trim,translated)}
    root.querySelectorAll?.('[placeholder],[title],[aria-label]').forEach(el=>['placeholder','title','aria-label'].forEach(a=>{const v=el.getAttribute(a);if(v&&en[v])el.setAttribute(a,en[v])}));
  }
  document.documentElement.lang=language; window.NeonI18n={language,t:translate,apply};
  document.addEventListener('DOMContentLoaded',()=>{apply();new MutationObserver(records=>records.forEach(r=>{if(r.type==='characterData')apply(r.target);else r.addedNodes.forEach(apply)})).observe(document.body,{childList:true,characterData:true,subtree:true})});
})();
