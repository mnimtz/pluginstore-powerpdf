/*tpui:begin*/
/* Tungsten Plug-in UI (tpui) 1.0.1 - line icons in the Brand Book style (p. 57/58): one base
   colour (currentColor) plus an accent part in the accent blue (class "ac"). 24 x 24 grid,
   round line ends. Drawn for this kit, MIT License, see LICENSE.
   Use: TPUI.icon("search") or <svg class="i" data-icon="search"></svg> (filled by TPUI.icons()). */
(function () {
  "use strict";
  // each icon: [base paths, accent paths]; a path is SVG path data
  var I = {
    search: [["M10.5 3.5a7 7 0 1 0 0 14a7 7 0 1 0 0-14z"], ["M15.5 15.5L21 21"]],
    settings: [["M12 2.8l1.6 2.3 2.8-.4.9 2.7 2.6 1.1-.4 2.8 2.3 1.6-2.3 1.6.4 2.8-2.6 1.1-.9 2.7-2.8-.4L12 21.2l-1.6-2.3-2.8.4-.9-2.7-2.6-1.1.4-2.8L2.2 12l2.3-1.6-.4-2.8 2.6-1.1.9-2.7 2.8.4z"], ["M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6z"]],
    close: [["M6 6l12 12M18 6L6 18"], []],
    check: [["M4.5 12.5l5 5L19.5 7"], []],
    plus: [["M12 5v14M5 12h14"], []],
    minus: [["M5 12h14"], []],
    info: [["M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z"], ["M12 11v5M12 8h.01"]],
    warning: [["M12 3.5L2.5 20h19z"], ["M12 10v4.5M12 17.5h.01"]],
    error: [["M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z"], ["M9 9l6 6M15 9l-6 6"]],
    ok: [["M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z"], ["M8 12.5l2.8 2.8L16.5 9.5"]],
    trash: [["M4 7h16M9 7V4.5h6V7M6.5 7l1 13h9l1-13"], ["M10 11v5.5M14 11v5.5"]],
    edit: [["M4 20h4L19 9l-4-4L4 16z"], ["M13.5 6.5l4 4"]],
    download: [["M4 17v3h16v-3"], ["M12 4v11M7.5 10.5L12 15l4.5-4.5"]],
    upload: [["M4 17v3h16v-3"], ["M12 15V4M7.5 8.5L12 4l4.5 4.5"]],
    refresh: [["M19.5 12a7.5 7.5 0 1 1-2.2-5.3"], ["M19.5 4v4h-4"]],
    send: [["M3 11.5L21 3l-6.5 18-3-7.5z"], ["M11.5 13.5L21 3"]],
    file: [["M6 2.5h8l4.5 4.5v14.5H6z"], ["M14 2.5V7h4.5"]],
    folder: [["M3 6.5h6l2 2.5h10v10.5H3z"], ["M3 11h18"]],
    compare: [["M4 3.5h6.5v17H4zM13.5 3.5H20v17h-6.5z"], ["M6.5 8h1.5M6.5 12h1.5M16 8h1.5M16 16h1.5"]],
    lock: [["M5.5 10.5h13v10h-13z"], ["M8.5 10.5V7.5a3.5 3.5 0 0 1 7 0v3M12 14.5v2.5"]],
    key: [["M8 10a4.5 4.5 0 1 0 0 9a4.5 4.5 0 1 0 0-9z"], ["M11 11.5L20 3M16.5 6.5L19 9M14.5 8.5l2 2"]],
    link: [["M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"], ["M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"]],
    external: [["M18 13.5V20H4V6h6.5"], ["M14 4h6v6M20 4l-9 9"]],
    chevronDown: [["M6 9l6 6 6-6"], []],
    chevronRight: [["M9 6l6 6-6 6"], []],
    chevronLeft: [["M15 6l-6 6 6 6"], []],
    menu: [["M4 6.5h16M4 12h16M4 17.5h16"], []],
    filter: [["M3.5 5h17l-6.5 8v6.5l-4-2V13z"], []],
    eye: [["M2.5 12s3.5-6.5 9.5-6.5S21.5 12 21.5 12s-3.5 6.5-9.5 6.5S2.5 12 2.5 12z"], ["M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6z"]],
    sparkle: [["M11 3.5l1.8 5.2L18 10.5l-5.2 1.8L11 17.5l-1.8-5.2L4 10.5l5.2-1.8z"], ["M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8z"]],
    shield: [["M12 2.5l8 3v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10v-6z"], ["M8.5 12l2.5 2.5 4.5-5"]],
    user: [["M12 3.5a4 4 0 1 0 0 8a4 4 0 1 0 0-8z"], ["M4.5 20.5c1.2-3.6 4-5.5 7.5-5.5s6.3 1.9 7.5 5.5"]],
    globe: [["M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z"], ["M3 12h18M12 3c2.5 2.6 3.7 5.6 3.7 9s-1.2 6.4-3.7 9c-2.5-2.6-3.7-5.6-3.7-9S9.5 5.6 12 3z"]],
    help: [["M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z"], ["M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.7.3-1 .9-1 1.7v.5M12 17h.01"]],
    signature: [["M3 20.5h18"], ["M4 16c2.5-7 4.5-9 5.5-7.5s-1.5 6 .5 6.5 3-4 4.5-3.5-.5 3.5 1 3.5 2-1.5 4.5-2"]],
    stamp: [["M4 20.5h16M5.5 16.5h13v4h-13z"], ["M9.5 16.5c0-3-2-4.5-2-7a4.5 4.5 0 0 1 9 0c0 2.5-2 4-2 7"]],
    bookmark: [["M6.5 3h11v18l-5.5-4-5.5 4z"], ["M9.5 8h5"]],
    mail: [["M3 5.5h18v13H3z"], ["M3.5 6l8.5 7 8.5-7"]],
    printer: [["M6.5 9V3.5h11V9M6.5 17H4V9.5h16V17h-2.5"], ["M6.5 14h11v6.5h-11z"]],
    barcode: [["M4 5v14M7 5v14M11 5v14M14 5v14M18 5v14M20 5v14"], ["M2.5 3.5h3M18.5 3.5h3M2.5 20.5h3M18.5 20.5h3"]],
    invoice: [["M6 2.5h12v19l-2-1.5-2 1.5-2-1.5-2 1.5-2-1.5-2 1.5z"], ["M9 8h6M9 11.5h6M9 15h3.5"]],
    store: [["M4 8.5h16l-1 12H5z"], ["M8.5 8.5V7a3.5 3.5 0 0 1 7 0v1.5"]]
  };
  var NS = "http://www.w3.org/2000/svg";
  function make(name, cls) {
    var d = I[name] || I.help;
    var s = document.createElementNS(NS, "svg");
    s.setAttribute("viewBox", "0 0 24 24");
    s.setAttribute("class", "i" + (cls ? " " + cls : ""));
    s.setAttribute("aria-hidden", "true");
    s.setAttribute("focusable", "false");
    d[0].forEach(function (p) { var e = document.createElementNS(NS, "path"); e.setAttribute("d", p); s.appendChild(e); });
    d[1].forEach(function (p) { var e = document.createElementNS(NS, "path"); e.setAttribute("d", p); e.setAttribute("class", "ac"); s.appendChild(e); });
    return s;
  }
  var T = window.TPUI || (window.TPUI = {});
  T.icon = make;
  T.iconNames = Object.keys(I);
  // fills every <svg data-icon="name"> placeholder below root
  T.icons = function (root) {
    (root || document).querySelectorAll("svg[data-icon]").forEach(function (ph) {
      var s = make(ph.getAttribute("data-icon"), (ph.getAttribute("class") || "").replace(/\bi\b/, "").trim());
      ph.replaceWith(s);
    });
  };
  if (document.readyState !== "loading") T.icons(); else document.addEventListener("DOMContentLoaded", function () { T.icons(); });
})();
/*tpui:end*/
