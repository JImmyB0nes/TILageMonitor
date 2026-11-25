"""Nachbildung des Konnektor-Fensters (ConnectorWindow.xaml) mit Demodaten, gerendert per Chrome.

Farben 1:1 aus ThemeService.cs, Abstände/Radien aus ConnectorWindow.xaml. Alle Daten sind erfunden.

Aufruf:  pip install playwright  &&  python docs/tools/render-connector-previews.py docs/assets
Optional CHROME_PATH setzen, sonst wird das von Playwright installierte Chromium genutzt.
"""
import os
import sys
from playwright.sync_api import sync_playwright

THEMES = {
    "dark": dict(win="#1A1D21", card="#25282D", text="#F3F4F6", muted="#9CA3AF", border="#3A3F47", chip="#32363C",
                 accent="#3B82F6", accentFg="#FFFFFF", btn="#2A2E34", btnFg="#E5E7EB", btnBorder="#4B5563",
                 ok="#34D399", partial="#FBBF24", outage="#F87171", maint="#38BDF8",
                 okS="#14261C", okB="#059669", partS="#2A2114", partB="#D97706", outS="#2A1616", outB="#DC2626",
                 maintS="#0F2433", maintB="#0284C7", empty="#4B5563", iconBg="#1E3A5F", iconFg="#93C5FD",
                 thumb="#4B5563", title="#202020", titleFg="#FFFFFF", shadow="0 3px 24px rgba(0,0,0,.25)"),
    "light": dict(win="#F3F6FB", card="#FFFFFF", text="#0F172A", muted="#64748B", border="#E2E8F0", chip="#F1F5F9",
                  accent="#2563EB", accentFg="#FFFFFF", btn="#FFFFFF", btnFg="#334155", btnBorder="#CBD5E1",
                  ok="#228B22", partial="#FF8C00", outage="#B22222", maint="#0284C8",
                  okS="#ECFDF5", okB="#86EFAC", partS="#FFF7ED", partB="#FDBA74", outS="#FEF2F2", outB="#FECACA",
                  maintS="#F0F9FF", maintB="#7DD3FC", empty="#CBD5E1", iconBg="#DBEAFE", iconFg="#1D4ED8",
                  thumb="#CBD5E1", title="#F3F3F3", titleFg="#1F1F1F", shadow="0 3px 24px rgba(0,0,0,.07)"),
}

# Schlichte Strich-Symbole anstelle der Segoe Fluent Icons
ICONS = {
    "plug": '<path d="M9 2v6M15 2v6M6 8h12v4a6 6 0 0 1-12 0zM12 18v4"/>',
    "check": '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    "warn": '<path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18h.01"/>',
    "refresh": '<path d="M20 12a8 8 0 1 1-2.3-5.6M20 4v5h-5"/>',
    "copy": '<rect x="8" y="8" width="12" height="12" rx="2"/><path d="M4 16V6a2 2 0 0 1 2-2h10"/>',
    "home": '<path d="M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/>',
    "card": '<rect x="3" y="5" width="18" height="14" rx="2.5"/><path d="M7 10h4M7 14h7"/><rect x="14" y="9" width="4" height="3" rx=".6"/>',
    "terminal": '<rect x="5" y="2.5" width="14" height="19" rx="2.5"/><path d="M8 6h8v4H8zM9 14h.01M12 14h.01M15 14h.01M9 17h.01M12 17h.01M15 17h.01"/>',
    "cert": '<circle cx="12" cy="9" r="5"/><path d="M9 13.5L8 21l4-2 4 2-1-7.5"/>',
    "events": '<path d="M3 12h4l3-7 4 14 3-7h4"/>',
    "history": '<path d="M3 12a9 9 0 1 0 3-6.7M3 4v4h4M12 7v5l3 2"/>',
    "diag": '<path d="M4 20V10M10 20V4M16 20v-7M22 20H2"/>',
    "gear": '<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>',
    "clock": '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    "server": '<rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/><path d="M7 7.5h.01M7 16.5h.01"/>',
    "key": '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M17 6l3 3M15 8l2 2"/>',
    "pen": '<path d="M4 20l4-1 11-11-3-3L5 16z"/><path d="M14 6l3 3"/>',
    "calendar": '<rect x="3" y="5" width="18" height="16" rx="2.5"/><path d="M3 10h18M8 3v4M16 3v4"/>',
    "lock": '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
    "shield": '<path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z"/>',
    "id": '<rect x="3" y="5" width="18" height="14" rx="2.5"/><circle cx="9" cy="11" r="2.2"/><path d="M5.8 16c.6-1.6 1.8-2.4 3.2-2.4s2.6.8 3.2 2.4M15 10h3M15 13h3"/>',
    "sliders": '<path d="M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0"/><circle cx="16" cy="6" r="2"/><circle cx="10" cy="12" r="2"/><circle cx="18" cy="18" r="2"/>',
    "chevron": '<path d="M6 9l6 6 6-6"/>',
    "folder": '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    "save": '<path d="M5 3h11l3 3v13a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"/><path d="M8 3v5h7M7 21v-7h10v7"/>',
    "test": '<path d="M9 3h6M10 3v6L5 19a1.5 1.5 0 0 0 1.3 2h11.4a1.5 1.5 0 0 0 1.3-2L14 9V3"/>',
    "info": '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
    "trash": '<path d="M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13"/>',
}


def icon(name, size=16, color="currentColor", stroke=1.8):
    return (f'<svg width="{size}" height="{size}" viewBox="0 0 24 24" fill="none" stroke="{color}" stroke-width="{stroke}" '
            f'stroke-linecap="round" stroke-linejoin="round">{ICONS[name]}</svg>')


def css(t):
    return f"""
*{{box-sizing:border-box;margin:0;padding:0}}
body{{font-family:'Inter',sans-serif;font-size:13px;color:{t['text']};background:transparent;-webkit-font-smoothing:antialiased;font-feature-settings:'cv11','ss01'}}
.frame{{width:1180px;border-radius:9px;overflow:hidden;border:1px solid {'#3a3a3a' if t is THEMES['dark'] else '#c9c9c9'};background:{t['win']};
  box-shadow:0 22px 60px rgba(0,0,0,.28)}}
.titlebar{{height:32px;background:{t['title']};display:flex;align-items:center;color:{t['titleFg']};font-size:12px;padding-left:12px}}
.titlebar .app{{width:16px;height:16px;border-radius:4px;background:{t['iconBg']};margin-right:10px;display:flex;align-items:center;justify-content:center;color:{t['iconFg']}}}
.titlebar .ctl{{margin-left:auto;display:flex}}
.titlebar .ctl span{{width:46px;height:32px;display:flex;align-items:center;justify-content:center;opacity:.85;font-size:13px}}
.root{{padding:20px 24px 14px;height:828px;display:flex;flex-direction:column}}
.muted{{color:{t['muted']}}}
.panel{{background:{t['card']};border:1px solid {t['border']};border-radius:16px;padding:20px 22px;margin-bottom:16px;box-shadow:{t['shadow']}}}
.hero{{border-radius:20px;padding:20px 24px;margin-bottom:18px;border:1px solid {t['okB']};background:{t['okS']};box-shadow:{t['shadow']}}}
.hero.warn{{border-color:{t['partB']};background:{t['partS']}}}
.row{{display:flex;align-items:center}}
.statusicon{{width:56px;height:56px;border-radius:18px;background:{t['ok']};display:flex;align-items:center;justify-content:center;margin-right:18px;color:#fff}}
.warn .statusicon{{background:{t['partial']}}}
.eyebrow{{font-size:11px;font-weight:600;color:{t['muted']};display:flex;align-items:center;gap:6px}}
.htitle{{font-size:21px;font-weight:600;margin-top:3px}}
.btn{{display:inline-flex;align-items:center;gap:8px;border:1px solid {t['btnBorder']};background:{t['btn']};color:{t['btnFg']};border-radius:12px;padding:9px 14px;font-size:13px;margin-left:8px;white-space:nowrap}}
.btn.accent{{background:{t['accent']};border-color:{t['accent']};color:{t['accentFg']};padding:9px 16px}}
.btn.sm{{font-size:12px;padding:8px 14px;margin:0 8px 6px 0;border-radius:12px}}
.kpis{{display:grid;grid-template-columns:repeat(6,1fr);gap:10px;margin-top:18px}}
.kpi{{background:{t['card']};border:1px solid {t['border']};border-radius:12px;padding:11px 14px}}
.kpi .l{{font-size:11px;font-weight:600;color:{t['muted']}}}
.kpi .v{{font-size:18px;font-weight:600;margin-top:4px;display:flex;align-items:center}}
.dot{{width:10px;height:10px;border-radius:5px;margin-right:8px;display:inline-block}}
.main{{flex:1;display:flex;min-height:0}}
.nav{{width:232px;margin-right:20px;padding:14px 10px;background:{t['card']};border:1px solid {t['border']};border-radius:16px;box-shadow:{t['shadow']}}}
.nav .cap{{font-size:11px;font-weight:600;color:{t['muted']};margin:0 0 8px 12px}}
.navitem{{position:relative;display:flex;align-items:center;padding:10px 12px;border-radius:8px;margin:2px 0;font-size:13px}}
.navitem svg{{margin-right:12px;color:{t['muted']}}}
.navitem.sel{{background:{t['chip']};font-weight:600}}
.navitem.sel svg{{color:{t['iconFg']}}}
.navitem.sel:before{{content:'';position:absolute;left:0;top:50%;margin-top:-8px;width:3px;height:16px;border-radius:2px;background:{t['accent']}}}
.badge{{margin-left:auto;min-width:22px;height:18px;border-radius:9px;background:{t['iconBg']};color:{t['iconFg']};font-size:11px;font-weight:600;display:flex;align-items:center;justify-content:center;padding:0 6px}}
.content{{flex:1;min-width:0;overflow:hidden}}
.ptitle{{font-size:24px;font-weight:600}}
.phint{{font-size:13px;color:{t['muted']};margin:4px 0 18px}}
.sechead{{display:flex;align-items:flex-start;margin-bottom:16px}}
.tile{{width:36px;height:36px;border-radius:10px;background:{t['iconBg']};color:{t['iconFg']};display:flex;align-items:center;justify-content:center;margin-right:14px;flex:none}}
.stitle{{font-size:15px;font-weight:600}}
.shint{{font-size:12px;color:{t['muted']};margin-top:2px}}
.props{{display:grid;grid-template-columns:repeat(auto-fill,minmax(232px,1fr));gap:10px}}
.prop{{background:{t['chip']};border-radius:10px;padding:10px 13px}}
.prop .l{{font-size:11px;font-weight:600;color:{t['muted']}}}
.prop .v{{font-size:13px;margin-top:3px}}
.pill{{display:inline-flex;align-items:center;border-radius:999px;border:1px solid {t['border']};background:{t['chip']};color:{t['muted']};font-weight:600;font-size:12px;padding:4px 11px 5px}}
.pill.sm{{font-size:10.5px;padding:1px 7px 2px}}
.pill.ok{{background:{t['okS']};border-color:{t['okB']};color:{t['ok']}}}
.pill.part{{background:{t['partS']};border-color:{t['partB']};color:{t['partial']}}}
.pill.out{{background:{t['outS']};border-color:{t['outB']};color:{t['outage']}}}
.pill.maint{{background:{t['maintS']};border-color:{t['maintB']};color:{t['maint']}}}
.avatar{{position:relative;flex:none;display:flex;align-items:center;justify-content:center;border:1px solid {t['border']};background:{t['chip']};color:{t['muted']}}}
.avatar.maint{{background:{t['maintS']};border-color:{t['maintB']};color:{t['maint']}}}
.avatar .sd{{position:absolute;right:-3px;bottom:-3px;border-radius:50%;border:2px solid {t['card']}}}
.list{{width:300px;margin-right:16px;padding:14px 10px}}
.li{{display:flex;align-items:center;padding:11px 12px;border-radius:12px;margin-bottom:6px;border:1px solid transparent}}
.li.sel{{background:{t['chip']};border-color:{t['accent']}}}
.bar{{height:4px;border-radius:2px;background:{t['chip']};overflow:hidden;margin-top:8px}}
.bar i{{display:block;height:100%;border-radius:2px}}
.cap2{{font-size:12px;font-weight:600;color:{t['muted']};margin:22px 0 10px}}
.group{{border:1px solid {t['border']};border-radius:12px;background:{t['card']}}}
.group .r{{display:flex;align-items:center;padding:12px 16px}}
.group .r + .r{{border-top:1px solid {t['border']}}}
.alert{{display:flex;border-radius:12px;padding:12px 14px;margin-bottom:8px;border:1px solid}}
.cert{{background:{t['card']};border:1px solid {t['border']};border-radius:16px;padding:16px 18px;box-shadow:{t['shadow']}}}
.input{{height:36px;border-radius:8px;border:1px solid {t['border']};background:{t['chip']};padding:0 10px;display:flex;align-items:center;font-size:13px}}
.input.focus{{border-color:{t['accent']};border-bottom-width:2px;background:{t['card']}}}
.flabel{{font-size:12px;font-weight:600;margin-bottom:6px}}
.setrow{{background:{t['chip']};border-radius:10px;padding:12px 16px;margin-bottom:6px;display:flex;align-items:center}}
.switch{{width:44px;height:22px;border-radius:11px;border:1.5px solid {t['muted']};position:relative;flex:none;margin-left:16px}}
.switch:after{{content:'';position:absolute;left:4px;top:50%;margin-top:-6px;width:12px;height:12px;border-radius:6px;background:{t['muted']}}}
.switch.on{{background:{t['accent']};border-color:{t['accent']}}}
.switch.on:after{{left:auto;right:4px;background:#fff}}
.footer{{display:flex;align-items:center;margin-top:14px;font-size:12px;color:{t['muted']}}}
.footer .btn{{margin-left:auto;min-width:110px;justify-content:center;padding:8px 16px}}
.scroll{{position:absolute;right:0;top:0;width:6px;border-radius:3px;background:{t['thumb']}}}
"""


def window(t, page, body_html, hero_html):
    nav = [("home", "Übersicht", None), ("card", "Karten", "4"), ("terminal", "Terminals", "2"), ("cert", "Zertifikate", None),
           ("events", "Ereignisse", None), ("history", "Verlauf", None), ("diag", "Diagnose", None), ("gear", "Verbindung", None)]
    items = "".join(
        f'<div class="navitem{" sel" if label == page else ""}">{icon(ic, 18)}<span>{label}</span>'
        + (f'<span class="badge">{b}</span>' if b else "") + "</div>" for ic, label, b in nav)
    return f"""<!doctype html><html><head><meta charset="utf-8"><style>{css(t)}</style></head><body>
<div class="frame">
 <div class="titlebar"><div class="app">{icon('plug', 11)}</div>Konnektor<div class="ctl"><span>&#x2013;</span><span>&#x25A1;</span><span>&#x2715;</span></div></div>
 <div class="root">
  {hero_html}
  <div class="main">
   <div class="nav"><div class="cap">Bereiche</div>{items}</div>
   <div class="content">{body_html}</div>
  </div>
  <div class="footer">{icon('clock', 13)}<span style="margin-left:8px">Letzter Abruf: 25.11.2026 07:42:18 · Demodaten</span><span class="btn">Schließen</span></div>
 </div>
</div></body></html>"""


def hero(t, warn=False):
    kpis = [("Erreichbar", "ja", t['ok']), ("VPN TI", "ja", t['ok']), ("VPN SIS", "ja", t['ok']), ("Live-Ereignisse", "ja", t['ok']),
            ("Karten", "4", None), ("Terminals", "2", None)]
    k = "".join(f'<div class="kpi"><div class="l">{l}</div><div class="v">' + (f'<span class="dot" style="background:{c}"></span>' if c else "")
                + f'{v}</div></div>' for l, v, c in kpis)
    title = "RISE Konnektor: Hinweise beachten" if warn else "RISE Konnektor: alles in Ordnung"
    glyph = icon("warn" if warn else "check", 26, "#fff", 2.2)
    return f"""<div class="hero{' warn' if warn else ''}">
 <div class="row">
  <div class="statusicon">{glyph}</div>
  <div style="flex:1"><div class="eyebrow">{icon('plug', 12)}Konnektor</div><div class="htitle">{title}</div>
   <div class="muted" style="font-size:13px;margin-top:3px">Stand 07:42:18 Uhr</div></div>
  <span class="btn">{icon('copy', 15)}Bericht</span><span class="btn accent">{icon('refresh', 15, '#fff')}Aktualisieren</span>
 </div>
 <div class="kpis">{k}</div>
</div>"""


def avatar(name, size, tone=None, dot=None):
    d = f'<span class="sd" style="width:{size*0.3:.0f}px;height:{size*0.3:.0f}px;background:{dot}"></span>' if dot else ""
    return (f'<div class="avatar{" maint" if tone else ""}" style="width:{size}px;height:{size}px;border-radius:{size*0.3:.0f}px">'
            f'{icon(name, int(size*0.45))}{d}</div>')


def sechead(ic, title, hint=""):
    return (f'<div class="sechead"><div class="tile">{icon(ic, 17)}</div><div><div class="stitle">{title}</div>'
            + (f'<div class="shint">{hint}</div>' if hint else "") + "</div></div>")


def overview(t):
    props = [("Hersteller", "RISE GmbH"), ("Produkt", "RISE Konnektor · KON-5"), ("Produkttyp-Version (PTV)", "Konnektor · 5.50.2"),
             ("Firmware", "5.1.12"), ("Hardware", "2.0.0"), ("TLS verpflichtend", "ja"),
             ("VPN TI", "Online · 25.11.2026 05:12"), ("VPN SIS (Internet)", "Online · 25.11.2026 05:12"),
             ("Fehlerzustände", "keine aktiven"), ("Uhrzeit-Abweichung", "synchron (unter 1 s)"),
             ("TLS-Zertifikat Konnektor", "CN=konnektor.praxis.local · gültig bis 14.06.2029")]
    p = "".join(f'<div class="prop"><div class="l">{l}</div><div class="v" style="font-weight:600">{v}</div></div>' for l, v in props)
    return f"""<div class="ptitle">Übersicht</div><div class="phint">Zustand, gesteckte Karte und Eckdaten des Konnektors auf einen Blick.</div>
<div class="panel"><div class="row">
  <div style="position:relative;width:48px;height:48px;border-radius:14px;background:{t['chip']};display:flex;align-items:center;justify-content:center;margin-right:16px;color:{t['muted']}">{icon('card', 22)}
   <span style="position:absolute;right:5px;bottom:4px;width:9px;height:9px;border-radius:5px;background:{t['ok']}"></span></div>
  <div style="flex:1"><div class="eyebrow">Karte</div><div style="font-size:17px;font-weight:600;color:{t['ok']};margin-top:2px">rein</div>
   <div class="muted" style="font-size:12px;margin-top:2px">SMC-B · Empfang</div></div>
  <span class="pill" style="font-weight:500">{icon('clock', 12)}&nbsp; vor 12 s · aus Ereignis-Abo</span>
</div></div>
<div class="panel">{sechead('server', 'Konnektor', 'Aus connector.sds und GetResourceInformation')}<div class="props">{p}</div></div>"""


def cards_page(t):
    rows = [
        ("Praxis Dr. Erika Mustermann", "SMC-B", "Empfang · Slot 1", "verifiziert", "ok", True, 0.72, t['ok']),
        ("Dr. med. Erika Mustermann", "HBA", "Sprechzimmer 1 · Slot 1", "nicht verifiziert", "part", True, 0.55, t['ok']),
        ("Dr. med. Max Beispiel", "HBA", "Sprechzimmer 1 · Slot 2", "verifiziert", "ok", True, 0.08, t['partial']),
        ("eGK", "eGK", "Empfang · Slot 2", None, None, False, None, None),
    ]
    li = ""
    for i, (n, kind, meta, pin, pc, tone, frac, col) in enumerate(rows):
        dot = {"ok": t['ok'], "part": t['partial']}.get(pc)
        li += (f'<div class="li{" sel" if i == 1 else ""}">{avatar("card", 40, tone, dot)}<div style="margin-left:12px;flex:1;min-width:0">'
               f'<div style="font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{n}</div>'
               f'<div class="row" style="margin-top:4px"><span class="pill sm{" maint" if tone else ""}" style="margin-right:7px">{kind}</span>'
               f'<span class="muted" style="font-size:11px">{meta}</span></div>'
               + (f'<span class="pill sm {pc}" style="margin-top:6px">{pin}</span>' if pin else "")
               + (f'<div class="bar"><i style="width:{frac*100:.0f}%;background:{col}"></i></div>' if frac else "") + "</div></div>")
    props = [("ICCSN", "80276001011699901234"), ("COS", "4.5.0"), ("Gesteckt seit", "25.11.2026 07:31"),
             ("Telematik-ID", "1-2-ARZT-4711-01"), ("Rolle", "Ärztin/Arzt"), ("Schlüssel", "C.AUT · ECC"),
             ("Aussteller", "Demo-HBA-CA2")]
    certs = [("C.AUT", "bis 17.04.2029 · 873 Tage", None), ("C.ENC", "bis 17.04.2029 · 873 Tage", None),
             ("C.QES", "bis 17.04.2029 · 873 Tage", None)]
    pp = "".join(f'<div class="prop"><div class="l">{l}</div><div class="v">{v}</div></div>' for l, v in props)
    cp = "".join(f'<div class="prop"><div class="l">{l}</div><div class="v">{v}</div></div>' for l, v, _ in certs)
    pinrow = lambda name, pill, pc, btns: (
        f'<div class="r"><div class="row" style="width:96px;font-weight:600">{icon("key", 14, t["muted"])}<span style="margin-left:8px">{name}</span></div>'
        f'<span class="pill {pc}">{pill}</span><div style="margin-left:auto;display:flex">'
        + "".join(f'<span class="btn sm" style="margin:0 0 0 8px;padding:6px 12px">{b}</span>' for b in btns) + "</div></div>")
    acts = [("info", "Zertifikat online prüfen (OCSP)"), ("cert", "Alle Zertifikate lesen"), ("refresh", "PIN-Status neu lesen"), ("pen", "QES-Probesignatur")]
    a = "".join(f'<span class="btn sm">{icon(i, 14)}{l}</span>' for i, l in acts)
    return f"""<div class="ptitle">Karten</div><div class="phint">SMC-B, HBA und eGK – PIN-Zustand, Zertifikate und Aktionen.</div>
<div class="row" style="align-items:stretch;height:560px">
 <div class="panel list" style="margin-bottom:0"><div class="row" style="margin:0 8px 10px 10px"><span class="eyebrow">Gesteckte Karten</span><span class="muted" style="margin-left:auto;font-size:12px">4 gesteckt</span></div>{li}</div>
 <div class="panel" style="flex:1;margin-bottom:0;padding:22px 24px;overflow:hidden">
  <div class="row">{avatar('card', 56, True)}<div style="margin:0 12px 0 16px;flex:1">
    <span class="pill sm maint">HBA</span><div style="font-size:20px;font-weight:600;margin-top:4px">Dr. med. Erika Mustermann</div>
    <div class="muted" style="font-size:12px;margin-top:3px">Sprechzimmer 1 · Slot 1</div></div><span class="pill part">nicht verifiziert</span></div>
  <div class="cap2">Eigenschaften</div><div class="props">{pp}</div>
  <div class="cap2">Zertifikate</div><div class="props">{cp}</div>
  <div class="cap2">PIN-Zustand</div>
  <div class="group">{pinrow('PIN.CH', 'nicht verifiziert · 3 Versuche übrig', 'part', ['PIN verifizieren', 'PIN ändern'])}{pinrow('PIN.QES', 'nicht verifiziert · 3 Versuche übrig', 'part', ['PIN verifizieren', 'PIN ändern'])}</div>
  <div class="cap2">Aktionen</div><div>{a}</div>
 </div>
</div>"""


def certs_page(t):
    items = [("C.AUT", "SMC-B Praxis Dr. Erika Mustermann", 412, "21.01.2028", "ok"),
             ("C.ENC", "SMC-B Praxis Dr. Erika Mustermann", 412, "21.01.2028", "ok"),
             ("C.AUT", "HBA Dr. med. Erika Mustermann", 873, "17.04.2029", "ok"),
             ("C.QES", "HBA Dr. med. Erika Mustermann", 873, "17.04.2029", "ok"),
             ("C.QES", "HBA Dr. med. Max Beispiel", 24, "19.12.2026", "partial"),
             ("Clientsystem-Zertifikat", "CN=PVS-Empfang, O=Praxis Mustermann", 61, "25.01.2027", "maint")]
    col = {"ok": (t['ok'], t['okS']), "partial": (t['partial'], t['partS']), "maint": (t['maint'], t['maintS'])}
    tiles = ""
    for kind, owner, days, until, c in items:
        fg, bg = col[c]
        frac = min(days / 365, 1)
        tiles += (f'<div class="cert"><div class="row"><div class="tile" style="background:{bg};color:{fg};margin-right:12px">{icon("cert", 17)}</div>'
                  f'<div style="min-width:0"><div style="font-size:14px;font-weight:600">{kind}</div><div class="muted" style="font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">{owner}</div></div></div>'
                  f'<div class="row" style="align-items:flex-end;margin-top:16px"><span style="font-size:28px;font-weight:600;line-height:1;color:{fg if days <= 30 else t["text"]}">{days}</span>'
                  f'<span class="muted" style="font-size:12px;margin:0 0 3px 8px">Tage übrig</span></div>'
                  f'<div class="bar" style="height:6px"><i style="width:{max(frac,0.015)*100:.1f}%;background:{fg}"></i></div>'
                  f'<div class="muted" style="font-size:12px;margin-top:8px">gültig bis {until}</div></div>')
    return f"""<div class="row" style="align-items:flex-start"><div style="flex:1"><div class="ptitle">Zertifikate</div>
<div class="phint">C.AUT, C.ENC, C.SIG und C.QES der gesteckten Karten sowie das Clientsystem-Zertifikat. Warnstufen: 90, 30 und 7 Tage vor Ablauf.</div></div>
<span class="btn" style="margin:2px 0 0 16px">{icon('calendar', 15)}Ablaufkalender (.ics)</span></div>
<div style="display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:14px">{tiles}</div>"""


def connection_page(t):
    def field(label, value, focus=False, w=None):
        return (f'<div style="{f"width:{w}px;" if w else "flex:1;"}"><div class="flabel">{label}</div>'
                f'<div class="input{" focus" if focus else ""}">{value}</div></div>')
    def setrow(title, hint, on):
        return (f'<div class="setrow"><div style="flex:1"><div style="font-size:13px">{title}</div><div class="shint">{hint}</div></div>'
                f'<span class="switch{" on" if on else ""}"></span></div>')
    return f"""<div class="ptitle">Verbindung</div><div class="phint">Werte aus der Konnektor-Administration. Das Client-Zertifikat (.p12) wird im Konnektor für dieses Clientsystem erzeugt bzw. hinterlegt.</div>
<div class="row" style="padding:8px 0;margin-bottom:12px"><span style="color:{t['iconFg']};margin-right:10px">{icon('lock', 15)}</span><span style="font-size:15px;font-weight:600">Zugang und Aufrufkontext</span><span class="muted" style="margin-left:auto;transform:rotate(180deg);display:flex">{icon('chevron', 13)}</span></div>
<div class="panel">{sechead('id', 'Zugang', 'Adresse des Konnektors und Client-Zertifikat dieses Arbeitsplatzes.')}
 <div class="row" style="align-items:flex-end;gap:18px;margin-bottom:14px">{field('Konnektor-Adresse', '192.168.10.20', True)}
  <div><div class="flabel">Übertragung</div><div class="row" style="height:36px"><span class="switch on" style="margin:0"></span><span style="margin-left:10px">TLS (https)</span></div></div></div>
 <div class="flabel">Client-Zertifikat (.p12)</div><div class="row" style="gap:8px"><div class="input" style="flex:1">C:\\TI\\Zertifikate\\pvs-empfang.p12</div><span class="btn" style="margin:0">{icon('folder', 14)}Durchsuchen…</span></div>
 <div class="row" style="background:{t['chip']};border-radius:10px;padding:10px 14px;margin-top:14px;font-size:12px;color:{t['muted']}">{icon('cert', 14)}<span style="margin-left:10px">Zertifikat: CN=PVS-Empfang, O=Praxis Mustermann · gültig bis 25.01.2027</span></div>
</div>
<div class="panel">{sechead('sliders', 'Verhalten', 'Wie oft abgefragt wird und was die App im Hintergrund tut.')}
 {setrow('Konnektor-Anbindung aktiv', 'Schaltet Abruf und Anzeige des Konnektors ein.', True)}
 {setrow('Im Hintergrund überwachen (Toasts)', 'Meldet Störungen auch bei geschlossenem Fenster.', True)}
 {setrow('Ereignis-Abos (Karte rein/raus sofort)', 'Die App lauscht auf einem lokalen TCP-Port. Schlägt das Abo fehl, wird automatisch weiter gepollt.', True)}
</div>"""


def render(name, theme, page, warn=False):
    t = THEMES[theme]
    body = {"Übersicht": overview, "Karten": cards_page, "Zertifikate": certs_page, "Verbindung": connection_page}[page](t)
    html = window(t, page, body, hero(t, warn))
    with sync_playwright() as p:
        chrome = os.environ.get("CHROME_PATH")
        b = p.chromium.launch(executable_path=chrome, args=["--no-sandbox"]) if chrome else p.chromium.launch()
        pg = b.new_page(viewport={"width": 1220, "height": 920}, device_scale_factor=1.5)
        pg.set_content(html)
        pg.wait_for_timeout(300)
        pg.locator(".frame").screenshot(path=name, omit_background=True)
        b.close()


if __name__ == "__main__":
    out = sys.argv[1]
    render(f"{out}/konnektor-uebersicht-dark.png", "dark", "Übersicht")
    render(f"{out}/konnektor-uebersicht-light.png", "light", "Übersicht")
    render(f"{out}/konnektor-karten.png", "dark", "Karten")
    render(f"{out}/konnektor-zertifikate.png", "light", "Zertifikate")
    render(f"{out}/konnektor-verbindung.png", "dark", "Verbindung")
