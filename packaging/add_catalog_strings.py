# add_catalog_strings.py - one-time: adds the search/filter/category labels to
# every SharedResource.<lang>.resx. Run from the repo root.
import html
import os

os.chdir(os.path.join(os.path.dirname(__file__), '..', 'server', 'src', 'AddonStore.Web', 'Resources'))

T = {
 'Search':        dict(de='Suchen', fr='Rechercher', it='Cerca', es='Buscar', nl='Zoeken', pt='Pesquisar',
                       da='Søg', fi='Hae', nb='Søk', sv='Sök', pl='Szukaj', cs='Hledat', hu='Keresés',
                       ru='Поиск', tr='Ara'),
 'All':           dict(de='Alle', fr='Tous', it='Tutti', es='Todos', nl='Alle', pt='Todos',
                       da='Alle', fi='Kaikki', nb='Alle', sv='Alla', pl='Wszystkie', cs='Vše', hu='Összes',
                       ru='Все', tr='Tümü'),
 'Conversion':    dict(de='Konvertierung', fr='Conversion', it='Conversione', es='Conversión', nl='Conversie',
                       pt='Conversão', da='Konvertering', fi='Muunnos', nb='Konvertering', sv='Konvertering',
                       pl='Konwersja', cs='Převod', hu='Konvertálás', ru='Преобразование', tr='Dönüştürme'),
 'Forms':         dict(de='Formulare', fr='Formulaires', it='Moduli', es='Formularios', nl='Formulieren',
                       pt='Formulários', da='Formularer', fi='Lomakkeet', nb='Skjemaer', sv='Formulär',
                       pl='Formularze', cs='Formuláře', hu='Űrlapok', ru='Формы', tr='Formlar'),
 'Signing':       dict(de='Signieren', fr='Signature', it='Firma', es='Firma', nl='Ondertekenen',
                       pt='Assinatura', da='Signering', fi='Allekirjoitus', nb='Signering', sv='Signering',
                       pl='Podpisywanie', cs='Podepisování', hu='Aláírás', ru='Подписание', tr='İmzalama'),
 'Navigation':    dict(de='Navigation', fr='Navigation', it='Navigazione', es='Navegación', nl='Navigatie',
                       pt='Navegação', da='Navigation', fi='Navigointi', nb='Navigasjon', sv='Navigering',
                       pl='Nawigacja', cs='Navigace', hu='Navigáció', ru='Навигация', tr='Gezinme'),
 'Printing':      dict(de='Drucken', fr='Impression', it='Stampa', es='Impresión', nl='Afdrukken',
                       pt='Impressão', da='Udskrivning', fi='Tulostus', nb='Utskrift', sv='Utskrift',
                       pl='Drukowanie', cs='Tisk', hu='Nyomtatás', ru='Печать', tr='Yazdırma'),
 'Productivity':  dict(de='Produktivität', fr='Productivité', it='Produttività', es='Productividad',
                       nl='Productiviteit', pt='Produtividade', da='Produktivitet', fi='Tuottavuus',
                       nb='Produktivitet', sv='Produktivitet', pl='Produktywność', cs='Produktivita',
                       hu='Produktivitás', ru='Продуктивность', tr='Üretkenlik'),
 'System':        dict(de='System', fr='Système', it='Sistema', es='Sistema', nl='Systeem', pt='Sistema',
                       da='System', fi='Järjestelmä', nb='System', sv='System', pl='System', cs='Systém',
                       hu='Rendszer', ru='Система', tr='Sistem'),
 'Other':         dict(de='Sonstiges', fr='Autres', it='Altro', es='Otros', nl='Overig', pt='Outros',
                       da='Andet', fi='Muut', nb='Annet', sv='Övrigt', pl='Inne', cs='Ostatní', hu='Egyéb',
                       ru='Прочее', tr='Diğer'),
}

langs = ['de', 'fr', 'it', 'es', 'nl', 'pt', 'da', 'fi', 'nb', 'sv', 'pl', 'cs', 'hu', 'ru', 'tr']
for lang in langs:
    p = f'SharedResource.{lang}.resx'
    s = open(p, encoding='utf-8').read()
    add = ''
    for key, trans in T.items():
        if f'name="{key}"' in s:
            continue
        add += f'  <data name="{key}" xml:space="preserve"><value>{html.escape(trans[lang])}</value></data>\n'
    s = s.replace('</root>', add + '</root>')
    open(p, 'w', encoding='utf-8').write(s)
    print(lang, 'ok')
print('done')
