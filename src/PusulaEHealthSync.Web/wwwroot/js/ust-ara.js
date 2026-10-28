// GENEL ARAMA (2026-10-28, ClinicAsist incelemesinden). Ust cubuktaki kutu iki is yapar:
//   1) SAYFA arama -- liste sol menudeki baglantilardan okunur, yani herkes yalnizca kendi
//      rolunun gordugu sayfalari bulur (Arsiv rolu yalnizca Yatan Hastalar'i).
//   2) PROTOKOL arama -- rakamsa dogrudan Protokol Detay; yazi ise Protokol Listesi'nin
//      kendi aramasi (hasta adi, FIN, protokol no). Yeni bir sunucu sorgusu yok.
// Kisayol: "/" ya da Ctrl+K. Oklar + Enter ile secilir, Esc kapatir.
(function () {
    var kutu = document.getElementById('ust-ara');
    var liste = document.getElementById('ust-ara-sonuc');
    if (!kutu || !liste) return;
    var protokolAcik = kutu.dataset.protokol === '1';

    // Turkce/Azerice harfleri sadelestir: "Şiş" ile "sis" eslessin.
    function sade(t) {
        return (t || '').toLocaleLowerCase('tr')
            .replace(/ı/g, 'i').replace(/ş/g, 's').replace(/ğ/g, 'g').replace(/ü/g, 'u')
            .replace(/ö/g, 'o').replace(/ç/g, 'c').replace(/ə/g, 'e')
            .normalize('NFD').replace(/[̀-ͯ]/g, '');
    }

    function kac(t) {
        return String(t).replace(/[&<>"]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
        });
    }

    var sayfalar = [];
    var bolum = '';
    document.querySelectorAll('.sidebar .nav-section-label, .sidebar a.nav-link').forEach(function (el) {
        if (el.classList.contains('nav-section-label')) { bolum = el.textContent.trim(); return; }
        var ad = el.textContent.trim();
        sayfalar.push({ ad: ad, bolum: bolum, href: el.getAttribute('href'), anahtar: sade(ad + ' ' + (el.dataset.ara || '')) });
    });

    // Eslesen parcayi isaretle. sade() harf sayisini degistirmedigi icin konum ayni kalir.
    function vurgula(ad, q) {
        var i = q ? sade(ad).indexOf(sade(q)) : -1;
        if (i < 0) return kac(ad);
        return kac(ad.slice(0, i)) + '<mark>' + kac(ad.slice(i, i + q.length)) + '</mark>' + kac(ad.slice(i + q.length));
    }

    var secili = -1;
    function ogeler() { return liste.querySelectorAll('.ust-ara-oge'); }
    function sec(i) {
        var o = ogeler();
        if (!o.length) return;
        secili = (i + o.length) % o.length;
        o.forEach(function (x, j) { x.setAttribute('aria-selected', j === secili ? 'true' : 'false'); });
        o[secili].scrollIntoView({ block: 'nearest' });
    }

    function kapat() { liste.hidden = true; kutu.setAttribute('aria-expanded', 'false'); secili = -1; }

    function goster() {
        var q = kutu.value.trim();
        if (!q) { kapat(); return; }
        var sq = sade(q);
        var html = '';

        if (protokolAcik && /^\d{3,}$/.test(q)) {
            html += '<div class="ust-ara-grup">Protokol</div>'
                + '<a class="ust-ara-oge" role="option" href="/Protokol/' + q + '">Protokol <b>' + q + '</b>'
                + '<small>Protokol detayını aç</small></a>';
        }
        var bulunan = sayfalar.filter(function (s) { return s.anahtar.indexOf(sq) >= 0; }).slice(0, 8);
        if (bulunan.length) {
            html += '<div class="ust-ara-grup">Sayfalar</div>';
            bulunan.forEach(function (s) {
                html += '<a class="ust-ara-oge" role="option" href="' + kac(s.href) + '">' + vurgula(s.ad, q)
                    + (s.bolum ? '<small>' + kac(s.bolum) + '</small>' : '') + '</a>';
            });
        }
        if (protokolAcik && q.length >= 2) {
            html += '<div class="ust-ara-grup">Protokol Listesi</div>'
                + '<a class="ust-ara-oge" role="option" href="/?Search=' + encodeURIComponent(q) + '">&ldquo;' + kac(q) + '&rdquo; için ara'
                + '<small>Hasta adı, FIN ya da protokol no</small></a>';
        }
        liste.innerHTML = html || '<div class="ust-ara-bos">Eşleşen sayfa yok.</div>';
        liste.hidden = false;
        kutu.setAttribute('aria-expanded', 'true');
        secili = -1;
        if (ogeler().length) sec(0);
    }

    kutu.addEventListener('input', goster);
    kutu.addEventListener('focus', function () { if (kutu.value.trim()) goster(); });
    kutu.addEventListener('keydown', function (e) {
        if (e.key === 'ArrowDown') { e.preventDefault(); sec(secili + 1); }
        else if (e.key === 'ArrowUp') { e.preventDefault(); sec(secili - 1); }
        else if (e.key === 'Enter') {
            var o = ogeler();
            if (secili >= 0 && o[secili]) { e.preventDefault(); window.location.href = o[secili].getAttribute('href'); }
        }
        else if (e.key === 'Escape') { kapat(); kutu.blur(); }
    });
    document.addEventListener('click', function (e) { if (!e.target.closest('.ust-ara')) kapat(); });
    document.addEventListener('keydown', function (e) {
        var a = document.activeElement;
        var yaziyor = a && (/^(INPUT|TEXTAREA|SELECT)$/.test(a.tagName) || a.isContentEditable);
        if ((e.key === '/' && !yaziyor) || ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k')) {
            e.preventDefault(); kutu.focus(); kutu.select();
        }
    });
})();
