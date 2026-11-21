"""help_page_test.py - the central help page (client/ui/help.html) with a mock host in Edge headless (C2.1.0).

    python -I client/tests/help_page_test.py [--shot out.png]

Loads the inlined page (build.cmd writes ui/help.html) with a fake window.chrome.webview, sends what the
host sends (init, docs, show) with the client's own help, the Mail Merge test help when present and a
hostile help, and checks in the page: chapters on the left, search while typing (several words, no case,
no accents), results with add-on, chapter and marks, arrow keys and Enter, Esc, jumps by topic and id,
links (https only through the host, never navigating), F1 once per key press, the second cleaning (no
script, no event attribute, no foreign image, no javascript: link), no JavaScript error. Exit code 1 on a
failure. --shot also writes a screenshot of the search state.
"""
import json
import os
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
CLIENT = os.path.normpath(os.path.join(HERE, '..'))
PAGE = os.path.join(CLIENT, 'ui', 'help.html')
OWN = os.path.join(CLIENT, 'docs', 'help')
MM = r'C:\Claude\EnhancedFeaturePack\mailmerge\help'
EDGE = [r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe', r'C:\Program Files\Microsoft\Edge\Application\msedge.exe']

EVIL = ('<h1 id="evil">Evil chapter</h1><p onclick="window.__pwned=1">Klick</p><script>window.__pwned=2</script>'
        '<img src="https://evil.example/x.png" onerror="window.__pwned=3"><img src="data:image/svg+xml;base64,PHN2Zz4=">'
        '<a href="javascript:window.__pwned=4">js</a><a href="https://www.tungstenautomation.com/">ok</a>'
        '<iframe src="https://evil.example"></iframe><svg><script>window.__pwned=5</script></svg>'
        '<p style="background:url(https://evil.example/t)">styled</p><form action="https://evil.example"><input></form>'
        '<h2 id="evil-sec">Zweiter Abschnitt</h2><p>Unbedenklicher Text mit Umlaut Größe.</p>')

STRINGS = {"title": "Hilfe zu den Add-ons", "search": "Hilfe durchsuchen", "hits": "%s Treffer", "noHits": "Nichts gefunden für: %s",
           "empty": "Noch keine Hilfe installiert.", "contents": "Inhalt", "print": "Kapitel drucken", "link": "Öffnet sich im Webbrowser",
           "hint": "Tippen Sie, um alle Hilfetexte zugleich zu durchsuchen.", "prev": "Zurück", "next": "Weiter", "help": "Hilfe", "clear": "Suche leeren"}


def body(path):
    t = open(path, encoding='utf-8').read()
    a = t.find('<body>')
    b = t.rfind('</body>')
    return t[a + 6:b] if a >= 0 and b > a else t


def main():
    shot = sys.argv[sys.argv.index('--shot') + 1] if '--shot' in sys.argv else None
    edge = next((e for e in EDGE if os.path.exists(e)), None)
    if not edge or not os.path.exists(PAGE):
        print('help_page_test: Edge or ui/help.html missing (run client\\build.cmd)')
        return 1
    own_meta = json.load(open(os.path.join(OWN, 'help.json'), encoding='utf-8'))
    items = [{"id": "com.tungsten.pluginstore", "name": "Add-on Store", "lang": "de", "topics": own_meta['topics'], "html": body(os.path.join(OWN, 'de.html'))}]
    if os.path.exists(os.path.join(MM, 'de.html')):
        mm = json.load(open(os.path.join(MM, 'help.json'), encoding='utf-8'))
        items.append({"id": "com.tungsten.mailmerge", "name": "Serienbrief", "lang": "de", "topics": mm['topics'], "html": body(os.path.join(MM, 'de.html'))})
    items.append({"id": "com.x.evil", "name": "Böses Add-on", "lang": "de", "topics": {}, "html": EVIL})
    msgs = {"init": {"type": "init", "version": "2.1.0", "lang": "de-DE", "self": "com.tungsten.pluginstore", "strings": STRINGS},
            "docs": {"type": "docs", "items": items}}
    mock = ('<script>window.__posts=[];window.__errors=[];window.onerror=function(m,s,l){__errors.push(m+" @"+l);};'
            'window.chrome={webview:{postMessage:function(m){__posts.push(JSON.parse(JSON.stringify(m)));},'
            'addEventListener:function(t,f){window.__host=f;}}};'
            'window.print=function(){__posts.push({cmd:"print"});};</script>')
    test = r'''<script>
(function(){
var R=[];function ok(c,n){R.push((c?"PASS ":"FAIL ")+n);}
function send(m){window.__host({data:m});}
function key(k,o){var e=new KeyboardEvent("keydown",Object.assign({key:k,bubbles:true,cancelable:true},o||{}));(document.activeElement||document).dispatchEvent(e);return e;}
function type(t){var q=document.getElementById("q");q.focus();q.value=t;q.dispatchEvent(new Event("input",{bubbles:true}));}
var M=__MSGS__;
try{
 ok(__posts.length===1&&__posts[0].cmd==="ready","page says ready");
 send(M.init);send(M.docs);send({type:"show",addon:"",id:""});
 ok(document.getElementById("title").textContent==="Hilfe zu den Add-ons","title from init");
 ok(document.querySelectorAll("nav .addon").length===M.docs.items.length,"every help on the left");
 ok(document.querySelectorAll(".card").length===M.docs.items.length,"start page lists the helps");
 // the second cleaning
 var all=document.querySelectorAll("nav .ab");document.querySelectorAll(".card")[M.docs.items.length-1].click();
 var v=document.getElementById("view");
 ok(!v.querySelector("script,iframe,svg,form,input,style"),"no active elements from a help");
 var bad=0;v.querySelectorAll("*").forEach(function(e){for(var i=0;i<e.attributes.length;i++){var a=e.attributes[i].name;if(/^on|^style$|^action$/.test(a))bad++;}});
 ok(bad===0,"no event or style attributes");
 ok(!v.querySelector("img"),"no foreign or svg pictures");
 ok(!/javascript:/i.test(v.innerHTML),"no javascript: links");
 ok(window.__pwned===undefined,"nothing of the hostile help ran");
 var links=v.querySelectorAll("a[data-url]");ok(links.length===1,"one https link kept");
 var n=__posts.length;links[0].click();ok(__posts.length===n+1&&__posts[n].cmd==="open"&&__posts[n].url==="https://www.tungstenautomation.com/","https link goes to the host");
 ok(location.href.indexOf("about:")===0||location.protocol==="file:","page did not navigate");
 // jumps: topic name and section id
 send({type:"show",addon:"com.tungsten.pluginstore",id:"codes"});
 ok(/10 /.test(v.querySelector(".doc h1").textContent),"topic codes opens chapter 10");
 send({type:"show",addon:"com.tungsten.pluginstore",id:"for-administrators-policies"});
 ok(/14 /.test(v.querySelector(".doc h1").textContent),"section id opens chapter 14");
 ok(document.querySelector("nav .ch.on")!==null,"current chapter marked on the left");
 send({type:"show",addon:"com.x.unknown",id:""});ok(document.querySelector(".start")!==null,"unknown add-on: start page");
 // search while typing: several words, no case, no accents
 type("KUNDENCODE entfernen");
 setTimeout(function(){
  var hits=v.querySelectorAll(".hit");ok(hits.length>0,"results while typing ("+hits.length+")");
  ok(v.querySelector(".res-head").textContent.indexOf("Treffer")>0,"result count");
  ok(hits[0].querySelector(".where").textContent.indexOf("\u203a")>0,"result names add-on and chapter");
  ok(v.querySelectorAll(".hit mark").length>0,"results highlight the words");
  ok(hits[0].classList.contains("sel"),"first result selected");
  key("ArrowDown");ok(v.querySelectorAll(".hit")[1]&&v.querySelectorAll(".hit")[1].classList.contains("sel"),"arrow down selects the next");
  key("ArrowUp");ok(v.querySelectorAll(".hit")[0].classList.contains("sel"),"arrow up back");
  key("Enter");
  ok(v.querySelector(".doc")!==null,"Enter opens the result");
  ok(v.querySelectorAll(".doc mark").length>0,"opened chapter highlights the words");
  ok(v.querySelector(".doc mark.cur")!==null,"current hit marked");
  type("grosse umlaut");
  setTimeout(function(){
   ok(v.querySelectorAll(".hit").length>=1,"accents ignored (grosse finds Größe)");
   type("xyzzyquux");
   setTimeout(function(){
    ok(v.querySelector(".res-head").textContent.indexOf("Nichts gefunden")===0,"no hits message");
    key("Escape");ok(document.getElementById("q").value===""&&!v.querySelector(".hit"),"Esc clears the search");
    // F1: own help, once per key press
    var c=__posts.length;key("F1");key("F1",{repeat:true});key("F1",{repeat:true});
    ok(/1 /.test(v.querySelector(".doc h1").textContent),"F1 shows the own help chapter 1");
    ok(document.querySelector("#overview-and-terms-help")!==null,"F1 lands at the Help section");
    ok(__posts.length===c,"F1 needs no host message");
    // pager and print
    var nx=v.querySelector(".pager .btn");ok(nx!==null,"pager");if(nx)nx.click();ok(/2 /.test(v.querySelector(".doc h1").textContent),"next chapter");
    var pr=v.querySelector(".crumb .hbtn");var p0=__posts.length;pr.click();ok(__posts.length===p0+1&&__posts[p0].cmd==="print","print chapter");
    // F5 asks the host to reload; a reload keeps the chapter
    var f=__posts.length;key("F5");ok(__posts[f]&&__posts[f].cmd==="reload","F5 reloads");
    send(M.docs);ok(/2 /.test(v.querySelector(".doc h1").textContent),"reload keeps the chapter");
    // broken messages do not break the page
    send(null);send({type:"docs",items:[{id:5,html:null},"x",{id:"a.b.c",name:"x",html:"<h1>ok</h1>"}]});
    ok(document.querySelectorAll("nav .addon").length===1,"bad items skipped");
    type("ok");
    setTimeout(function(){
     type("Serienbrief");
     ok(__errors.length===0,"no JavaScript error "+__errors.join("; "));
     send(M.docs);type("Kundencode");
     setTimeout(function(){ fin(); },200);
    },200);
   },200);
  },200);
 },200);
}catch(e){R.push("FAIL exception "+e.message);fin();}
function fin(){var p=document.createElement("pre");p.id="result";p.textContent=R.join("\n");document.body.appendChild(p);document.title="DONE";}
})();
</script>'''.replace('__MSGS__', json.dumps(msgs, ensure_ascii=False).replace('</', '<' + chr(92) + '/'))
    page = open(PAGE, encoding='utf-8').read()
    i = page.find('<script>')
    html = page[:i] + mock + page[i:]
    html = html.replace('</body>', test + '</body>')
    tmp = tempfile.mkdtemp(prefix='helptest_')
    path = os.path.join(tmp, 'help_test.html')
    open(path, 'w', encoding='utf-8').write(html)
    out = os.path.join(tmp, 'dom.html')
    url = 'file:///' + path.replace('\\', '/')
    cmd = '"%s" --headless=new --disable-gpu --user-data-dir="%s" --virtual-time-budget=8000 --dump-dom "%s" > "%s"' % (edge, os.path.join(tmp, 'e1'), url, out)
    subprocess.call(cmd, shell=True, timeout=120)
    dom = open(out, encoding='utf-8', errors='replace').read() if os.path.exists(out) else ''
    a = dom.find('<pre id="result">')
    if a < 0:
        print('help_page_test: no result in the page')
        return 1
    res = dom[a + 17:dom.find('</pre>', a)]
    import html as h
    res = h.unescape(res)
    print(res)
    if shot:
        # the search state for the eyes: the same page with the test stopped after a search
        stop = html.replace('send(M.docs);type("Kundencode");', 'send(M.docs);type("Kundencode entfernen");return;')
        p2 = os.path.join(tmp, 'shot.html')
        open(p2, 'w', encoding='utf-8').write(stop)
        subprocess.call([edge, '--headless=new', '--disable-gpu', '--user-data-dir=' + os.path.join(tmp, 'e2'), '--window-size=1200,800',
                         '--virtual-time-budget=8000', '--screenshot=' + os.path.abspath(shot), 'file:///' + p2.replace('\\', '/')], timeout=120)
        # and the chapter the first result opens (Enter), with the words highlighted
        stop2 = html.replace('send(M.docs);type("Kundencode");', 'send(M.docs);type("Kundencode entfernen");setTimeout(function(){key("Enter");},300);return;')
        p3 = os.path.join(tmp, 'shot2.html')
        open(p3, 'w', encoding='utf-8').write(stop2)
        base, ext = os.path.splitext(os.path.abspath(shot))
        subprocess.call([edge, '--headless=new', '--disable-gpu', '--user-data-dir=' + os.path.join(tmp, 'e3'), '--window-size=1200,800',
                         '--virtual-time-budget=8000', '--screenshot=' + base + '_chapter' + ext, 'file:///' + p3.replace('\\', '/')], timeout=120)
    fails = [l for l in res.splitlines() if l.startswith('FAIL')]
    print('help_page_test: %d checks, %d failed' % (len(res.splitlines()), len(fails)))
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())
