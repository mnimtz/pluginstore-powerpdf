/*tpui:begin*/
/* Tungsten Plug-in UI (tpui) 1.0.1 - behaviour shared by our Power PDF plug-in pages:
   host bridge, 21 languages with right-to-left, in-page dialogs (never alert/confirm/prompt),
   toasts, busy overlay, paged lists, menus, icons. No dependencies, no network.
   MIT License, see LICENSE. */
(function () {
  "use strict";
  var TPUI = { version: "1.0.1" };

  // ---- small helpers -------------------------------------------------------
  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function fmt(s) {
    var a = arguments;
    return String(s == null ? "" : s).replace(/\{(\d+)\}/g, function (m, i) { var v = a[+i + 1]; return v == null ? m : String(v); });
  }
  TPUI.el = el;
  TPUI.fmt = fmt;

  // ---- host bridge: JSON messages {type: ...} to and from the C++ side ---------
  var host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  var handlers = {};
  TPUI.inHost = !!host;
  TPUI.post = function (msg) {
    if (!msg || typeof msg.type !== "string") throw new Error("tpui: a message needs a type");
    if (host) host.postMessage(msg);
    else if (TPUI.onPreviewPost) TPUI.onPreviewPost(msg);   // browser preview
  };
  TPUI.on = function (type, fn) { (handlers[type] = handlers[type] || []).push(fn); };
  TPUI.receive = function (msg) {   // also used by previews to simulate the host
    if (!msg || typeof msg.type !== "string") return;
    (handlers[msg.type] || []).forEach(function (fn) { try { fn(msg); } catch (e) { if (window.console) console.error(e); } });
  };
  if (host) host.addEventListener("message", function (e) { TPUI.receive(e.data); });

  // ---- languages: strings from the host, [data-i18n] in the markup ---------------
  var S = {};
  var RTL = { ar: 1, he: 1, fa: 1, ur: 1 };
  TPUI.t = function (key) {
    var s = Object.prototype.hasOwnProperty.call(S, key) ? S[key] : key;
    var args = [s].concat(Array.prototype.slice.call(arguments, 1));
    return fmt.apply(null, args);
  };
  TPUI.setStrings = function (strings, lang) {
    S = strings || {};
    var l = String(lang || "en");
    document.documentElement.lang = l;
    document.documentElement.dir = RTL[l.split("-")[0]] ? "rtl" : "ltr";
    TPUI.translate(document);
  };
  TPUI.translate = function (root) {
    (root || document).querySelectorAll("[data-i18n]").forEach(function (n) { n.textContent = TPUI.t(n.getAttribute("data-i18n")); });
    (root || document).querySelectorAll("[data-i18n-title]").forEach(function (n) { n.title = TPUI.t(n.getAttribute("data-i18n-title")); });
    (root || document).querySelectorAll("[data-i18n-ph]").forEach(function (n) { n.placeholder = TPUI.t(n.getAttribute("data-i18n-ph")); });
    (root || document).querySelectorAll("[data-i18n-label]").forEach(function (n) { n.setAttribute("aria-label", TPUI.t(n.getAttribute("data-i18n-label"))); });
  };

  // ---- dialogs in the page -----------------------------------------------------
  // Cancel is focused first (safe default), Escape and a click on the veil cancel,
  // Tab stays inside the dialog. Returns a Promise.
  var openDialogs = 0;
  function dialog(o) {
    return new Promise(function (resolve) {
      var veil = el("div", "tp-veil"), box = el("div", "tp-dialog");
      box.setAttribute("role", o.input ? "dialog" : "alertdialog");
      box.setAttribute("aria-modal", "true");
      if (o.title) { var h = el("h3", "", o.title); h.id = "tp-dlg-t" + (++openDialogs); box.setAttribute("aria-labelledby", h.id); box.appendChild(h); }
      if (o.text) box.appendChild(el("p", "", o.text));
      var input = null;
      if (o.input) {
        var lab = el("label", "field");
        if (o.label) lab.appendChild(el("span", "", o.label));
        input = el("input");
        input.type = o.inputType || "text";
        input.value = o.value || "";
        if (o.maxLength) input.maxLength = o.maxLength;
        lab.appendChild(input);
        box.appendChild(lab);
      }
      var acts = el("div", "acts"), cancel = null, ok;
      if (o.cancel !== false) { cancel = el("button", "btn sec", o.cancel || TPUI.t("Cancel")); cancel.type = "button"; acts.appendChild(cancel); }
      ok = el("button", "btn" + (o.danger ? " danger" : ""), o.ok || TPUI.t("OK"));
      ok.type = "button";
      acts.appendChild(ok);
      box.appendChild(acts);
      veil.appendChild(box);
      var before = document.activeElement;
      function close(v) {
        document.removeEventListener("keydown", key, true);
        veil.remove();
        if (before && before.focus) try { before.focus(); } catch (e) { }
        resolve(v);
      }
      function key(e) {
        if (e.key === "Escape") { e.preventDefault(); close(o.input ? null : false); }
        else if (e.key === "Enter" && input && document.activeElement === input) { e.preventDefault(); close(input.value); }
        else if (e.key === "Tab") {
          var f = box.querySelectorAll("button, input");
          if (!f.length) return;
          var first = f[0], last = f[f.length - 1];
          if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
          else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        }
      }
      ok.onclick = function () { close(o.input ? input.value : true); };
      if (cancel) cancel.onclick = function () { close(o.input ? null : false); };
      veil.addEventListener("mousedown", function (e) { if (e.target === veil && cancel) close(o.input ? null : false); });
      document.addEventListener("keydown", key, true);
      document.body.appendChild(veil);
      (input || cancel || ok).focus();
    });
  }
  TPUI.confirm = function (o) { return dialog(typeof o === "string" ? { text: o } : o); };
  TPUI.alert = function (o) { var x = typeof o === "string" ? { text: o } : o; x.cancel = false; return dialog(x); };
  TPUI.prompt = function (o) { var x = typeof o === "string" ? { label: o } : o; x.input = true; return dialog(x); };

  // ---- toasts ------------------------------------------------------------------
  TPUI.toast = function (text, kind, ms) {
    var box = document.querySelector(".tp-toasts");
    if (!box) { box = el("div", "tp-toasts"); box.setAttribute("aria-live", "polite"); document.body.appendChild(box); }
    var t = el("div", "tp-toast" + (kind ? " " + kind : ""), text);
    box.appendChild(t);
    setTimeout(function () { t.remove(); }, ms || 4000);
    return t;
  };

  // ---- banner in a given container -----------------------------------------------
  TPUI.banner = function (container, kind, text, closable) {
    container.textContent = "";
    if (!text) { container.classList.add("hidden"); return; }
    container.className = "banner " + (kind || "info");
    container.appendChild(el("span", "grow", text));
    if (closable) {
      var x = el("button", "x", "×");
      x.type = "button";
      x.setAttribute("aria-label", TPUI.t("Close"));
      x.onclick = function () { container.classList.add("hidden"); };
      container.appendChild(x);
    }
  };

  // ---- busy overlay with steps and progress ------------------------------------------
  var busyVeil = null;
  TPUI.busy = {
    show: function (title, steps) {
      TPUI.busy.hide();
      busyVeil = el("div", "tp-veil");
      var box = el("div", "tp-dialog tp-busy");
      box.setAttribute("role", "status");
      var h = el("h3", "", title || "");
      h.insertBefore(el("span", "spin"), h.firstChild);
      h.style.display = "flex"; h.style.gap = "8px"; h.style.alignItems = "center";
      box.appendChild(h);
      var list = el("div", "steps");
      (steps || []).forEach(function (s) { var d = el("div"); d.appendChild(el("span", "dot")); d.appendChild(el("span", "", s)); list.appendChild(d); });
      box.appendChild(list);
      var bar = el("div", "bar"); bar.appendChild(el("i"));
      box.appendChild(bar);
      busyVeil.appendChild(box);
      document.body.appendChild(busyVeil);
    },
    step: function (i) {
      if (!busyVeil) return;
      busyVeil.querySelectorAll(".steps > div").forEach(function (d, k) { d.className = k < i ? "done" : k === i ? "on" : ""; });
    },
    progress: function (p) { if (busyVeil) busyVeil.querySelector(".bar > i").style.width = Math.max(0, Math.min(100, p)) + "%"; },
    hide: function () { if (busyVeil) { busyVeil.remove(); busyVeil = null; } }
  };

  // ---- paged list: 25, 50 or 100 per page ---------------------------------------------
  TPUI.pager = function (o) {
    var state = { page: 0, size: o.size || 25, items: o.items || [] };
    var pagerBox = o.pager;
    function draw() {
      var pages = Math.max(1, Math.ceil(state.items.length / state.size));
      if (state.page >= pages) state.page = pages - 1;
      o.list.textContent = "";
      var slice = state.items.slice(state.page * state.size, (state.page + 1) * state.size);
      if (!slice.length && o.empty) o.list.appendChild(el("div", "empty", o.empty));
      slice.forEach(function (it, i) { o.list.appendChild(o.render(it, state.page * state.size + i)); });
      pagerBox.textContent = "";
      pagerBox.classList.add("pager");
      var prev = el("button", "btn ghost sm", "‹"), next = el("button", "btn ghost sm", "›");
      prev.type = next.type = "button";
      prev.setAttribute("aria-label", TPUI.t("Previous page"));
      next.setAttribute("aria-label", TPUI.t("Next page"));
      prev.disabled = state.page === 0;
      next.disabled = state.page >= pages - 1;
      prev.onclick = function () { state.page--; draw(); };
      next.onclick = function () { state.page++; draw(); };
      var sel = el("select");
      [25, 50, 100].forEach(function (n) { var op = el("option", "", String(n)); op.value = n; op.selected = n === state.size; sel.appendChild(op); });
      sel.setAttribute("aria-label", TPUI.t("Per page"));
      sel.onchange = function () { state.size = +sel.value; state.page = 0; draw(); };
      pagerBox.appendChild(prev);
      pagerBox.appendChild(el("span", "", fmt(TPUI.t("Page {0} of {1}"), state.page + 1, pages)));
      pagerBox.appendChild(next);
      pagerBox.appendChild(el("span", "grow"));
      pagerBox.appendChild(sel);
    }
    draw();
    return { set: function (items) { state.items = items || []; state.page = 0; draw(); }, redraw: draw };
  };

  // ---- menus: .menu > button + .pop ------------------------------------------------
  document.addEventListener("click", function (e) {
    var trigger = e.target.closest && e.target.closest(".menu > button");
    document.querySelectorAll(".menu.open").forEach(function (m) { if (!trigger || m !== trigger.parentNode) m.classList.remove("open"); });
    if (trigger) trigger.parentNode.classList.toggle("open");
    else if (e.target.closest && e.target.closest(".menu > .pop button")) e.target.closest(".menu").classList.remove("open");
  });
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape") document.querySelectorAll(".menu.open").forEach(function (m) { m.classList.remove("open"); });
  });

  // ---- segmented control and side navigation: one "on" at a time ---------------------------
  TPUI.choice = function (container, onChange) {
    container.addEventListener("click", function (e) {
      var b = e.target.closest("button");
      if (!b || !container.contains(b)) return;
      container.querySelectorAll("button").forEach(function (x) { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", x === b); });
      if (onChange) onChange(b.getAttribute("data-value") || b.textContent);
    });
  };

  window.TPUI = TPUI;
})();
/*tpui:end*/
