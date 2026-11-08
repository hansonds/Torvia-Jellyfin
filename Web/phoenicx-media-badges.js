/* ==========================================================
   PHOENICX CINEMA — Media Badges v1.0
   Adds quality badges under the year / rating row on movie and
   episode detail pages:  4K · Dolby Vision · HDR10+ · HDR10 · HLG ·
   HDR · SDR · 10-bit · 8-bit · HEVC · Atmos · DTS:X · DTS-HD MA ·
   TrueHD · DD+ · 7.1 …
   Styling lives in the PHOENICX CINEMA CSS ("media badges" block).
   Works in Jellyfin web, Jellyfin Media Player and the Android app.
   phoenicx.org
   ========================================================== */
(function () {
  'use strict';
  if (window.__pxMediaBadges) return;
  window.__pxMediaBadges = true;

  var TYPES = { Movie: 1, Episode: 1, Video: 1, MusicVideo: 1 };
  var cache = {};
  var timer = null;

  /* ---------- helpers ---------- */
  function itemIdFromUrl() {
    var h = location.hash || '';
    var q = h.indexOf('?') > -1 ? h.slice(h.indexOf('?') + 1) : location.search.slice(1);
    try { return new URLSearchParams(q).get('id'); } catch (e) { return null; }
  }

  function activePage() {
    var pages = document.querySelectorAll('#itemDetailPage');
    for (var i = 0; i < pages.length; i++) {
      if (!pages[i].classList.contains('hide')) return pages[i];
    }
    return null;
  }

  function getItem(id) {
    if (cache[id]) return cache[id];
    var api = window.ApiClient;
    if (!api || !api.getCurrentUserId) return Promise.resolve(null);
    cache[id] = api.getItem(api.getCurrentUserId(), id).catch(function () {
      delete cache[id];
      return null;
    });
    return cache[id];
  }

  function selectValue(page, cls) {
    var s = page.querySelector('select.' + cls);
    var box = s && s.closest('.selectContainer, .trackSelectionFieldContainer, div');
    if (!s || !s.value || (box && box.classList.contains('hide'))) return null;
    return s.value;
  }

  function streamByIndex(streams, type, index) {
    var list = streams.filter(function (s) { return s.Type === type; });
    if (index != null) {
      for (var i = 0; i < list.length; i++) if (String(list[i].Index) === String(index)) return list[i];
    }
    for (var j = 0; j < list.length; j++) if (list[j].IsDefault) return list[j];
    return list[0] || null;
  }

  /* ---------- video ---------- */
  function resolution(v) {
    var w = v.Width || 0, h = v.Height || 0;
    if (w >= 7000 || h >= 4000) return ['8K', 'res4k'];
    if (w >= 3800 || h >= 2000) return ['4K', 'res4k'];
    if (w >= 2500 || h >= 1400) return ['1440p', 'res'];
    if (w >= 1900 || h >= 1000) return ['1080p', 'res'];
    if (w >= 1200 || h >= 700) return ['720p', 'res'];
    if (h) return ['SD', 'res'];
    return null;
  }

  var VIDEO_CODECS = { hevc: 'HEVC', h265: 'HEVC', h264: 'H.264', avc: 'H.264', av1: 'AV1', vp9: 'VP9', vp8: 'VP8', mpeg2video: 'MPEG-2', mpeg4: 'MPEG-4', vc1: 'VC-1' };

  function videoBadges(v) {
    var out = [];
    var r = resolution(v);
    if (r) out.push(r);

    var type = String(v.VideoRangeType || '').toUpperCase();
    var range = String(v.VideoRange || '').toUpperCase();
    var dv = type.indexOf('DOVI') === 0;
    if (dv) out.push(['Dolby <b>Vision</b>', 'dv']);
    if (type.indexOf('HDR10PLUS') > -1) out.push(['HDR10+', 'hdr10p']);
    else if (type.indexOf('HDR10') > -1) out.push(['HDR10', 'hdr10']);
    else if (type.indexOf('HLG') > -1) out.push(['HLG', 'hlg']);
    else if (!dv && range === 'HDR') out.push(['HDR', 'hdr']);
    else if (!dv && (type === 'SDR' || range === 'SDR')) out.push(['SDR', 'sdr']);

    var bits = v.BitDepth;
    if (!bits && v.PixelFormat) bits = /p1[02]/.test(v.PixelFormat) ? (v.PixelFormat.indexOf('12') > -1 ? 12 : 10) : 8;
    if (bits) out.push([bits + '-bit', bits >= 10 ? 'bit10' : 'bit8']);

    var c = VIDEO_CODECS[String(v.Codec || '').toLowerCase()];
    if (c) out.push([c, 'codec']);
    return out;
  }

  /* ---------- audio ---------- */
  function channels(a) {
    var layout = String(a.ChannelLayout || '').toLowerCase();
    var m = layout.match(/\d\.\d/);
    if (m) return m[0];
    var n = a.Channels || 0;
    return ({ 1: '1.0', 2: '2.0', 3: '2.1', 6: '5.1', 7: '6.1', 8: '7.1' })[n] || (n ? n + 'CH' : null);
  }

  function audioBadges(a) {
    var out = [];
    var codec = String(a.Codec || '').toLowerCase();
    var profile = String(a.Profile || '');
    var text = (profile + ' ' + (a.DisplayTitle || '') + ' ' + (a.Title || '')).toLowerCase();
    var atmos = text.indexOf('atmos') > -1;
    var dtsx = /dts[:\- ]?x(?![a-z0-9])/.test(text);

    if (atmos) out.push(['Dolby <b>Atmos</b>', 'atmos']);

    if (codec === 'dts' || codec === 'dca') {
      if (dtsx) out.push(['DTS:X', 'dtsx']);
      else if (/hd ma|hd-ma|master/i.test(profile)) out.push(['DTS-HD MA', 'dts']);
      else if (/hra|high res/i.test(profile)) out.push(['DTS-HD HRA', 'dts']);
      else if (/es/i.test(profile)) out.push(['DTS-ES', 'dts']);
      else out.push(['DTS', 'dts']);
    } else if (codec === 'truehd' || codec === 'mlp') out.push(['TrueHD', 'dolby']);
    else if (codec === 'eac3') out.push(['DD+', 'dolby']);
    else if (codec === 'ac3') out.push(['Dolby Digital', 'dolby']);
    else if (codec === 'flac') out.push(['FLAC', 'lossless']);
    else if (codec.indexOf('pcm') === 0) out.push(['LPCM', 'lossless']);
    else if (codec === 'aac') out.push(['AAC', 'codec']);
    else if (codec === 'opus') out.push(['Opus', 'codec']);
    else if (codec === 'mp3') out.push(['MP3', 'codec']);
    else if (codec) out.push([codec.toUpperCase(), 'codec']);

    var ch = channels(a);
    if (ch) out.push([ch, 'ch']);
    return out;
  }

  /* ---------- render ---------- */
  function group(list) {
    if (!list.length) return '';
    return '<span class="pxBadgeGroup">' + list.map(function (b) {
      return '<span class="pxBadge pxBadge--' + b[1] + '">' + b[0] + '</span>';
    }).join('') + '</span>';
  }

  function render() {
    timer = null;
    var page = activePage();
    var id = itemIdFromUrl();
    var old = page && page.querySelector('.pxBadges');
    if (!page || !id) return;

    var anchor = page.querySelector('.itemMiscInfo-primary');
    if (!anchor) return;

    getItem(id).then(function (item) {
      if (itemIdFromUrl() !== id || activePage() !== page) return;
      old = page.querySelector('.pxBadges');
      if (!item || !TYPES[item.Type] || !item.MediaSources || !item.MediaSources.length) {
        if (old) old.remove();
        return;
      }

      var srcId = selectValue(page, 'selectSource');
      var source = item.MediaSources.filter(function (s) { return s.Id === srcId; })[0] || item.MediaSources[0];
      var streams = source.MediaStreams || [];
      var v = streamByIndex(streams, 'Video', selectValue(page, 'selectVideo'));
      var a = streamByIndex(streams, 'Audio', selectValue(page, 'selectAudio'));

      var key = id + '|' + source.Id + '|' + (v ? v.Index : '') + '|' + (a ? a.Index : '');
      if (old && old.getAttribute('data-key') === key && old.previousElementSibling === anchor) return;

      var html = group(v ? videoBadges(v) : []) + group(a ? audioBadges(a) : []);
      if (old) old.remove();
      if (!html) return;

      var box = document.createElement('div');
      box.className = 'pxBadges';
      box.setAttribute('data-key', key);
      box.innerHTML = html;
      anchor.insertAdjacentElement('afterend', box);
    });
  }

  function schedule() {
    if (!timer) timer = setTimeout(render, 150);
  }

  new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
  window.addEventListener('hashchange', schedule);
  document.addEventListener('viewshow', schedule);
  document.addEventListener('change', function (e) {
    var t = e.target;
    if (t && t.matches && t.matches('select.selectSource, select.selectVideo, select.selectAudio')) schedule();
  });
  schedule();
})();