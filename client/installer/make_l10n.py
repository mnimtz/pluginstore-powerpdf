#!/usr/bin/env python3
"""make_l10n.py - writes the localization files of the client MSI (C0.8.0).

For each of the 16 store languages: l10n/<culture>.wxl (our own installer
texts; WiX brings the standard dialogs in these cultures) and
l10n/license-<culture>.rtf (translated heading and note, then the MIT license
in its English original, which stays authoritative).

    python installer/make_l10n.py
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'l10n')

# culture, LCID, codepage of the MSI tables for that language
CULTURES = [
    ('en-US', 1033, 1252), ('de-DE', 1031, 1252), ('fr-FR', 1036, 1252), ('it-IT', 1040, 1252),
    ('es-ES', 3082, 1252), ('nl-NL', 1043, 1252), ('pt-BR', 1046, 1252), ('da-DK', 1030, 1252),
    ('fi-FI', 1035, 1252), ('nb-NO', 1044, 1252), ('sv-SE', 1053, 1252), ('pl-PL', 1045, 1250),
    ('cs-CZ', 1029, 1250), ('hu-HU', 1038, 1250), ('ru-RU', 1049, 1251), ('tr-TR', 1055, 1254),
]

# NoPowerPdf, ClosePowerPdf, Downgrade, LicenseNote
TEXT = {
    'en-US': ("Tungsten Power PDF was not found on this computer. Please install Power PDF first.",
              "Please close Tungsten Power PDF and continue.",
              "A newer version of the Add-on Store is already installed.",
              "The following MIT license applies in its English original."),
    'de-DE': ("Tungsten Power PDF wurde auf diesem Computer nicht gefunden. Bitte installieren Sie zuerst Power PDF.",
              "Bitte schließen Sie Tungsten Power PDF und fahren Sie fort.",
              "Eine neuere Version des Add-on Store ist bereits installiert.",
              "Es gilt die folgende MIT-Lizenz in ihrem englischen Originalwortlaut."),
    'fr-FR': ("Tungsten Power PDF est introuvable sur cet ordinateur. Installez d'abord Power PDF.",
              "Fermez Tungsten Power PDF, puis continuez.",
              "Une version plus récente de l'Add-on Store est déjà installée.",
              "La licence MIT suivante s'applique dans sa version originale anglaise."),
    'it-IT': ("Tungsten Power PDF non è stato trovato su questo computer. Installare prima Power PDF.",
              "Chiudere Tungsten Power PDF e continuare.",
              "È già installata una versione più recente di Add-on Store.",
              "Si applica la seguente licenza MIT nella versione originale inglese."),
    'es-ES': ("No se encontró Tungsten Power PDF en este equipo. Instale primero Power PDF.",
              "Cierre Tungsten Power PDF y continúe.",
              "Ya hay instalada una versión más reciente de Add-on Store.",
              "Se aplica la siguiente licencia MIT en su versión original en inglés."),
    'nl-NL': ("Tungsten Power PDF is niet gevonden op deze computer. Installeer eerst Power PDF.",
              "Sluit Tungsten Power PDF en ga verder.",
              "Er is al een nieuwere versie van de Add-on Store geïnstalleerd.",
              "De volgende MIT-licentie geldt in de oorspronkelijke Engelse versie."),
    'pt-BR': ("O Tungsten Power PDF não foi encontrado neste computador. Instale primeiro o Power PDF.",
              "Feche o Tungsten Power PDF e continue.",
              "Uma versão mais recente do Add-on Store já está instalada.",
              "Aplica-se a seguinte licença MIT na sua versão original em inglês."),
    'da-DK': ("Tungsten Power PDF blev ikke fundet på denne computer. Installer først Power PDF.",
              "Luk Tungsten Power PDF, og fortsæt.",
              "En nyere version af Add-on Store er allerede installeret.",
              "Følgende MIT-licens gælder i sin engelske originalversion."),
    'fi-FI': ("Tungsten Power PDF:ää ei löytynyt tästä tietokoneesta. Asenna ensin Power PDF.",
              "Sulje Tungsten Power PDF ja jatka.",
              "Add-on Storesta on jo asennettu uudempi versio.",
              "Seuraava MIT-lisenssi on voimassa alkuperäisessä englanninkielisessä muodossaan."),
    'nb-NO': ("Tungsten Power PDF ble ikke funnet på denne datamaskinen. Installer Power PDF først.",
              "Lukk Tungsten Power PDF og fortsett.",
              "En nyere versjon av Add-on Store er allerede installert.",
              "Følgende MIT-lisens gjelder i sin engelske originalversjon."),
    'sv-SE': ("Tungsten Power PDF hittades inte på den här datorn. Installera Power PDF först.",
              "Stäng Tungsten Power PDF och fortsätt.",
              "En nyare version av Add-on Store är redan installerad.",
              "Följande MIT-licens gäller i sin engelska originalversion."),
    'pl-PL': ("Nie znaleziono programu Tungsten Power PDF na tym komputerze. Najpierw zainstaluj Power PDF.",
              "Zamknij program Tungsten Power PDF i kontynuuj.",
              "Zainstalowano już nowszą wersję Add-on Store.",
              "Obowiązuje poniższa licencja MIT w oryginalnej wersji angielskiej."),
    'cs-CZ': ("Na tomto počítači nebyl nalezen Tungsten Power PDF. Nejprve nainstalujte Power PDF.",
              "Zavřete Tungsten Power PDF a pokračujte.",
              "Novější verze Add-on Store je již nainstalována.",
              "Platí následující licence MIT v původním anglickém znění."),
    'hu-HU': ("A Tungsten Power PDF nem található ezen a számítógépen. Először telepítse a Power PDF-et.",
              "Zárja be a Tungsten Power PDF-et, majd folytassa.",
              "Az Add-on Store egy újabb verziója már telepítve van.",
              "A következő MIT-licenc az eredeti angol nyelvű szövegével érvényes."),
    'ru-RU': ("Tungsten Power PDF не найден на этом компьютере. Сначала установите Power PDF.",
              "Закройте Tungsten Power PDF и продолжите.",
              "Уже установлена более новая версия Add-on Store.",
              "Действует следующая лицензия MIT в оригинальной английской редакции."),
    'tr-TR': ("Bu bilgisayarda Tungsten Power PDF bulunamadı. Lütfen önce Power PDF'i yükleyin.",
              "Lütfen Tungsten Power PDF'i kapatın ve devam edin.",
              "Add-on Store'un daha yeni bir sürümü zaten yüklü.",
              "Aşağıdaki MIT lisansı İngilizce özgün metniyle geçerlidir."),
}

MIT = ("MIT License\n\nCopyright (c) 2026 Tungsten Automation internal tooling\n\n"
       "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated "
       "documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation "
       "the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to "
       "permit persons to whom the Software is furnished to do so, subject to the following conditions:\n\n"
       "The above copyright notice and this permission notice shall be included in all copies or substantial portions of "
       "the Software.\n\n"
       "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO "
       "THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE "
       "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, "
       "TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE "
       "SOFTWARE.")


def rtf_text(s):
    """Plain text to RTF: escapes, paragraphs, every non-ASCII character as \\uN?."""
    out = []
    for ch in s:
        o = ord(ch)
        if ch in '\\{}':
            out.append('\\' + ch)
        elif ch == '\n':
            out.append('\\par\n')
        elif o < 128:
            out.append(ch)
        else:
            out.append(f'\\u{o if o < 32768 else o - 65536}?')
    return ''.join(out)


def xml(s):
    return s.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('"', '&quot;')


# Minimum Power PDF version of the client (C1.1.0): the Plugin SDK it is built with.
# Keep in sync with Product.wxs (PPDFNEWENOUGH) and common/hostversion.h.
MIN_PPDF = '2025.3.7'   # 2025.3 hotfix 7: the MSI checks 2025.3, the client the hotfix level
TOO_OLD = {
    'en-US': "The Add-on Store needs Tungsten Power PDF {v} or later. Please update Power PDF first.",
    'de-DE': "Der Add-on Store benötigt Tungsten Power PDF {v} oder neuer. Bitte aktualisieren Sie zuerst Power PDF.",
    'fr-FR': "L'Add-on Store nécessite Tungsten Power PDF {v} ou une version ultérieure. Mettez d'abord Power PDF à jour.",
    'it-IT': "Add-on Store richiede Tungsten Power PDF {v} o versione successiva. Aggiornare prima Power PDF.",
    'es-ES': "La Add-on Store necesita Tungsten Power PDF {v} o posterior. Actualice primero Power PDF.",
    'nl-NL': "De Add-on Store vereist Tungsten Power PDF {v} of hoger. Werk eerst Power PDF bij.",
    'pt-BR': "A Add-on Store requer o Tungsten Power PDF {v} ou posterior. Atualize primeiro o Power PDF.",
    'da-DK': "Add-on Store kræver Tungsten Power PDF {v} eller nyere. Opdater Power PDF først.",
    'fi-FI': "Add-on Store vaatii Tungsten Power PDF:n version {v} tai uudemman. Päivitä ensin Power PDF.",
    'nb-NO': "Add-on Store krever Tungsten Power PDF {v} eller nyere. Oppdater Power PDF først.",
    'sv-SE': "Add-on Store kräver Tungsten Power PDF {v} eller senare. Uppdatera Power PDF först.",
    'pl-PL': "Add-on Store wymaga programu Tungsten Power PDF w wersji {v} lub nowszej. Najpierw zaktualizuj Power PDF.",
    'cs-CZ': "Add-on Store vyžaduje Tungsten Power PDF {v} nebo novější. Nejprve aktualizujte Power PDF.",
    'hu-HU': "Az Add-on Store a Tungsten Power PDF {v} vagy újabb verzióját igényli. Először frissítse a Power PDF-et.",
    'ru-RU': "Для Add-on Store требуется Tungsten Power PDF {v} или новее. Сначала обновите Power PDF.",
    'tr-TR': "Add-on Store, Tungsten Power PDF {v} veya üstünü gerektirir. Lütfen önce Power PDF'i güncelleyin.",
}

# Editions (C1.1.3): Power PDF 2025 loads plug-ins in Business only; from 2026.4 every
# edition does. Keep in sync with Product.wxs (PPDFSHORTVER) and common/hostversion.h.
ALL_EDITIONS_FROM = '2026.4'
EDITION = {
    'en-US': "Add-ons need Tungsten Power PDF Business {v} or later, or any edition (Standard, Advanced, Business) from {e} on. Please update Power PDF first.",
    'de-DE': "Add-ons benötigen Tungsten Power PDF Business ab {v} oder eine beliebige Edition (Standard, Advanced, Business) ab {e}. Bitte aktualisieren Sie zuerst Power PDF.",
    'fr-FR': "Les modules complémentaires nécessitent Tungsten Power PDF Business {v} ou ultérieur, ou toute édition (Standard, Advanced, Business) à partir de {e}. Mettez d'abord Power PDF à jour.",
    'it-IT': "I componenti aggiuntivi richiedono Tungsten Power PDF Business {v} o successivo, oppure qualsiasi edizione (Standard, Advanced, Business) dalla {e}. Aggiornare prima Power PDF.",
    'es-ES': "Los complementos necesitan Tungsten Power PDF Business {v} o posterior, o cualquier edición (Standard, Advanced, Business) a partir de {e}. Actualice primero Power PDF.",
    'nl-NL': "Invoegtoepassingen vereisen Tungsten Power PDF Business {v} of hoger, of elke editie (Standard, Advanced, Business) vanaf {e}. Werk eerst Power PDF bij.",
    'pt-BR': "Os suplementos requerem o Tungsten Power PDF Business {v} ou posterior, ou qualquer edição (Standard, Advanced, Business) a partir da {e}. Atualize primeiro o Power PDF.",
    'da-DK': "Tilføjelsesprogrammer kræver Tungsten Power PDF Business {v} eller nyere eller en vilkårlig udgave (Standard, Advanced, Business) fra {e}. Opdater Power PDF først.",
    'fi-FI': "Lisäosat vaativat Tungsten Power PDF Business -version {v} tai uudemman, tai minkä tahansa version (Standard, Advanced, Business) {e} alkaen. Päivitä ensin Power PDF.",
    'nb-NO': "Tillegg krever Tungsten Power PDF Business {v} eller nyere, eller en hvilken som helst utgave (Standard, Advanced, Business) fra {e}. Oppdater Power PDF først.",
    'sv-SE': "Tillägg kräver Tungsten Power PDF Business {v} eller senare, eller valfri utgåva (Standard, Advanced, Business) från {e}. Uppdatera Power PDF först.",
    'pl-PL': "Dodatki wymagają programu Tungsten Power PDF Business {v} lub nowszego albo dowolnej edycji (Standard, Advanced, Business) od {e}. Najpierw zaktualizuj Power PDF.",
    'cs-CZ': "Doplňky vyžadují Tungsten Power PDF Business {v} nebo novější, případně libovolnou edici (Standard, Advanced, Business) od verze {e}. Nejprve aktualizujte Power PDF.",
    'hu-HU': "A bővítményekhez Tungsten Power PDF Business {v} vagy újabb, illetve {e} verziótól bármely kiadás (Standard, Advanced, Business) szükséges. Először frissítse a Power PDF-et.",
    'ru-RU': "Для надстроек требуется Tungsten Power PDF Business {v} или новее либо любая редакция (Standard, Advanced, Business) начиная с {e}. Сначала обновите Power PDF.",
    'tr-TR': "Eklentiler için Tungsten Power PDF Business {v} veya üstü ya da {e} sürümünden itibaren herhangi bir sürüm (Standard, Advanced, Business) gerekir. Lütfen önce Power PDF'i güncelleyin.",
}


def main():
    os.makedirs(OUT, exist_ok=True)
    for culture, lcid, cp in CULTURES:
        no_ppdf, close, downgrade, note = TEXT[culture]
        too_old = TOO_OLD[culture].format(v=MIN_PPDF)
        edition = EDITION[culture].format(v=MIN_PPDF, e=ALL_EDITIONS_FROM)
        for t in (no_ppdf, close, downgrade, note):
            assert '\u2014' not in t, culture
        wxl = (f'<?xml version="1.0" encoding="utf-8"?>\n'
               f'<!-- generated by make_l10n.py; edit there -->\n'
               f'<WixLocalization Culture="{culture}" Codepage="{cp}" xmlns="http://schemas.microsoft.com/wix/2006/localization">\n'
               f'  <String Id="ProductLanguage">{lcid}</String>\n'
               f'  <String Id="NoPowerPdf">{xml(no_ppdf)}</String>\n'
               f'  <String Id="PowerPdfTooOld">{xml(too_old)}</String>\n'
               f'  <String Id="PowerPdfEdition">{xml(edition)}</String>\n'
               f'  <String Id="ClosePowerPdf">{xml(close)}</String>\n'
               f'  <String Id="Downgrade">{xml(downgrade)}</String>\n'
               f'</WixLocalization>\n')
        with open(os.path.join(OUT, f'{culture}.wxl'), 'w', encoding='utf-8') as f:
            f.write(wxl)
        rtf = ('{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0 Arial;}}\\fs18 '
               '{\\b Add-on Store for Tungsten Power PDF}\\par\\par\n'
               + rtf_text(note) + '\\par\\par\n' + rtf_text(MIT) + '\\par\n}')
        with open(os.path.join(OUT, f'license-{culture}.rtf'), 'w', encoding='ascii') as f:
            f.write(rtf)
    print(f'{len(CULTURES)} cultures written to {OUT}')


if __name__ == '__main__':
    main()
