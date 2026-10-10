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
    # the five further Power PDF languages (C1.3.0)
    ('zh-CN', 2052, 936),
    ('zh-TW', 1028, 950),
    ('ja-JP', 1041, 932),
    ('ko-KR', 1042, 949),
    ('ar-SA', 1025, 1256),
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


# Texts of the five further Power PDF languages (C1.3.0)
TEXT.update({
    'zh-CN': ('在此计算机上未找到 Tungsten Power PDF。请先安装 Power PDF。',
              '请关闭 Tungsten Power PDF，然后继续。',
              '已安装较新版本的 Add-on Store。',
              '以下 MIT 许可证以其英文原文为准。'),
    'zh-TW': ('在此電腦上找不到 Tungsten Power PDF。請先安裝 Power PDF。',
              '請關閉 Tungsten Power PDF，然後繼續。',
              '已安裝較新版本的 Add-on Store。',
              '下列 MIT 授權以其英文原文為準。'),
    'ja-JP': ('このコンピューターで Tungsten Power PDF が見つかりませんでした。先に Power PDF をインストールしてください。',
              'Tungsten Power PDF を閉じてから続行してください。',
              'Add-on Store の新しいバージョンが既にインストールされています。',
              '以下の MIT ライセンスは、英語の原文が適用されます。'),
    'ko-KR': ('이 컴퓨터에서 Tungsten Power PDF를 찾을 수 없습니다. 먼저 Power PDF를 설치하십시오.',
              'Tungsten Power PDF를 닫고 계속하십시오.',
              '최신 버전의 Add-on Store가 이미 설치되어 있습니다.',
              '다음 MIT 라이선스는 영어 원문으로 적용됩니다.'),
    'ar-SA': ('لم يتم العثور على Tungsten Power PDF على هذا الكمبيوتر. يرجى تثبيت Power PDF أولاً.',
              'يرجى إغلاق Tungsten Power PDF والمتابعة.',
              'إصدار أحدث من Add-on Store مثبت بالفعل.',
              'ينطبق ترخيص MIT التالي بنصه الإنجليزي الأصلي.'),
})
TOO_OLD_X = {
    'zh-CN': 'Add-on Store 需要 Tungsten Power PDF {v} 或更高版本。请先更新 Power PDF。',
    'zh-TW': 'Add-on Store 需要 Tungsten Power PDF {v} 或更新版本。請先更新 Power PDF。',
    'ja-JP': 'Add-on Store には Tungsten Power PDF {v} 以降が必要です。先に Power PDF を更新してください。',
    'ko-KR': 'Add-on Store에는 Tungsten Power PDF {v} 이상이 필요합니다. 먼저 Power PDF를 업데이트하십시오.',
    'ar-SA': 'يحتاج Add-on Store إلى Tungsten Power PDF {v} أو أحدث. يرجى تحديث Power PDF أولاً.',
}
EDITION_X = {
    'zh-CN': '加载项需要 Tungsten Power PDF Business {v} 或更高版本，或者 {e} 及更高版本的任意版本（Standard、Advanced、Business）。请先更新 Power PDF。',
    'zh-TW': '增益集需要 Tungsten Power PDF Business {v} 或更新版本，或 {e} 起的任何版本 (Standard、Advanced、Business)。請先更新 Power PDF。',
    'ja-JP': 'アドインには Tungsten Power PDF Business {v} 以降、または {e} 以降の任意のエディション (Standard、Advanced、Business) が必要です。先に Power PDF を更新してください。',
    'ko-KR': '추가 기능에는 Tungsten Power PDF Business {v} 이상 또는 {e}부터의 모든 에디션(Standard, Advanced, Business)이 필요합니다. 먼저 Power PDF를 업데이트하십시오.',
    'ar-SA': 'تحتاج الوظائف الإضافية إلى Tungsten Power PDF Business {v} أو أحدث، أو أي إصدار (Standard و Advanced و Business) بدءًا من {e}. يرجى تحديث Power PDF أولاً.',
}
# PowerPdfSaasOnly (C1.9.6): the store is for Power PDF with sign-in (SaaS); a serial number is refused
SAAS_ONLY = {
    'en-US': 'The Add-on Store is available only for Tungsten Power PDF with sign-in (SaaS). This Power PDF is licensed with a serial number.',
    'de-DE': 'Der Add-on Store ist nur für Tungsten Power PDF mit Anmeldung (SaaS) verfügbar. Dieses Power PDF ist mit einer Seriennummer lizenziert.',
    'fr-FR': "L'Add-on Store est disponible uniquement pour Tungsten Power PDF avec connexion (SaaS). Ce Power PDF est sous licence avec un numéro de série.", 'it-IT': "L'Add-on Store è disponibile solo per Tungsten Power PDF con accesso (SaaS). Questo Power PDF è concesso in licenza con un numero di serie.", 'es-ES': 'Add-on Store solo está disponible para Tungsten Power PDF con inicio de sesión (SaaS). Este Power PDF tiene licencia con un número de serie.',
    'nl-NL': 'De Add-on Store is alleen beschikbaar voor Tungsten Power PDF met aanmelding (SaaS). Deze Power PDF is gelicentieerd met een serienummer.',
    'pt-BR': 'A Add-on Store está disponível apenas para o Tungsten Power PDF com entrada (SaaS). Este Power PDF está licenciado com um número de série.',
    'da-DK': 'Add-on Store er kun tilgængelig for Tungsten Power PDF med logon (SaaS). Denne Power PDF er licenseret med et serienummer.',
    'fi-FI': 'Add-on Store on käytettävissä vain Tungsten Power PDF:lle, johon kirjaudutaan (SaaS). Tämä Power PDF on lisensoitu sarjanumerolla.',
    'nb-NO': 'Add-on Store er bare tilgjengelig for Tungsten Power PDF med pålogging (SaaS). Denne Power PDF er lisensiert med et serienummer.',
    'sv-SE': 'Add-on Store är bara tillgänglig för Tungsten Power PDF med inloggning (SaaS). Den här Power PDF är licensierad med ett serienummer.',
    'pl-PL': 'Add-on Store jest dostępny tylko dla programu Tungsten Power PDF z logowaniem (SaaS). Ten Power PDF jest licencjonowany numerem seryjnym.',
    'cs-CZ': 'Add-on Store je k dispozici pouze pro Tungsten Power PDF s přihlášením (SaaS). Tento Power PDF je licencován sériovým číslem.',
    'hu-HU': 'Az Add-on Store csak bejelentkezéses (SaaS) Tungsten Power PDF-hez érhető el. Ez a Power PDF sorozatszámmal van licencelve.',
    'ru-RU': 'Add-on Store доступен только для Tungsten Power PDF со входом в учетную запись (SaaS). Этот Power PDF лицензирован серийным номером.',
    'tr-TR': 'Add-on Store yalnızca oturum açmalı (SaaS) Tungsten Power PDF için kullanılabilir. Bu Power PDF bir seri numarasıyla lisanslanmıştır.',
    'zh-CN': 'Add-on Store 仅适用于需要登录的 Tungsten Power PDF (SaaS)。此 Power PDF 使用序列号授权。',
    'zh-TW': 'Add-on Store 僅適用於需要登入的 Tungsten Power PDF (SaaS)。此 Power PDF 使用序號授權。',
    'ja-JP': 'Add-on Store は、サインインして使用する Tungsten Power PDF (SaaS) でのみ利用できます。この Power PDF はシリアル番号でライセンスされています。',
    'ko-KR': 'Add-on Store는 로그인하여 사용하는 Tungsten Power PDF(SaaS)에서만 사용할 수 있습니다. 이 Power PDF는 일련 번호로 라이선스가 부여되어 있습니다.',
    'ar-SA': 'يتوفر Add-on Store فقط لـ Tungsten Power PDF مع تسجيل الدخول (SaaS). هذا الإصدار من Power PDF مرخّص برقم تسلسلي.',
}
# SaasOnlyDlg (C1.9.8): what a serial-number user can do, the button text and the quote page.
# Tungsten's quote page exists in English, German and French only (hreflang of the page, Oct 10, 2026).
SAAS_HINT = {
    'en-US': 'To use the Add-on Store, you need Power PDF Business SaaS. Request a quote from Tungsten Automation; the page opens in your web browser.',
    'de-DE': 'Für den Add-on Store benötigen Sie Power PDF Business SaaS. Fordern Sie ein Angebot bei Tungsten Automation an; die Seite öffnet sich in Ihrem Browser.',
    'fr-FR': "Pour utiliser l'Add-on Store, vous avez besoin de Power PDF Business SaaS. Demandez un devis à Tungsten Automation ; la page s'ouvre dans votre navigateur.",
    'it-IT': "Per usare l'Add-on Store è necessario Power PDF Business SaaS. Richiedi un preventivo a Tungsten Automation; la pagina si apre nel browser.",
    'es-ES': 'Para usar Add-on Store necesita Power PDF Business SaaS. Solicite un presupuesto a Tungsten Automation; la página se abre en su navegador.',
    'nl-NL': 'Voor de Add-on Store hebt u Power PDF Business SaaS nodig. Vraag een offerte aan bij Tungsten Automation; de pagina wordt in uw browser geopend.',
    'pt-BR': 'Para usar a Add-on Store, você precisa do Power PDF Business SaaS. Solicite uma cotação à Tungsten Automation; a página será aberta no seu navegador.',
    'da-DK': 'For at bruge Add-on Store skal du have Power PDF Business SaaS. Anmod Tungsten Automation om et tilbud; siden åbnes i din browser.',
    'fi-FI': 'Add-on Storen käyttö edellyttää Power PDF Business SaaS -versiota. Pyydä tarjous Tungsten Automationilta; sivu avautuu selaimeesi.',
    'nb-NO': 'For å bruke Add-on Store trenger du Power PDF Business SaaS. Be Tungsten Automation om et tilbud; siden åpnes i nettleseren.',
    'sv-SE': 'För att använda Add-on Store behöver du Power PDF Business SaaS. Begär en offert från Tungsten Automation; sidan öppnas i webbläsaren.',
    'pl-PL': 'Do korzystania z Add-on Store potrzebny jest Power PDF Business SaaS. Poproś Tungsten Automation o ofertę; strona otworzy się w przeglądarce.',
    'cs-CZ': 'Pro Add-on Store potřebujete Power PDF Business SaaS. Vyžádejte si nabídku od společnosti Tungsten Automation; stránka se otevře v prohlížeči.',
    'hu-HU': 'Az Add-on Store használatához Power PDF Business SaaS szükséges. Kérjen árajánlatot a Tungsten Automationtől; az oldal a böngészőben nyílik meg.',
    'ru-RU': 'Для работы с Add-on Store нужен Power PDF Business SaaS. Запросите предложение у Tungsten Automation; страница откроется в браузере.',
    'tr-TR': "Add-on Store'u kullanmak için Power PDF Business SaaS gerekir. Tungsten Automation'dan teklif isteyin; sayfa tarayıcınızda açılır.",
    'zh-CN': '使用 Add-on Store 需要 Power PDF Business SaaS。请向 Tungsten Automation 申请报价；页面将在浏览器中打开。',
    'zh-TW': '使用 Add-on Store 需要 Power PDF Business SaaS。請向 Tungsten Automation 索取報價；頁面會在瀏覽器中開啟。',
    'ja-JP': 'Add-on Store を使用するには Power PDF Business SaaS が必要です。Tungsten Automation に見積もりを依頼してください。ページはブラウザーで開きます。',
    'ko-KR': 'Add-on Store를 사용하려면 Power PDF Business SaaS가 필요합니다. Tungsten Automation에 견적을 요청하세요. 페이지가 브라우저에서 열립니다.',
    'ar-SA': 'لاستخدام Add-on Store تحتاج إلى Power PDF Business SaaS. اطلب عرض سعر من Tungsten Automation؛ تُفتح الصفحة في المتصفح.',
}
SAAS_QUOTE = {
    'en-US': 'Request a quote', 'de-DE': 'Angebot anfordern', 'fr-FR': 'Demander un devis', 'it-IT': 'Richiedi un preventivo',
    'es-ES': 'Solicitar presupuesto', 'nl-NL': 'Offerte aanvragen', 'pt-BR': 'Solicitar cotação', 'da-DK': 'Anmod om tilbud',
    'fi-FI': 'Pyydä tarjous', 'nb-NO': 'Be om tilbud', 'sv-SE': 'Begär offert', 'pl-PL': 'Poproś o ofertę',
    'cs-CZ': 'Vyžádat nabídku', 'hu-HU': 'Árajánlat kérése', 'ru-RU': 'Запросить предложение', 'tr-TR': 'Teklif isteyin',
    'zh-CN': '申请报价', 'zh-TW': '索取報價', 'ja-JP': '見積もりを依頼', 'ko-KR': '견적 요청', 'ar-SA': 'طلب عرض سعر',
}
QUOTE_PATH = '/products/power-pdf/business-request-a-quote'
QUOTE_HOST = {'de-DE': 'https://www.tungstenautomation.de', 'fr-FR': 'https://www.tungstenautomation.fr'}


def quote_url(culture):
    return QUOTE_HOST.get(culture, 'https://www.tungstenautomation.com') + QUOTE_PATH


TOO_OLD.update(TOO_OLD_X)
EDITION.update(EDITION_X)


def main():
    os.makedirs(OUT, exist_ok=True)
    for culture, lcid, cp in CULTURES:
        no_ppdf, close, downgrade, note = TEXT[culture]
        too_old = TOO_OLD[culture].format(v=MIN_PPDF)
        edition = EDITION[culture].format(v=MIN_PPDF, e=ALL_EDITIONS_FROM)
        for t in (no_ppdf, close, downgrade, note, SAAS_HINT[culture], SAAS_QUOTE[culture]):
            assert '\u2014' not in t, culture
        wxl = (f'<?xml version="1.0" encoding="utf-8"?>\n'
               f'<!-- generated by make_l10n.py; edit there -->\n'
               f'<WixLocalization Culture="{culture}" Codepage="{cp}" xmlns="http://schemas.microsoft.com/wix/2006/localization">\n'
               f'  <String Id="ProductLanguage">{lcid}</String>\n'
               f'  <String Id="NoPowerPdf">{xml(no_ppdf)}</String>\n'
               f'  <String Id="PowerPdfTooOld">{xml(too_old)}</String>\n'
               f'  <String Id="PowerPdfEdition">{xml(edition)}</String>\n'
               f'  <String Id="PowerPdfSaasOnly">{xml(SAAS_ONLY[culture])}</String>\n'
               f'  <String Id="SaasHint">{xml(SAAS_HINT[culture])}</String>\n'
               f'  <String Id="SaasQuote">{xml(SAAS_QUOTE[culture])}</String>\n'
               f'  <String Id="SaasQuoteUrl">{xml(quote_url(culture))}</String>\n'
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
