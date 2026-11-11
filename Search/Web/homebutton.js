/*
 * Torvia: "Search & Add" button on the Jellyfin home screen.
 * Shown only on the home screen, and only to users allowed to use Search & Add.
 * Restyle it from Dashboard → General → Custom CSS with the class .torvia-search-btn
 */
(function () {
    'use strict';
    if (window.__torviaHomeButton) return;
    window.__torviaHomeButton = true;

    var BTN_ID = 'torviaSearchBtn';
    var state = { userId: null, show: false, checking: false };

    // Default look. It uses the theme's own colours where it has them (PHOENICX CINEMA, ElegantFin and
    // similar), with plain fallbacks. It goes first in <head>, so any Custom CSS wins over it.
    function addStyle() {
        if (document.getElementById('torviaSearchBtnStyle')) return;
        var css =
            '.torvia-search-btn{display:inline-flex;align-items:center;justify-content:center;gap:.45em;box-sizing:border-box;' +
            'height:2.36rem;padding:0 1.05rem;margin:0 .35rem;border-radius:var(--ef12-navRadius,999px);' +
            'border:1px solid var(--px-future-line,rgba(186,177,255,.22));' +
            'background:var(--ef12-navPillBackground,rgba(255,255,255,.08));color:var(--textColor,#f7f7fc);' +
            'font-family:inherit;font-size:.95rem;font-weight:500;line-height:1;white-space:nowrap;text-decoration:none;cursor:pointer;' +
            'box-shadow:var(--px-pill-shadow,0 5px 18px rgba(93,85,231,.16));' +
            '-webkit-backdrop-filter:var(--px-surface-blur,blur(12px));backdrop-filter:var(--px-surface-blur,blur(12px));' +
            'transition:background-color .22s ease,color .22s ease,box-shadow .22s ease,transform .15s ease,border-color .22s ease}' +
            '.torvia-search-btn:hover,.torvia-search-btn:focus-visible{background:var(--hoverTabBackgroundColor,rgba(255,255,255,.15));' +
            'color:var(--zeroContrastColor,#fff);border-color:var(--px-accent-light,#cbc4ff);' +
            'box-shadow:var(--px-pill-hover-shadow,0 8px 24px rgba(147,121,255,.27));outline:none}' +
            '.torvia-search-btn:active{transform:scale(.95)}' +
            '.torvia-search-btn .torvia-search-icon{font-size:1.05em;line-height:1}' +
            '.torvia-search-btn[hidden]{display:none!important}' +
            '.torvia-search-btn.torvia-float{position:fixed;right:1.2rem;bottom:1.2rem;z-index:1000;height:2.8rem;padding:0 1.25rem;' +
            'background:var(--px-surface,rgba(18,22,34,.92))}' +
            '.layout-mobile .torvia-search-btn:not(.torvia-float),.torvia-search-btn.torvia-compact{width:2.36rem;padding:0;margin:0 .2rem}' +
            '.layout-mobile .torvia-search-btn:not(.torvia-float) .torvia-search-label,.torvia-search-btn.torvia-compact .torvia-search-label{display:none}' +
            '@media (max-width:640px){.torvia-search-btn:not(.torvia-float){width:2.36rem;padding:0;margin:0 .2rem}' +
            '.torvia-search-btn:not(.torvia-float) .torvia-search-label{display:none}}';
        var st = document.createElement('style');
        st.id = 'torviaSearchBtnStyle';
        st.textContent = css;
        var head = document.head || document.documentElement;
        head.insertBefore(st, head.firstChild);
    }

    function api() { return window.ApiClient || (window.ConnectionManager && window.ConnectionManager.currentApiClient && window.ConnectionManager.currentApiClient()); }

    function currentUser() {
        var c = api();
        try { return c && c.getCurrentUserId && c.getCurrentUserId(); } catch (e) { return null; }
    }

    function onHome() {
        var h = (location.hash || '').toLowerCase();
        if (/^#!?\/?(home|home\.html)([?/]|$)/.test(h)) return true;
        var page = document.querySelector('#indexPage:not(.hide), .homePage:not(.hide)');
        return !!page && (h === '' || h === '#' || h === '#/');
    }

    function webUrl() {
        var c = api();
        var url = c && c.getUrl ? c.getUrl('TorviaSearch/Web') : location.pathname.replace(/\/web\/.*$/i, '') + '/TorviaSearch/Web';
        return url + (url.indexOf('?') < 0 ? '?' : '&') + 'from=home';
    }

    function check() {
        var c = api();
        var uid = currentUser();
        if (!uid || !c) { state.userId = null; state.show = false; return; }
        if (uid === state.userId || state.checking) return;
        state.checking = true;
        var req = c.getJSON ? c.getJSON(c.getUrl('TorviaSearch/Status'))
            : c.ajax({ type: 'GET', url: c.getUrl('TorviaSearch/Status'), dataType: 'json' });
        Promise.resolve(req).then(function (s) {
            state.userId = uid;
            state.show = !!(s && s.enabled && s.homeButton !== false);
        }, function () {
            state.userId = uid;
            state.show = false;
        }).then(function () { state.checking = false; update(); });
    }

    function findAnchor() {
        // The search icon in the top bar (new and classic header).
        return document.querySelector(
            '.MuiAppBar-root a[href$="#/search"], .MuiAppBar-root a[href*="/search"], .MuiAppBar-root [aria-label="Search"], ' +
            '.MuiAppBar-root [title="Search"], .skinHeader .headerSearchButton, .headerRight .headerSearchButton');
    }

    function makeButton() {
        var a = document.createElement('a');
        a.id = BTN_ID;
        a.className = 'torvia-search-btn';
        a.setAttribute('role', 'button');
        a.setAttribute('title', 'Search & Add');
        a.setAttribute('aria-label', 'Search & Add');
        a.innerHTML = '<span class="torvia-search-icon" aria-hidden="true">🔍</span><span class="torvia-search-label">Search &amp; Add</span>';
        a.addEventListener('click', function (e) {
            e.preventDefault();
            location.href = webUrl();
        });
        return a;
    }

    function place(btn) {
        var anchor = findAnchor();
        if (anchor && anchor.parentNode) {
            btn.classList.remove('torvia-float');
            if (btn.nextSibling !== anchor || btn.parentNode !== anchor.parentNode) {
                anchor.parentNode.insertBefore(btn, anchor);
            }
            return;
        }
        var right = document.querySelector('.skinHeader .headerRight');
        if (right) {
            btn.classList.remove('torvia-float');
            if (btn.parentNode !== right) right.insertBefore(btn, right.firstChild);
            return;
        }
        // No top bar found: show it as a small floating button instead.
        btn.classList.add('torvia-float');
        if (btn.parentNode !== document.body) document.body.appendChild(btn);
    }

    function update() {
        var btn = document.getElementById(BTN_ID);
        var want = state.show && currentUser() === state.userId && onHome();
        if (!want) {
            if (btn) btn.hidden = true;
            return;
        }
        addStyle();
        if (!btn) btn = makeButton();
        btn.href = webUrl();
        place(btn);
        btn.hidden = false;
    }

    var timer = null;
    function soon() {
        if (timer) return;
        timer = setTimeout(function () { timer = null; check(); update(); }, 200);
    }

    function start() {
        window.addEventListener('hashchange', soon);
        window.addEventListener('popstate', soon);
        document.addEventListener('viewshow', soon, true);
        new MutationObserver(soon).observe(document.body, { childList: true, subtree: true });
        soon();
    }

    if (document.body) start();
    else document.addEventListener('DOMContentLoaded', start);
})();
