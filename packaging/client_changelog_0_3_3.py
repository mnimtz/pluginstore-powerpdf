# client_changelog_0_3_3.py - 16-language changelog of client 0.3.3.
import json
import os

os.chdir(os.path.join(os.path.dirname(__file__), '..'))

changelog = {
 'en': "Author and contact now have their own columns in the plugin list. The client speaks all 16 European Power PDF languages. More reliable restart after installing or removing a plugin.",
 'de': "Autor und Kontakt haben jetzt eigene Spalten in der Plug-in-Liste. Der Client spricht alle 16 europäischen Power-PDF-Sprachen. Zuverlässigerer Neustart nach dem Installieren oder Entfernen eines Plug-ins.",
 'fr': "L'auteur et le contact disposent désormais de leurs propres colonnes dans la liste des plugins. Le client est disponible dans les 16 langues européennes de Power PDF. Redémarrage plus fiable après l'installation ou la suppression d'un plugin.",
 'it': "Autore e contatto hanno ora colonne proprie nell'elenco dei plugin. Il client è disponibile in tutte le 16 lingue europee di Power PDF. Riavvio più affidabile dopo l'installazione o la rimozione di un plugin.",
 'es': "El autor y el contacto tienen ahora columnas propias en la lista de plugins. El cliente está disponible en los 16 idiomas europeos de Power PDF. Reinicio más fiable tras instalar o quitar un plugin.",
 'nl': "Auteur en contact hebben nu eigen kolommen in de lijst met plug-ins. De client is beschikbaar in alle 16 Europese talen van Power PDF. Betrouwbaardere herstart na het installeren of verwijderen van een plug-in.",
 'pt': "O autor e o contacto têm agora colunas próprias na lista de plugins. O cliente está disponível nas 16 línguas europeias do Power PDF. Reinício mais fiável após instalar ou remover um plugin.",
 'da': "Forfatter og kontakt har nu egne kolonner i pluginlisten. Klienten findes på alle 16 europæiske Power PDF-sprog. Mere pålidelig genstart efter installation eller fjernelse af et plugin.",
 'fi': "Tekijällä ja yhteystiedolla on nyt omat sarakkeet laajennusluettelossa. Asiakasohjelma on saatavilla kaikilla 16 eurooppalaisella Power PDF -kielellä. Luotettavampi uudelleenkäynnistys laajennuksen asentamisen tai poistamisen jälkeen.",
 'nb': "Forfatter og kontakt har nå egne kolonner i listen over programtillegg. Klienten finnes på alle 16 europeiske Power PDF-språk. Mer pålitelig omstart etter installasjon eller fjerning av et programtillegg.",
 'sv': "Författare och kontakt har nu egna kolumner i listan över plugin-program. Klienten finns på alla 16 europeiska Power PDF-språk. Mer tillförlitlig omstart efter installation eller borttagning av ett plugin-program.",
 'pl': "Autor i kontakt mają teraz własne kolumny na liście wtyczek. Klient jest dostępny we wszystkich 16 europejskich językach Power PDF. Bardziej niezawodny restart po zainstalowaniu lub usunięciu wtyczki.",
 'cs': "Autor a kontakt mají nyní v seznamu pluginů vlastní sloupce. Klient je k dispozici ve všech 16 evropských jazycích Power PDF. Spolehlivější restart po instalaci nebo odebrání pluginu.",
 'hu': "A szerző és a kapcsolattartó mostantól saját oszlopot kapott a bővítménylistában. A kliens mind a 16 európai Power PDF-nyelven elérhető. Megbízhatóbb újraindítás bővítmény telepítése vagy eltávolítása után.",
 'ru': "Автор и контакт теперь отображаются в отдельных столбцах списка плагинов. Клиент доступен на всех 16 европейских языках Power PDF. Более надёжный перезапуск после установки или удаления плагина.",
 'tr': "Yazar ve iletişim artık eklenti listesinde kendi sütunlarına sahip. İstemci, Power PDF'in 16 Avrupa dilinin tamamında kullanılabilir. Bir eklenti yüklendikten veya kaldırıldıktan sonra daha güvenilir yeniden başlatma.",
}

p = 'packaging/pluginstore.ppakspec.json'
d = json.load(open(p, encoding='utf-8'))
d['changelog'] = changelog
# ship every UILayout language folder, not only the base + DEU
json.dump(d, open(p, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
print('changelog 0.3.3 written')
