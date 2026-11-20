// Minimal PowerPoint (.pptx) viewer for Focus Study. Original code, MIT licence.
// Reads the OOXML package with JSZip and draws each slide as positioned HTML:
// text boxes and placeholders (with layout/master inheritance), pictures, simple shapes,
// groups, tables and backgrounds. Charts and SmartArt show as labelled boxes.
window.PptxView = (() => {
  const EMU = 9525; // EMU per CSS pixel at 96 dpi
  const NS_R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships';
  const parse = s => new DOMParser().parseFromString(s, 'application/xml');
  const kids = (el, n) => (el ? Array.from(el.children).filter(c => c.localName === n) : []);
  const kid = (el, n) => { if (!el) return null; for (const c of el.children) if (c.localName === n) return c; return null; };
  const path = (el, ...names) => { for (const n of names) { el = kid(el, n); if (!el) return null; } return el; };
  const num = (v, d) => (v == null || v === '' ? d : Number(v));
  const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const PRST = { black: '000000', white: 'FFFFFF', red: 'FF0000', green: '008000', blue: '0000FF', yellow: 'FFFF00', gray: '808080', grey: '808080', orange: 'FFA500' };

  const dirOf = p => p.substring(0, p.lastIndexOf('/') + 1);
  function resolve(base, target) {
    if (target.startsWith('/')) return target.slice(1);
    const out = [];
    for (const part of (dirOf(base) + target).split('/')) {
      if (part === '..') out.pop(); else if (part !== '.' && part !== '') out.push(part);
    }
    return out.join('/');
  }
  const relsPathOf = p => dirOf(p) + '_rels/' + p.substring(dirOf(p).length) + '.rels';

  class Pkg {
    constructor(zip) { this.zip = zip; this.cache = {}; this.urls = {}; }
    async xml(p) {
      if (!(p in this.cache)) { const f = this.zip.file(p); this.cache[p] = f ? parse(await f.async('string')) : null; }
      return this.cache[p];
    }
    async rels(p) {
      const x = await this.xml(relsPathOf(p)), m = {};
      if (!x) return m;
      for (const r of x.getElementsByTagName('Relationship')) {
        const ext = r.getAttribute('TargetMode') === 'External';
        m[r.getAttribute('Id')] = { target: ext ? r.getAttribute('Target') : resolve(p, r.getAttribute('Target')), type: r.getAttribute('Type') || '', ext };
      }
      return m;
    }
    async url(p) {
      if (this.urls[p]) return this.urls[p];
      const f = this.zip.file(p);
      if (!f) return null;
      const mime = { png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', svg: 'image/svg+xml', bmp: 'image/bmp', webp: 'image/webp' }[p.split('.').pop().toLowerCase()];
      if (!mime) return null; // EMF/WMF etc. can't be shown in a browser
      const blob = new Blob([await f.async('arraybuffer')], { type: mime });
      return (this.urls[p] = URL.createObjectURL(blob));
    }
  }

  // ---------- colours ----------
  const hexToRgb = h => [parseInt(h.substr(0, 2), 16), parseInt(h.substr(2, 2), 16), parseInt(h.substr(4, 2), 16)];
  function rgbToHsl(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    const mx = Math.max(r, g, b), mn = Math.min(r, g, b), l = (mx + mn) / 2;
    let h = 0, s = 0;
    if (mx !== mn) {
      const d = mx - mn;
      s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
      h = mx === r ? (g - b) / d + (g < b ? 6 : 0) : mx === g ? (b - r) / d + 2 : (r - g) / d + 4;
      h /= 6;
    }
    return [h, s, l];
  }
  function hslToRgb(h, s, l) {
    if (s === 0) return [l * 255, l * 255, l * 255];
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
    const f = t => { if (t < 0) t += 1; if (t > 1) t -= 1; if (t < 1 / 6) return p + (q - p) * 6 * t; if (t < 1 / 2) return q; if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6; return p; };
    return [f(h + 1 / 3) * 255, f(h) * 255, f(h - 1 / 3) * 255];
  }
  function colorOf(holder, ctx) {
    if (!holder) return null;
    const c = /Clr$/.test(holder.localName) ? holder : holder.firstElementChild;
    if (!c) return null;
    const v = c.getAttribute('val');
    let hex;
    switch (c.localName) {
      case 'srgbClr': hex = v; break;
      case 'sysClr': hex = c.getAttribute('lastClr') || (v === 'window' ? 'FFFFFF' : '000000'); break;
      case 'schemeClr': {
        const m = ctx.clrMap[v] || v;
        hex = ctx.theme.colors[m] || ctx.theme.colors[v] || (/^(bg|lt)/.test(m) ? 'FFFFFF' : '000000');
        break;
      }
      case 'prstClr': hex = PRST[v] || '000000'; break;
      default: return null;
    }
    if (!hex || hex.length < 6) return null;
    let [r, g, b] = hexToRgb(hex), a = 1;
    for (const m of c.children) {
      const mv = num(m.getAttribute('val'), 100000) / 100000;
      if (m.localName === 'tint') { r += (255 - r) * (1 - mv); g += (255 - g) * (1 - mv); b += (255 - b) * (1 - mv); }
      else if (m.localName === 'shade') { r *= mv; g *= mv; b *= mv; }
      else if (m.localName === 'alpha') a = mv;
    }
    const lm = kid(c, 'lumMod'), lo = kid(c, 'lumOff');
    if (lm || lo) {
      const [h, s, l] = rgbToHsl(r, g, b);
      const nl = Math.min(1, Math.max(0, l * num(lm && lm.getAttribute('val'), 100000) / 100000 + num(lo && lo.getAttribute('val'), 0) / 100000));
      [r, g, b] = hslToRgb(h, s, nl);
    }
    return `rgba(${Math.round(r)},${Math.round(g)},${Math.round(b)},${a})`;
  }

  // ---------- theme / master data ----------
  function readTheme(x) {
    const t = { colors: {}, major: 'Calibri Light', minor: 'Calibri' };
    if (!x) return t;
    const cs = x.getElementsByTagNameNS('*', 'clrScheme')[0];
    if (cs) for (const c of cs.children) {
      const v = c.firstElementChild;
      if (v) t.colors[c.localName] = v.localName === 'sysClr' ? (v.getAttribute('lastClr') || '000000') : (v.getAttribute('val') || '000000');
    }
    const mj = x.getElementsByTagNameNS('*', 'majorFont')[0], mn = x.getElementsByTagNameNS('*', 'minorFont')[0];
    const lat = el => { const l = kid(el, 'latin'); return l && l.getAttribute('typeface'); };
    if (mj && lat(mj)) t.major = lat(mj);
    if (mn && lat(mn)) t.minor = lat(mn);
    return t;
  }
  function fontCss(face, ctx) {
    if (!face) face = ctx.theme.minor;
    if (face.startsWith('+mj')) face = ctx.theme.major; else if (face.startsWith('+mn')) face = ctx.theme.minor;
    return `'${face.replace(/'/g, '')}', 'Segoe UI', sans-serif`;
  }
  const spTree = doc => doc && path(doc.documentElement, 'cSld', 'spTree');
  function phMap(doc) {
    const m = { byIdx: {}, byType: {} };
    if (!doc) return m;
    for (const ph of doc.getElementsByTagNameNS('*', 'ph')) {
      const shape = ph.parentElement && ph.parentElement.parentElement && ph.parentElement.parentElement.parentElement;
      if (!shape) continue;
      const entry = { el: shape, ph };
      const idx = ph.getAttribute('idx'), type = ph.getAttribute('type') || 'body';
      if (idx != null && !(idx in m.byIdx)) m.byIdx[idx] = entry;
      if (!(type in m.byType)) m.byType[type] = entry;
    }
    return m;
  }
  function findPh(map, type, idx, useIdx) {
    if (!map) return null;
    if (useIdx && idx != null && map.byIdx[idx]) return map.byIdx[idx];
    if (map.byType[type]) return map.byType[type];
    const alt = { ctrTitle: 'title', subTitle: 'body', obj: 'body' }[type];
    return alt ? map.byType[alt] || null : null;
  }

  // ---------- geometry ----------
  function xfrmOf(el) {
    const x = el && (el.localName === 'xfrm' ? el : kid(el, 'xfrm'));
    if (!x) return null;
    const off = kid(x, 'off'), ext = kid(x, 'ext');
    if (!off || !ext) return null;
    return {
      x: num(off.getAttribute('x'), 0), y: num(off.getAttribute('y'), 0),
      w: num(ext.getAttribute('cx'), 0), h: num(ext.getAttribute('cy'), 0),
      rot: num(x.getAttribute('rot'), 0) / 60000,
      flipH: x.getAttribute('flipH') === '1', flipV: x.getAttribute('flipV') === '1',
      chOff: kid(x, 'chOff'), chExt: kid(x, 'chExt')
    };
  }
  function pos(b) {
    let t = '';
    if (b.rot) t += `rotate(${b.rot}deg) `;
    if (b.flipH) t += 'scaleX(-1) ';
    if (b.flipV) t += 'scaleY(-1) ';
    return `left:${b.x}px;top:${b.y}px;width:${b.w}px;height:${b.h}px;${t ? 'transform:' + t + ';' : ''}`;
  }

  // ---------- text ----------
  function runStyle(rPr, def, ctx, fs) {
    const g = n => { const v = rPr && rPr.getAttribute(n); return v != null ? v : def(d => d.getAttribute(n)); };
    const sz = g('sz');
    let size = (sz ? Number(sz) / 100 : 18) * 96 / 72 * fs;
    let color = rPr ? colorOf(kid(rPr, 'solidFill'), ctx) : null;
    if (!color) color = def(d => colorOf(kid(d, 'solidFill'), ctx)) || ctx.defaultText;
    const latin = rPr && kid(rPr, 'latin');
    const face = (latin && latin.getAttribute('typeface')) || def(d => { const l = kid(d, 'latin'); return l && l.getAttribute('typeface'); });
    let css = `color:${color};font-family:${fontCss(face, ctx)}`;
    const b = g('b'), i = g('i'), u = g('u'), base = num(g('baseline'), 0);
    if (b === '1' || b === 'true') css += ';font-weight:bold';
    if (i === '1' || i === 'true') css += ';font-style:italic';
    if (u && u !== 'none') css += ';text-decoration:underline';
    if (base) { css += `;vertical-align:${base > 0 ? 'super' : 'sub'}`; size *= 0.7; }
    return { css: css + `;font-size:${size}px`, size };
  }

  function renderParas(txBody, chain, ctx, fs) {
    let html = '';
    const autoNum = {};
    for (const p of kids(txBody, 'p')) {
      const pPr = kid(p, 'pPr');
      const lvl = num(pPr && pPr.getAttribute('lvl'), 0);
      const all = (pPr ? [pPr] : []).concat(chain.map(c => kid(c, `lvl${lvl + 1}pPr`)).filter(Boolean));
      const attr = n => { for (const e of all) { const v = e.getAttribute(n); if (v != null) return v; } return null; };
      const def = fn => { for (const e of all) { const d = kid(e, 'defRPr'); if (d) { const v = fn(d); if (v != null) return v; } } return null; };

      let bullet = null;
      for (const e of all) {
        if (kid(e, 'buNone')) { bullet = null; break; }
        const bc = kid(e, 'buChar');
        if (bc) { bullet = bc.getAttribute('char'); break; }
        if (kid(e, 'buAutoNum')) { autoNum[lvl] = (autoNum[lvl] || 0) + 1; bullet = autoNum[lvl] + '.'; break; }
      }

      const runs = [];
      let firstSize = null;
      for (const r of p.children) {
        if (r.localName === 'r' || r.localName === 'fld') {
          const t = kid(r, 't');
          const st = runStyle(kid(r, 'rPr'), def, ctx, fs);
          if (firstSize == null) firstSize = st.size;
          runs.push(`<span style="${st.css}">${esc(t ? t.textContent : '')}</span>`);
        } else if (r.localName === 'br') {
          runs.push('<br>');
        }
      }
      const size = firstSize || runStyle(kid(p, 'endParaRPr'), def, ctx, fs).size;
      const spc = n => {
        for (const e of all) {
          const s = kid(e, n);
          if (!s) continue;
          const pts = kid(s, 'spcPts');
          if (pts) return num(pts.getAttribute('val'), 0) / 100 * 96 / 72;
          const pct = kid(s, 'spcPct');
          if (pct) return num(pct.getAttribute('val'), 0) / 100000 * size * 1.2;
        }
        return 0;
      };
      let lh = 1.15;
      for (const e of all) {
        const l = kid(e, 'lnSpc');
        if (l) { const pct = kid(l, 'spcPct'); if (pct) lh = Math.max(0.6, num(pct.getAttribute('val'), 100000) / 100000 * 1.15); break; }
      }
      const algn = { l: 'left', ctr: 'center', r: 'right', just: 'justify', dist: 'justify' }[attr('algn') || 'l'] || 'left';
      const marL = num(attr('marL'), 0) / EMU, indent = num(attr('indent'), 0) / EMU;
      let left = marL, bulletHtml = '';
      if (bullet && runs.length) {
        const bw = indent < 0 ? -indent : size * 0.9;
        left = Math.max(marL, bw);
        bulletHtml = `<span style="display:inline-block;width:${bw}px;margin-left:${-bw}px;font-size:${size}px;color:${ctx.defaultText}">${esc(bullet)}</span>`;
      }
      html += `<p style="text-align:${algn};margin:${spc('spcBef')}px 0 ${spc('spcAft')}px ${left}px;line-height:${lh};font-size:${size}px">` +
        bulletHtml + (runs.length ? runs.join('') : '&nbsp;') + '</p>';
    }
    return html;
  }

  // ---------- shapes ----------
  function fillCss(spPr, style, ctx) {
    if (spPr) {
      if (kid(spPr, 'noFill')) return '';
      const sf = kid(spPr, 'solidFill');
      if (sf) { const c = colorOf(sf, ctx); return c ? `background:${c};` : ''; }
      const gf = kid(spPr, 'gradFill');
      if (gf) {
        const cols = Array.from(gf.getElementsByTagNameNS('*', 'gs')).map(s => colorOf(s, ctx)).filter(Boolean);
        if (cols.length) return `background:linear-gradient(180deg,${cols.join(',')});`;
      }
    }
    const fr = style && kid(style, 'fillRef');
    if (fr && num(fr.getAttribute('idx'), 0) > 0) { const c = colorOf(fr, ctx); if (c) return `background:${c};`; }
    return '';
  }
  function lineOf(spPr, style, ctx) {
    const ln = spPr && kid(spPr, 'ln');
    if (ln && kid(ln, 'noFill')) return null;
    let c = ln && colorOf(kid(ln, 'solidFill'), ctx);
    if (!c) { const lr = style && kid(style, 'lnRef'); if (lr && num(lr.getAttribute('idx'), 0) > 0) c = colorOf(lr, ctx); }
    if (!c) return null;
    return { c, w: Math.max(1, num(ln && ln.getAttribute('w'), 12700) / EMU) };
  }

  function renderSp(sp, ctx, T) {
    const nv = kid(sp, 'nvSpPr') || kid(sp, 'nvCxnSpPr');
    const phEl = nv && path(nv, 'nvPr', 'ph');
    let lay = null, mas = null, kind = 'other';
    if (phEl) {
      const type = phEl.getAttribute('type') || 'body', idx = phEl.getAttribute('idx');
      lay = findPh(ctx.layoutPh, type, idx, true);
      mas = findPh(ctx.masterPh, lay ? (lay.ph.getAttribute('type') || 'body') : type, idx, false);
      kind = type === 'title' || type === 'ctrTitle' ? 'title' : ['body', 'subTitle', 'obj'].includes(type) ? 'body' : 'other';
    }
    const spPr = kid(sp, 'spPr'), style = kid(sp, 'style');
    const raw = xfrmOf(spPr) || (lay && xfrmOf(kid(lay.el, 'spPr'))) || (mas && xfrmOf(kid(mas.el, 'spPr')));
    if (!raw) return '';
    const b = T(raw);
    const geom = path(spPr, 'prstGeom');
    const prst = geom ? geom.getAttribute('prst') : 'rect';
    const line = lineOf(spPr, style, ctx);

    if (/line|Connector/i.test(prst)) {
      if (!line) return '';
      return `<div class="sh" style="${pos(b)}height:0;border-top:${line.w}px solid ${line.c}"></div>`;
    }

    let css = pos(b) + fillCss(spPr, style, ctx);
    if (line) css += `border:${line.w}px solid ${line.c};`;
    if (prst === 'ellipse') css += 'border-radius:50%;';
    else if (prst === 'roundRect') css += `border-radius:${Math.min(b.w, b.h) * 0.12}px;`;

    let inner = '';
    const txBody = kid(sp, 'txBody');
    if (txBody && txBody.getElementsByTagNameNS('*', 't').length) {
      const bps = [kid(txBody, 'bodyPr'), lay && path(lay.el, 'txBody', 'bodyPr'), mas && path(mas.el, 'txBody', 'bodyPr')].filter(Boolean);
      const ba = n => { for (const x of bps) { const v = x.getAttribute(n); if (v != null) return v; } return null; };
      let fs = 1;
      for (const x of bps) { const na = kid(x, 'normAutofit'); if (na) { fs = num(na.getAttribute('fontScale'), 100000) / 100000; break; } }
      const chain = [
        kid(txBody, 'lstStyle'),
        lay && path(lay.el, 'txBody', 'lstStyle'),
        mas && path(mas.el, 'txBody', 'lstStyle'),
        kind === 'title' ? ctx.tx.title : kind === 'body' ? ctx.tx.body : ctx.tx.other
      ].filter(Boolean);
      const anchor = { t: 'flex-start', ctr: 'center', b: 'flex-end' }[ba('anchor') || 't'] || 'flex-start';
      const pad = [ba('tIns') ?? 45720, ba('rIns') ?? 91440, ba('bIns') ?? 45720, ba('lIns') ?? 91440].map(v => num(v, 0) / EMU + 'px').join(' ');
      const nowrap = ba('wrap') === 'none' ? 'white-space:nowrap;' : '';
      inner = `<div style="position:absolute;inset:0;padding:${pad};display:flex;flex-direction:column;justify-content:${anchor};${nowrap}">` +
        renderParas(txBody, chain, ctx, fs) + '</div>';
    }
    if (!inner && !css.includes('background') && !line) return '';
    return `<div class="sh" style="${css}">${inner}</div>`;
  }

  async function renderPic(pic, ctx, T) {
    const phEl = path(pic, 'nvPicPr', 'nvPr', 'ph');
    let raw = xfrmOf(kid(pic, 'spPr'));
    if (!raw && phEl) {
      const lay = findPh(ctx.layoutPh, phEl.getAttribute('type') || 'body', phEl.getAttribute('idx'), true);
      raw = lay && xfrmOf(kid(lay.el, 'spPr'));
    }
    if (!raw) return '';
    const blip = path(pic, 'blipFill', 'blip');
    const id = blip && blip.getAttributeNS(NS_R, 'embed');
    const rel = id && ctx.rels[id];
    const url = rel && !rel.ext ? await ctx.pkg.url(rel.target) : null;
    return `<div class="sh" style="${pos(T(raw))}">` +
      (url ? `<img src="${url}" style="width:100%;height:100%;display:block">` : '<div style="width:100%;height:100%;background:#eceff4"></div>') +
      '</div>';
  }

  function renderFrame(gf, ctx, T) {
    const raw = xfrmOf(kid(gf, 'xfrm'));
    if (!raw) return '';
    const b = T(raw);
    const gd = path(gf, 'graphic', 'graphicData');
    const tbl = gd && kid(gd, 'tbl');
    if (tbl) {
      const cols = kids(kid(tbl, 'tblGrid'), 'gridCol').map(c => num(c.getAttribute('w'), 0) / EMU);
      let h = '<table style="border-collapse:collapse;width:100%;table-layout:fixed"><colgroup>' +
        cols.map(w => `<col style="width:${w}px">`).join('') + '</colgroup>';
      for (const tr of kids(tbl, 'tr')) {
        h += `<tr style="height:${num(tr.getAttribute('h'), 0) / EMU}px">`;
        for (const tc of kids(tr, 'tc')) {
          if (tc.getAttribute('hMerge') === '1' || tc.getAttribute('vMerge') === '1') continue;
          const tcPr = kid(tc, 'tcPr'), bg = tcPr ? colorOf(kid(tcPr, 'solidFill'), ctx) : null, body = kid(tc, 'txBody');
          h += `<td colspan="${tc.getAttribute('gridSpan') || 1}" rowspan="${tc.getAttribute('rowSpan') || 1}" ` +
            `style="border:1px solid #9aa3b5;padding:4px 7px;vertical-align:top;${bg ? 'background:' + bg : ''}">` +
            (body ? renderParas(body, [kid(body, 'lstStyle'), ctx.tx.other].filter(Boolean), ctx, 1) : '') + '</td>';
        }
        h += '</tr>';
      }
      return `<div class="sh" style="${pos(b)}">${h}</table></div>`;
    }
    const uri = (gd && gd.getAttribute('uri')) || '';
    const label = /chart/.test(uri) ? 'Chart' : /diagram/.test(uri) ? 'SmartArt diagram' : 'Embedded object';
    return `<div class="sh" style="${pos(b)}background:#f1f3f7;border:1px dashed #b8bfcc;display:flex;align-items:center;` +
      `justify-content:center;color:#7a8396;font:16px 'Segoe UI',sans-serif">${label} (not shown in Focus Study)</div>`;
  }

  async function renderTree(tree, ctx, T, skipPh) {
    let h = '';
    if (!tree) return h;
    for (const el of tree.children) {
      switch (el.localName) {
        case 'sp':
        case 'cxnSp':
          if (skipPh && path(el, 'nvSpPr', 'nvPr', 'ph')) break;
          h += renderSp(el, ctx, T);
          break;
        case 'pic':
          if (skipPh && path(el, 'nvPicPr', 'nvPr', 'ph')) break;
          h += await renderPic(el, ctx, T);
          break;
        case 'grpSp': {
          const g = xfrmOf(kid(el, 'grpSpPr'));
          if (!g) { h += await renderTree(el, ctx, T, skipPh); break; }
          const chx = num(g.chOff && g.chOff.getAttribute('x'), g.x), chy = num(g.chOff && g.chOff.getAttribute('y'), g.y);
          const chw = num(g.chExt && g.chExt.getAttribute('cx'), g.w) || 1, chh = num(g.chExt && g.chExt.getAttribute('cy'), g.h) || 1;
          const sx = g.w / chw, sy = g.h / chh;
          const TT = c => T(Object.assign({}, c, { x: g.x + (c.x - chx) * sx, y: g.y + (c.y - chy) * sy, w: c.w * sx, h: c.h * sy }));
          h += await renderTree(el, ctx, TT, skipPh);
          break;
        }
        case 'graphicFrame':
          h += renderFrame(el, ctx, T);
          break;
        case 'AlternateContent': {
          const alt = kid(el, 'Fallback') || kid(el, 'Choice');
          if (alt) h += await renderTree(alt, ctx, T, skipPh);
          break;
        }
      }
    }
    return h;
  }

  async function bgCss(layers, ctx) {
    for (const d of layers) {
      const bg = path(d.xml.documentElement, 'cSld', 'bg');
      if (!bg) continue;
      const bgPr = kid(bg, 'bgPr');
      if (bgPr) {
        const lctx = Object.assign({}, ctx, { rels: d.rels });
        const sf = kid(bgPr, 'solidFill');
        if (sf) return 'background:' + (colorOf(sf, lctx) || '#fff');
        const gf = kid(bgPr, 'gradFill');
        if (gf) {
          const cols = Array.from(gf.getElementsByTagNameNS('*', 'gs')).map(s => colorOf(s, lctx)).filter(Boolean);
          if (cols.length) return `background:linear-gradient(180deg,${cols.join(',')})`;
        }
        const blip = path(bgPr, 'blipFill', 'blip');
        const id = blip && blip.getAttributeNS(NS_R, 'embed');
        const rel = id && d.rels[id];
        const url = rel && !rel.ext ? await ctx.pkg.url(rel.target) : null;
        if (url) return `background:url(${url}) center/cover`;
      }
      const br = kid(bg, 'bgRef');
      if (br) { const c = colorOf(br, ctx); if (c) return 'background:' + c; }
    }
    return 'background:#fff';
  }

  async function renderSlide(pkg, slidePath, W, H, n) {
    const slide = await pkg.xml(slidePath);
    const sRels = await pkg.rels(slidePath);
    const layRel = Object.values(sRels).find(r => r.type.endsWith('/slideLayout'));
    const layout = layRel ? await pkg.xml(layRel.target) : null;
    const lRels = layRel ? await pkg.rels(layRel.target) : {};
    const masRel = Object.values(lRels).find(r => r.type.endsWith('/slideMaster'));
    const master = masRel ? await pkg.xml(masRel.target) : null;
    const mRels = masRel ? await pkg.rels(masRel.target) : {};
    const themeRel = Object.values(mRels).find(r => r.type.endsWith('/theme'));
    const theme = readTheme(themeRel ? await pkg.xml(themeRel.target) : null);

    const clrMap = {};
    const cm = master && master.getElementsByTagNameNS('*', 'clrMap')[0];
    if (cm) for (const a of cm.attributes) clrMap[a.name] = a.value;
    const ovr = slide.getElementsByTagNameNS('*', 'overrideClrMapping')[0];
    if (ovr) for (const a of ovr.attributes) clrMap[a.name] = a.value;

    const ts = master && master.getElementsByTagNameNS('*', 'txStyles')[0];
    const ctx = {
      pkg, theme, clrMap,
      tx: { title: kid(ts, 'titleStyle'), body: kid(ts, 'bodyStyle'), other: kid(ts, 'otherStyle') },
      masterPh: phMap(master), layoutPh: phMap(layout)
    };
    ctx.defaultText = '#' + (theme.colors[clrMap.tx1 || 'dk1'] || '000000');
    const T = b => ({ x: b.x / EMU, y: b.y / EMU, w: b.w / EMU, h: b.h / EMU, rot: b.rot, flipH: b.flipH, flipV: b.flipV });

    let html = '';
    const hideMaster = slide.documentElement.getAttribute('showMasterSp') === '0' ||
      (layout && layout.documentElement.getAttribute('showMasterSp') === '0');
    if (master && !hideMaster) html += await renderTree(spTree(master), Object.assign({}, ctx, { rels: mRels }), T, true);
    if (layout && slide.documentElement.getAttribute('showMasterSp') !== '0')
      html += await renderTree(spTree(layout), Object.assign({}, ctx, { rels: lRels }), T, true);
    html += await renderTree(spTree(slide), Object.assign({}, ctx, { rels: sRels }), T, false);

    const layers = [{ xml: slide, rels: sRels }, { xml: layout, rels: lRels }, { xml: master, rels: mRels }].filter(d => d.xml);
    const bg = await bgCss(layers, ctx);

    const item = document.createElement('div');
    item.innerHTML = `<div class="slide-wrap"><div class="slide" style="width:${W}px;height:${H}px;${bg}">${html}</div></div>` +
      `<div class="num">Slide ${n}</div>`;
    return item;
  }

  async function render(buf, out) {
    const zip = await JSZip.loadAsync(buf);
    const pkg = new Pkg(zip);
    const presPath = 'ppt/presentation.xml';
    const pres = await pkg.xml(presPath);
    if (!pres) throw new Error('this is not a PowerPoint (.pptx) file');
    const sz = pres.getElementsByTagNameNS('*', 'sldSz')[0];
    const W = num(sz && sz.getAttribute('cx'), 12192000) / EMU, H = num(sz && sz.getAttribute('cy'), 6858000) / EMU;
    const presRels = await pkg.rels(presPath);
    const ids = Array.from(pres.getElementsByTagNameNS('*', 'sldId')).map(s => s.getAttributeNS(NS_R, 'id'));

    const container = document.createElement('div');
    container.className = 'slides';
    out.append(container);
    const fitOne = wrap => {
      const s = Math.min(1.6, Math.max(0.2, (window.innerWidth - 56) / W));
      wrap.style.width = W * s + 'px';
      wrap.style.height = H * s + 'px';
      wrap.firstElementChild.style.transform = `scale(${s})`;
    };
    window.addEventListener('resize', () => container.querySelectorAll('.slide-wrap').forEach(fitOne));

    let n = 0;
    for (const id of ids) {
      const rel = presRels[id];
      if (!rel) continue;
      n++;
      let item;
      try {
        item = await renderSlide(pkg, rel.target, W, H, n);
      } catch (e) {
        item = document.createElement('div');
        item.className = 'num';
        item.textContent = `Slide ${n} could not be shown (${e.message})`;
      }
      container.append(item);
      const wrap = item.querySelector('.slide-wrap');
      if (wrap) fitOne(wrap);
      if (n % 4 === 0) await new Promise(r => setTimeout(r));
    }
    if (n === 0) throw new Error('the presentation has no slides');
  }

  return { render };
})();
