# client_changelog_0_3_0.py - writes the 16-language changelog of client 0.3.0
# into packaging/pluginstore.ppakspec.json. Run from the repo root.
import json
import os

os.chdir(os.path.join(os.path.dirname(__file__), '..'))

changelog = {
 'en': "Installer is now an MSI. The client has its own update lane: a newer version shows an update hint and installs via MSI. New uninstall button. Updating a plugin that is loaded in Power PDF now works.",
 'de': "Das Installationsprogramm ist jetzt ein MSI. Der Client hat eine eigene Update-Spur: eine neuere Version zeigt einen Update-Hinweis und installiert per MSI. Neuer Deinstallieren-Button. Updates von Plug-ins, die in Power PDF geladen sind, funktionieren jetzt.",
 'fr': "Le programme d'installation est désormais un MSI. Le client dispose de son propre circuit de mise à jour : une nouvelle version affiche un avis et s'installe via MSI. Nouveau bouton de désinstallation. La mise à jour d'un plugin chargé dans Power PDF fonctionne désormais.",
 'it': "Il programma di installazione è ora un MSI. Il client ha un proprio canale di aggiornamento: una nuova versione mostra un avviso e si installa tramite MSI. Nuovo pulsante di disinstallazione. L'aggiornamento di un plugin caricato in Power PDF ora funziona.",
 'es': "El instalador ahora es un MSI. El cliente tiene su propio canal de actualización: una versión más reciente muestra un aviso y se instala mediante MSI. Nuevo botón de desinstalación. La actualización de un plugin cargado en Power PDF ya funciona.",
 'nl': "Het installatieprogramma is nu een MSI. De client heeft een eigen updatekanaal: een nieuwere versie toont een updatemelding en installeert via MSI. Nieuwe knop voor verwijderen. Het bijwerken van een plug-in die in Power PDF is geladen, werkt nu.",
 'pt': "O instalador agora é um MSI. O cliente tem o seu próprio canal de atualização: uma versão mais recente mostra um aviso e instala-se via MSI. Novo botão de desinstalação. A atualização de um plugin carregado no Power PDF já funciona.",
 'da': "Installationsprogrammet er nu en MSI. Klienten har sit eget opdateringsspor: en nyere version viser en opdateringsmeddelelse og installeres via MSI. Ny afinstallationsknap. Opdatering af et plugin, der er indlæst i Power PDF, virker nu.",
 'fi': "Asennusohjelma on nyt MSI. Asiakasohjelmalla on oma päivityskanava: uudempi versio näyttää päivitysilmoituksen ja asentuu MSI:n kautta. Uusi asennuksen poistopainike. Power PDF:ään ladatun laajennuksen päivittäminen toimii nyt.",
 'nb': "Installasjonsprogrammet er nå en MSI. Klienten har sitt eget oppdateringsspor: en nyere versjon viser et oppdateringsvarsel og installeres via MSI. Ny avinstalleringsknapp. Oppdatering av et programtillegg som er lastet i Power PDF, fungerer nå.",
 'sv': "Installationsprogrammet är nu en MSI. Klienten har ett eget uppdateringsspår: en nyare version visar ett uppdateringsmeddelande och installeras via MSI. Ny avinstallationsknapp. Uppdatering av ett plugin-program som är inläst i Power PDF fungerar nu.",
 'pl': "Instalator jest teraz plikiem MSI. Klient ma własną ścieżkę aktualizacji: nowsza wersja wyświetla powiadomienie i instaluje się przez MSI. Nowy przycisk odinstalowania. Aktualizacja wtyczki załadowanej w Power PDF działa teraz poprawnie.",
 'cs': "Instalační program je nyní MSI. Klient má vlastní kanál aktualizací: novější verze zobrazí upozornění a nainstaluje se přes MSI. Nové tlačítko pro odinstalaci. Aktualizace pluginu načteného v Power PDF nyní funguje.",
 'hu': "A telepítő mostantól MSI. A kliens saját frissítési csatornát kapott: az újabb verzió frissítési értesítést jelenít meg, és MSI-n keresztül települ. Új eltávolítás gomb. A Power PDF-be betöltött bővítmény frissítése mostantól működik.",
 'ru': "Установщик теперь в формате MSI. У клиента собственный канал обновлений: новая версия показывает уведомление и устанавливается через MSI. Новая кнопка удаления. Обновление плагина, загруженного в Power PDF, теперь работает.",
 'tr': "Yükleyici artık bir MSI. İstemcinin kendi güncelleme kanalı var: daha yeni bir sürüm güncelleme bildirimi gösterir ve MSI ile yüklenir. Yeni kaldırma düğmesi. Power PDF'te yüklü bir eklentinin güncellenmesi artık çalışıyor.",
}

p = 'packaging/pluginstore.ppakspec.json'
d = json.load(open(p, encoding='utf-8'))
d['changelog'] = changelog
json.dump(d, open(p, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
print(len(changelog), 'languages written')
