# smartbookmarks_1_0_7.py - updates the Smart Bookmarks spec for 1.0.7
# (shared ribbon tab) with a 16-language changelog. Run from the repo root.
import json
import os

os.chdir(os.path.join(os.path.dirname(__file__), '..'))

changelog = {
 'en': "Smart Bookmarks now lives on the shared \"Enhanced Features\" ribbon tab instead of its own tab; existing installations move automatically at the next start.",
 'de': "Smart Bookmarks liegt jetzt im gemeinsamen Register \"Erweiterte Funktionen\" statt in einem eigenen Register; bestehende Installationen ziehen beim nächsten Start automatisch um.",
 'fr': "Smart Bookmarks se trouve désormais dans l'onglet commun \"Fonctions avancées\" au lieu de son propre onglet ; les installations existantes sont migrées automatiquement au prochain démarrage.",
 'it': "Smart Bookmarks si trova ora nella scheda comune \"Funzioni avanzate\" invece che in una scheda propria; le installazioni esistenti vengono spostate automaticamente al prossimo avvio.",
 'es': "Smart Bookmarks ahora se encuentra en la pestaña común \"Funciones avanzadas\" en lugar de en una pestaña propia; las instalaciones existentes se trasladan automáticamente en el próximo inicio.",
 'nl': "Smart Bookmarks staat nu op het gedeelde lint-tabblad \"Geavanceerde functies\" in plaats van op een eigen tabblad; bestaande installaties verhuizen automatisch bij de volgende start.",
 'pt': "O Smart Bookmarks encontra-se agora no separador comum \"Funções avançadas\" em vez de num separador próprio; as instalações existentes mudam automaticamente no próximo arranque.",
 'da': "Smart Bookmarks ligger nu på den fælles fane \"Avancerede funktioner\" i stedet for på sin egen fane; eksisterende installationer flytter automatisk ved næste start.",
 'fi': "Smart Bookmarks sijaitsee nyt yhteisellä \"Lisätoiminnot\"-välilehdellä oman välilehden sijaan; olemassa olevat asennukset siirtyvät automaattisesti seuraavalla käynnistyskerralla.",
 'nb': "Smart Bookmarks ligger nå på den felles fanen \"Avanserte funksjoner\" i stedet for på sin egen fane; eksisterende installasjoner flyttes automatisk ved neste oppstart.",
 'sv': "Smart Bookmarks finns nu på den gemensamma fliken \"Avancerade funktioner\" i stället för på en egen flik; befintliga installationer flyttas automatiskt vid nästa start.",
 'pl': "Smart Bookmarks znajduje się teraz na wspólnej karcie \"Funkcje zaawansowane\" zamiast na własnej karcie; istniejące instalacje są przenoszone automatycznie przy następnym uruchomieniu.",
 'cs': "Smart Bookmarks se nyní nachází na společné kartě \"Rozšířené funkce\" místo na vlastní kartě; stávající instalace se automaticky přesunou při příštím spuštění.",
 'hu': "A Smart Bookmarks mostantól a közös \"Speciális funkciók\" lapon található saját lap helyett; a meglévő telepítések a következő indításkor automatikusan átkerülnek.",
 'ru': "Smart Bookmarks теперь находится на общей вкладке \"Расширенные функции\" вместо собственной вкладки; существующие установки переносятся автоматически при следующем запуске.",
 'tr': "Smart Bookmarks artık kendi sekmesi yerine ortak \"Gelişmiş İşlevler\" şeridinde yer alıyor; mevcut kurulumlar bir sonraki başlatmada otomatik olarak taşınır.",
}

p = 'packaging/smartbookmarks.ppakspec.json'
d = json.load(open(p, encoding='utf-8'))
d['version'] = '1.0.7'
d['changelog'] = changelog
d['ribbonAtomNamespace'] = 'FeaturePack::SmartBookmarks'
json.dump(d, open(p, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
print('spec 1.0.7 written')
