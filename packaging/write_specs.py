# write_specs.py - one-time generator for the two initial package specs,
# their icons and license files. Run from the repo root:
#   python packaging/write_specs.py
import json
import os
import sys

os.chdir(os.path.join(os.path.dirname(__file__), '..'))

client_desc = {
 'en': "The Plugin-Store ribbon for Power PDF: browse, install, update and remove plugins from the team catalog.",
 'de': "Das Plugin-Store-Menüband für Power PDF: Plug-ins aus dem Team-Katalog durchsuchen, installieren, aktualisieren und entfernen.",
 'fr': "Le ruban Plugin-Store pour Power PDF : parcourez, installez, mettez à jour et supprimez les plugins du catalogue d'équipe.",
 'it': "La barra multifunzione Plugin-Store per Power PDF: sfoglia, installa, aggiorna e rimuovi i plugin dal catalogo del team.",
 'es': "La cinta Plugin-Store para Power PDF: explore, instale, actualice y elimine plugins del catálogo del equipo.",
 'nl': "Het Plugin-Store-lint voor Power PDF: blader door de teamcatalogus en installeer, update of verwijder plug-ins.",
 'pt': "A faixa Plugin-Store para o Power PDF: navegue, instale, atualize e remova plugins do catálogo da equipe.",
 'da': "Plugin-Store-båndet til Power PDF: gennemse, installer, opdater og fjern plugins fra teamkataloget.",
 'fi': "Plugin-Store-valintanauha Power PDF:lle: selaa, asenna, päivitä ja poista laajennuksia tiimin luettelosta.",
 'nb': "Plugin-Store-båndet for Power PDF: bla gjennom, installer, oppdater og fjern programtillegg fra teamkatalogen.",
 'sv': "Plugin-Store-menyfliken för Power PDF: bläddra, installera, uppdatera och ta bort plugin-program från teamkatalogen.",
 'pl': "Wstążka Plugin-Store dla Power PDF: przeglądaj, instaluj, aktualizuj i usuwaj wtyczki z katalogu zespołu.",
 'cs': "Pás karet Plugin-Store pro Power PDF: procházejte, instalujte, aktualizujte a odebírejte pluginy z týmového katalogu.",
 'hu': "A Plugin-Store menüszalag a Power PDF-hez: böngéssze, telepítse, frissítse és távolítsa el a bővítményeket a csapatkatalógusból.",
 'ru': "Лента Plugin-Store для Power PDF: просматривайте, устанавливайте, обновляйте и удаляйте плагины из каталога команды.",
 'tr': "Power PDF için Plugin-Store şeridi: ekip kataloğundan eklentilere göz atın, yükleyin, güncelleyin ve kaldırın."}

client = {
  "id": "com.tungsten.pluginstore",
  "versionFrom": {"file": "../client/common/version.h", "regex": "FP_VERSION_W  L\"([0-9.]+)\""},
  "name": {"en": "Plugin-Store"},
  "description": client_desc,
  "changelog": {"en": "First release of the Plugin-Store client.",
                "de": "Erste Veröffentlichung des Plugin-Store-Clients."},
  "minPowerPdfVersion": "5.0",
  "ribbonAtomNamespace": "FeaturePack::PluginStore",
  "zxt": {"x64": "../client/Release/PluginStore.zxt"},
  "include": [
    {"src": "../client/Plug-ins/PluginStore/UILayout", "dst": "UILayout"},
    {"src": "install.cmd", "dst": "install.cmd"},
    {"src": "icon.png", "dst": "assets/icon.png"},
    {"src": "../LICENSE", "dst": "LICENSES.md"}
  ],
  "uninstall": {"registryKeys": ["HKCU\\Software\\Kofax\\PDF\\Tungsten Power PDF\\PluginStore"],
                "extraPaths": []}
}
with open('packaging/pluginstore.ppakspec.json', 'w', encoding='utf-8') as f:
    json.dump(client, f, indent=2, ensure_ascii=False)

sb_desc = {
 'en': "Automatically creates a multi-level bookmark outline for any PDF: heading detection, live preview panel, table of contents, optional OCR.",
 'de': "Erzeugt automatisch eine mehrstufige Lesezeichen-Gliederung für jedes PDF: Überschriften-Erkennung, Live-Vorschau-Panel, Inhaltsverzeichnis, optionale OCR.",
 'fr': "Crée automatiquement une structure de signets à plusieurs niveaux pour tout PDF : détection des titres, panneau d'aperçu en direct, table des matières, OCR en option.",
 'it': "Crea automaticamente una struttura di segnalibri a più livelli per qualsiasi PDF: rilevamento dei titoli, pannello di anteprima dal vivo, sommario, OCR opzionale.",
 'es': "Crea automáticamente un esquema de marcadores de varios niveles para cualquier PDF: detección de títulos, panel de vista previa en vivo, índice, OCR opcional.",
 'nl': "Maakt automatisch een gelaagde bladwijzerstructuur voor elke PDF: koppendetectie, live voorbeeldpaneel, inhoudsopgave, optionele OCR.",
 'pt': "Cria automaticamente uma estrutura de marcadores em vários níveis para qualquer PDF: detecção de títulos, painel de visualização ao vivo, sumário, OCR opcional.",
 'da': "Opretter automatisk en bogmærkestruktur i flere niveauer for enhver PDF: registrering af overskrifter, live forhåndsvisningspanel, indholdsfortegnelse, valgfri OCR.",
 'fi': "Luo automaattisesti monitasoisen kirjanmerkkirakenteen mihin tahansa PDF-tiedostoon: otsikoiden tunnistus, live-esikatselupaneeli, sisällysluettelo, valinnainen OCR.",
 'nb': "Lager automatisk en bokmerkestruktur i flere nivåer for enhver PDF: overskriftsgjenkjenning, live forhåndsvisningspanel, innholdsfortegnelse, valgfri OCR.",
 'sv': "Skapar automatiskt en bokmärkesstruktur i flera nivåer för alla PDF-filer: rubrikidentifiering, live förhandsgranskningspanel, innehållsförteckning, valfri OCR.",
 'pl': "Automatycznie tworzy wielopoziomową strukturę zakładek dla każdego PDF: wykrywanie nagłówków, panel podglądu na żywo, spis treści, opcjonalne OCR.",
 'cs': "Automaticky vytváří víceúrovňovou strukturu záložek pro jakékoli PDF: rozpoznávání nadpisů, panel živého náhledu, obsah, volitelné OCR.",
 'hu': "Automatikusan többszintű könyvjelző-szerkezetet készít bármely PDF-hez: címsorfelismerés, élő előnézeti panel, tartalomjegyzék, opcionális OCR.",
 'ru': "Автоматически создаёт многоуровневую структуру закладок для любого PDF: распознавание заголовков, панель предварительного просмотра, оглавление, опциональное OCR.",
 'tr': "Her PDF için otomatik olarak çok seviyeli yer işareti yapısı oluşturur: başlık algılama, canlı önizleme paneli, içindekiler, isteğe bağlı OCR."}

sb = {
  "id": "com.tungsten.smartbookmarks",
  "version": "1.0.5",
  "name": {"en": "Smart Bookmarks"},
  "description": sb_desc,
  "changelog": {"en": "First release in the Plugin-Store (identical to the MSI release 1.0.5). The optional Tesseract OCR payload ships separately for now.",
                "de": "Erste Veröffentlichung im Plugin-Store (identisch mit MSI-Release 1.0.5). Die optionale Tesseract-OCR-Beilage kommt vorerst separat."},
  "minPowerPdfVersion": "5.0",
  "ribbonAtomNamespace": "TungstenBookmarks2",
  "zxt": {"x64": "C:/Claude/SmartBookmarks/Release/SmartBookmarks.zxt"},
  "include": [
    {"src": "C:/Claude/SmartBookmarks/Plug-ins/SmartBookmarks/UILayout", "dst": "UILayout"},
    {"src": "C:/Claude/SmartBookmarks/Plug-ins/SmartBookmarks/SmartBookmarks.ini.sample", "dst": "docs/SmartBookmarks.ini.sample"},
    {"src": "sb-icon.png", "dst": "assets/icon.png"},
    {"src": "sb-LICENSES.md", "dst": "LICENSES.md"}
  ],
  "uninstall": {"registryKeys": ["HKCU\\Software\\Kofax\\PDF\\Tungsten Power PDF\\SmartBookmarks"],
                "extraPaths": []}
}
with open('packaging/smartbookmarks.ppakspec.json', 'w', encoding='utf-8') as f:
    json.dump(sb, f, indent=2, ensure_ascii=False)

from PIL import Image
sys.path.insert(0, 'client/res')
from make_icons import draw
draw(128).save('packaging/icon.png')
Image.open('C:/Claude/SmartBookmarks/icon_auto.bmp').convert('RGB').resize((128, 128), Image.NEAREST).save('packaging/sb-icon.png')

with open('packaging/sb-LICENSES.md', 'w', encoding='utf-8') as f:
    f.write("# Smart Bookmarks - third-party licenses\n\n"
            "- nlohmann/json (json.hpp), MIT License.\n"
            "- Tesseract OCR (optional, NOT bundled in this package), Apache License 2.0;\n"
            "  the plugin detects the engine at runtime and falls back gracefully when absent.\n")

print('specs, icons and licenses written')
