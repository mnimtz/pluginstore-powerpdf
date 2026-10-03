# add_strings_0_3.py - adds the v0.3 UI strings (status names, time zone,
# installer hint, client lane notices) to every SharedResource.<lang>.resx.
import html
import os

os.chdir(os.path.join(os.path.dirname(__file__), '..', 'server', 'src', 'AddonStore.Web', 'Resources'))

L = ['de', 'fr', 'it', 'es', 'nl', 'pt', 'da', 'fi', 'nb', 'sv', 'pl', 'cs', 'hu', 'ru', 'tr']

T = {
 "Active": ["Aktiv", "Actif", "Attivo", "Activo", "Actief", "Ativo", "Aktiv", "Aktiivinen", "Aktiv", "Aktiv",
            "Aktywne", "Aktivní", "Aktív", "Активна", "Etkin"],
 "Beta": ["Beta"] * 15,
 "Disabled": ["Deaktiviert", "Désactivé", "Disattivato", "Desactivado", "Uitgeschakeld", "Desativado",
              "Deaktiveret", "Poistettu käytöstä", "Deaktivert", "Inaktiverad", "Wyłączone", "Zakázáno",
              "Letiltva", "Отключена", "Devre dışı"],
 "Download and run the installer (administrator rights required); afterwards the add-on keeps itself and your plugins up to date.": [
    "Installationsprogramm herunterladen und ausführen (Administratorrechte erforderlich); danach hält das Add-on sich und Ihre Plug-ins aktuell.",
    "Téléchargez et exécutez le programme d'installation (droits d'administrateur requis) ; ensuite, le module garde lui-même et vos plugins à jour.",
    "Scaricare ed eseguire il programma di installazione (sono necessari diritti di amministratore); in seguito il componente aggiuntivo mantiene aggiornati sé stesso e i plugin.",
    "Descargue y ejecute el instalador (se requieren derechos de administrador); después, el complemento se mantiene actualizado junto con sus plugins.",
    "Download en start het installatieprogramma (beheerdersrechten vereist); daarna houdt de add-on zichzelf en uw plug-ins actueel.",
    "Transfira e execute o instalador (são necessários direitos de administrador); depois disso, o complemento mantém-se a si próprio e aos seus plugins atualizados.",
    "Download og kør installationsprogrammet (administratorrettigheder kræves); derefter holder tilføjelsen sig selv og dine plugins opdateret.",
    "Lataa ja suorita asennusohjelma (järjestelmänvalvojan oikeudet vaaditaan); sen jälkeen lisäosa pitää itsensä ja laajennukset ajan tasalla.",
    "Last ned og kjør installasjonsprogrammet (administratorrettigheter kreves); deretter holder tillegget seg selv og programtilleggene oppdatert.",
    "Ladda ner och kör installationsprogrammet (administratörsbehörighet krävs); därefter håller tillägget sig självt och dina plugin-program uppdaterade.",
    "Pobierz i uruchom instalator (wymagane uprawnienia administratora); następnie dodatek będzie aktualizował siebie i wtyczki.",
    "Stáhněte a spusťte instalační program (vyžadována práva správce); poté doplněk udržuje sebe i vaše pluginy aktuální.",
    "Töltse le és futtassa a telepítőt (rendszergazdai jogosultság szükséges); ezután a bővítmény naprakészen tartja magát és a beépülő modulokat.",
    "Скачайте и запустите установщик (требуются права администратора); после этого надстройка будет обновлять себя и ваши плагины.",
    "Yükleyiciyi indirip çalıştırın (yönetici hakları gerekir); ardından eklenti kendini ve eklentilerinizi güncel tutar."],
 "Live": ["Live", "En ligne", "Pubblicato", "Publicado", "Live", "Publicado", "Live", "Julkaistu", "Publisert",
          "Publicerad", "Opublikowane", "Zveřejněno", "Élő", "Опубликовано", "Yayında"],
 "Only administrators can publish new versions of the Plugin-Store client.": [
    "Nur Administratoren dürfen neue Versionen des Plugin-Store-Clients veröffentlichen.",
    "Seuls les administrateurs peuvent publier de nouvelles versions du client Plugin-Store.",
    "Solo gli amministratori possono pubblicare nuove versioni del client Plugin-Store.",
    "Solo los administradores pueden publicar nuevas versiones del cliente Plugin-Store.",
    "Alleen beheerders kunnen nieuwe versies van de Plugin-Store-client publiceren.",
    "Apenas administradores podem publicar novas versões do cliente Plugin-Store.",
    "Kun administratorer kan udgive nye versioner af Plugin-Store-klienten.",
    "Vain järjestelmänvalvojat voivat julkaista Plugin-Store-asiakasohjelman uusia versioita.",
    "Bare administratorer kan publisere nye versjoner av Plugin-Store-klienten.",
    "Endast administratörer kan publicera nya versioner av Plugin-Store-klienten.",
    "Tylko administratorzy mogą publikować nowe wersje klienta Plugin-Store.",
    "Nové verze klienta Plugin-Store mohou publikovat pouze správci.",
    "A Plugin-Store kliens új verzióit csak rendszergazdák tehetik közzé.",
    "Публиковать новые версии клиента Plugin-Store могут только администраторы.",
    "Plugin-Store istemcisinin yeni sürümlerini yalnızca yöneticiler yayımlayabilir."],
 "Pending": ["Beantragt", "En attente", "In attesa", "Pendiente", "In afwachting", "Pendente", "Afventer",
             "Odottaa", "Venter", "Väntar", "Oczekuje", "Čeká", "Függőben", "Ожидает", "Beklemede"],
 "Plugin-Store client released. Installed clients offer the update now.": [
    "Plugin-Store-Client veröffentlicht. Installierte Clients bieten das Update jetzt an.",
    "Client Plugin-Store publié. Les clients installés proposent désormais la mise à jour.",
    "Client Plugin-Store pubblicato. I client installati propongono ora l'aggiornamento.",
    "Cliente Plugin-Store publicado. Los clientes instalados ya ofrecen la actualización.",
    "Plugin-Store-client gepubliceerd. Geïnstalleerde clients bieden de update nu aan.",
    "Cliente Plugin-Store publicado. Os clientes instalados já oferecem a atualização.",
    "Plugin-Store-klienten er udgivet. Installerede klienter tilbyder nu opdateringen.",
    "Plugin-Store-asiakasohjelma julkaistu. Asennetut asiakasohjelmat tarjoavat nyt päivitystä.",
    "Plugin-Store-klienten er publisert. Installerte klienter tilbyr nå oppdateringen.",
    "Plugin-Store-klienten har publicerats. Installerade klienter erbjuder nu uppdateringen.",
    "Klient Plugin-Store został opublikowany. Zainstalowani klienci oferują teraz aktualizację.",
    "Klient Plugin-Store byl zveřejněn. Nainstalovaní klienti nyní nabízejí aktualizaci.",
    "A Plugin-Store kliens megjelent. A telepített kliensek most felajánlják a frissítést.",
    "Клиент Plugin-Store опубликован. Установленные клиенты теперь предлагают обновление.",
    "Plugin-Store istemcisi yayımlandı. Yüklü istemciler artık güncellemeyi sunuyor."],
 "Rejected": ["Abgelehnt", "Refusé", "Rifiutato", "Rechazado", "Afgewezen", "Rejeitado", "Afvist", "Hylätty",
              "Avvist", "Avvisad", "Odrzucone", "Zamítnuto", "Elutasítva", "Отклонено", "Reddedildi"],
 "Time": ["Zeit", "Heure", "Ora", "Hora", "Tijd", "Hora", "Tid", "Aika", "Tid", "Tid", "Czas", "Čas", "Idő",
          "Время", "Saat"],
 "Time zone for displayed times": [
    "Zeitzone für angezeigte Uhrzeiten", "Fuseau horaire des heures affichées", "Fuso orario per gli orari visualizzati",
    "Zona horaria de las horas mostradas", "Tijdzone voor weergegeven tijden", "Fuso horário das horas apresentadas",
    "Tidszone for viste klokkeslæt", "Näytettävien aikojen aikavyöhyke", "Tidssone for viste klokkeslett",
    "Tidszon för visade tider", "Strefa czasowa wyświetlanych godzin", "Časové pásmo zobrazovaných časů",
    "A megjelenített időpontok időzónája", "Часовой пояс для отображаемого времени", "Gösterilen saatler için saat dilimi"],
 "Withdrawn": ["Zurückgezogen", "Retiré", "Ritirato", "Retirado", "Teruggetrokken", "Retirado", "Trukket tilbage",
               "Peruttu", "Trukket tilbake", "Återkallad", "Wycofane", "Staženo", "Visszavonva", "Отозвано",
               "Geri çekildi"],
}

for i, lang in enumerate(L):
    p = f'SharedResource.{lang}.resx'
    s = open(p, encoding='utf-8').read()
    add = ''
    for key, vals in T.items():
        if f'name="{html.escape(key)}"' in s:
            continue
        add += f'  <data name="{html.escape(key)}" xml:space="preserve"><value>{html.escape(vals[i])}</value></data>\r\n'
    s = s.replace('</root>', add + '</root>')
    open(p, 'w', encoding='utf-8', newline='').write(s)
print('done')
