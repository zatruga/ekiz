// TARIH ARALIGI SECICI (2026-10-26) -- tasarim tuvalindeki "B" secenegi, tek aylik takvimle.
//
// Cubukta tek dugme ("Son 7 gun · 20 – 26 Ekim 2026"); tiklayinca solda hazir araliklar,
// sagda takvim. Hazir aralik TEK TIKLA uygulanir; takvimde iki gun secilip "Uygula"ya
// basilir. Deger formdaki gizli From/To alanlarina yazilir ve form gonderilir -- sunucu
// tarafi eskisi gibi From/To okuyor.
//
// Isaretleme: Pages/Shared/_TarihSecici.cshtml. Bugunden ileri gun secilemez.
(function () {
    'use strict';

    var AYLAR = ['Ocak', 'Şubat', 'Mart', 'Nisan', 'Mayıs', 'Haziran', 'Temmuz', 'Ağustos', 'Eylül', 'Ekim', 'Kasım', 'Aralık'];
    var KISA = ['Oca', 'Şub', 'Mar', 'Nis', 'May', 'Haz', 'Tem', 'Ağu', 'Eyl', 'Eki', 'Kas', 'Ara'];
    var GUNLER = ['Pt', 'Sa', 'Ça', 'Pe', 'Cu', 'Ct', 'Pz'];

    function gun(d) { return new Date(d.getFullYear(), d.getMonth(), d.getDate()); }
    function ekle(d, n) { var x = new Date(d); x.setDate(x.getDate() + n); return x; }
    function ayni(a, b) { return !!a && !!b && a.getTime() === b.getTime(); }
    function iso(d) {
        return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
    }
    function oku(s) {
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s || '');
        return m ? new Date(+m[1], +m[2] - 1, +m[3]) : null;
    }
    function noktali(d) {
        return String(d.getDate()).padStart(2, '0') + '.' + String(d.getMonth() + 1).padStart(2, '0') + '.' + d.getFullYear();
    }

    // "20 – 26 Ekim 2026", "28 Eyl – 3 Eki 2026", "28 Ara 2025 – 3 Oca 2026", "26 Ekim 2026"
    function aralikMetni(a, b) {
        if (ayni(a, b)) return a.getDate() + ' ' + AYLAR[a.getMonth()] + ' ' + a.getFullYear();
        if (a.getFullYear() !== b.getFullYear())
            return a.getDate() + ' ' + KISA[a.getMonth()] + ' ' + a.getFullYear() + ' – ' + b.getDate() + ' ' + KISA[b.getMonth()] + ' ' + b.getFullYear();
        if (a.getMonth() !== b.getMonth())
            return a.getDate() + ' ' + KISA[a.getMonth()] + ' – ' + b.getDate() + ' ' + KISA[b.getMonth()] + ' ' + b.getFullYear();
        return a.getDate() + ' – ' + b.getDate() + ' ' + AYLAR[b.getMonth()] + ' ' + b.getFullYear();
    }

    function hazirlar(bugun) {
        var ayBasi = new Date(bugun.getFullYear(), bugun.getMonth(), 1);
        var gecenAyBasi = new Date(bugun.getFullYear(), bugun.getMonth() - 1, 1);
        return [
            { ad: 'Bugün', bas: bugun, bit: bugun },
            { ad: 'Dün', bas: ekle(bugun, -1), bit: ekle(bugun, -1) },
            { ad: 'Son 7 gün', bas: ekle(bugun, -6), bit: bugun },
            { ad: 'Son 30 gün', bas: ekle(bugun, -29), bit: bugun },
            { ad: 'Bu ay', bas: ayBasi, bit: bugun },
            { ad: 'Geçen ay', bas: gecenAyBasi, bit: ekle(ayBasi, -1) },
            { ad: 'Bu yıl', bas: new Date(bugun.getFullYear(), 0, 1), bit: bugun },
        ];
    }

    function kur(kok) {
        var bugun = gun(new Date());
        var form = kok.closest('form');
        var girFrom = kok.querySelector('input[data-ts-from]');
        var girTo = kok.querySelector('input[data-ts-to]');
        var dugme = kok.querySelector('.ts-dugme');
        var panel = kok.querySelector('.ts-panel');
        var etiket = kok.dataset.etiket || '';
        var liste = hazirlar(bugun);

        var bas = oku(girFrom.value) || ekle(bugun, -6);
        var bit = oku(girTo.value) || bugun;
        var secBas, secBit, uzerinde, gosterilenAy;

        function hazirAdi(a, b) {
            for (var i = 0; i < liste.length; i++)
                if (ayni(liste[i].bas, a) && ayni(liste[i].bit, b)) return liste[i].ad;
            return 'Özel aralık';
        }

        function dugmeyiYaz() {
            var ad = hazirAdi(bas, bit);
            kok.querySelector('.ts-ust').textContent = etiket ? etiket + ' · ' + ad : ad;
            kok.querySelector('.ts-aralik').textContent = aralikMetni(bas, bit);
        }

        function uygula(a, b) {
            girFrom.value = iso(a);
            girTo.value = iso(b);
            kapat();
            if (form.requestSubmit) form.requestSubmit(); else form.submit();
        }

        function hazirlariCiz() {
            var kutu = panel.querySelector('.ts-hazir');
            kutu.innerHTML = '';
            var secili = hazirAdi(bas, bit);
            liste.forEach(function (h) {
                var b = document.createElement('button');
                b.type = 'button';
                b.className = 'ts-hazir-btn' + (h.ad === secili ? ' active' : '');
                b.textContent = h.ad;
                b.addEventListener('click', function () { uygula(h.bas, h.bit); });
                kutu.appendChild(b);
            });
            var ozel = document.createElement('span');
            ozel.className = 'ts-hazir-not';
            ozel.textContent = 'Özel aralık için takvimden iki gün seçin.';
            kutu.appendChild(ozel);
        }

        function takvimiCiz() {
            var kutu = panel.querySelector('.ts-takvim');
            kutu.innerHTML = '';
            var y = gosterilenAy.getFullYear(), m = gosterilenAy.getMonth();

            var ust = document.createElement('div');
            ust.className = 'ts-takvim-ust';
            var onceki = document.createElement('button');
            onceki.type = 'button'; onceki.className = 'ts-ok'; onceki.setAttribute('aria-label', 'Önceki ay');
            onceki.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m15 18-6-6 6-6"/></svg>';
            onceki.addEventListener('click', function () { gosterilenAy = new Date(y, m - 1, 1); takvimiCiz(); });
            var baslik = document.createElement('span');
            baslik.className = 'ts-ay';
            baslik.textContent = AYLAR[m] + ' ' + y;
            var sonraki = document.createElement('button');
            sonraki.type = 'button'; sonraki.className = 'ts-ok'; sonraki.setAttribute('aria-label', 'Sonraki ay');
            sonraki.innerHTML = '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="m9 18 6-6-6-6"/></svg>';
            sonraki.disabled = new Date(y, m + 1, 1) > bugun;
            sonraki.addEventListener('click', function () { gosterilenAy = new Date(y, m + 1, 1); takvimiCiz(); });
            ust.append(onceki, baslik, sonraki);
            kutu.appendChild(ust);

            var izgara = document.createElement('div');
            izgara.className = 'ts-izgara';
            GUNLER.forEach(function (g) {
                var s = document.createElement('span');
                s.className = 'ts-gun-adi';
                s.textContent = g;
                izgara.appendChild(s);
            });
            var bosluk = (new Date(y, m, 1).getDay() + 6) % 7;
            for (var i = 0; i < bosluk; i++) izgara.appendChild(document.createElement('span'));

            // Onizleme: ilk gun secilmis, ikinci bekleniyorsa farenin ustundeki gune kadar.
            var a = secBas, b = secBit || (secBas && uzerinde ? uzerinde : secBas);
            if (a && b && b < a) { var t = a; a = b; b = t; }

            var gunSayisi = new Date(y, m + 1, 0).getDate();
            for (var d = 1; d <= gunSayisi; d++) {
                (function (tarih) {
                    var h = document.createElement('button');
                    h.type = 'button';
                    h.className = 'ts-gun';
                    h.textContent = tarih.getDate();
                    h.setAttribute('aria-label', tarih.getDate() + ' ' + AYLAR[tarih.getMonth()] + ' ' + tarih.getFullYear());
                    if (ayni(tarih, bugun)) h.classList.add('bugun');
                    if (a && b && tarih >= a && tarih <= b) h.classList.add('aralik');
                    if (ayni(tarih, a)) h.classList.add('bas');
                    if (ayni(tarih, b)) h.classList.add('bit');
                    if (tarih > bugun) h.disabled = true;
                    h.addEventListener('click', function () { gunSec(tarih); });
                    h.addEventListener('mouseenter', function () {
                        if (secBas && !secBit) { uzerinde = tarih; takvimiCiz(); }
                    });
                    izgara.appendChild(h);
                })(new Date(y, m, d));
            }
            kutu.appendChild(izgara);
            altiYaz();
        }

        function altiYaz() {
            var a = secBas, b = secBit;
            if (a && b && b < a) { var t = a; a = b; b = t; }
            panel.querySelector('.ts-alt-bas').textContent = a ? noktali(a) : 'gg.aa.yyyy';
            panel.querySelector('.ts-alt-bit').textContent = b ? noktali(b) : 'gg.aa.yyyy';
            var gunN = a && b ? Math.round((b - a) / 86400000) + 1 : 0;
            panel.querySelector('.ts-alt-sayi').textContent = gunN ? gunN + ' gün' : (a ? 'bitiş gününü seçin' : '');
            panel.querySelector('.ts-uygula').disabled = !(a && b);
        }

        function gunSec(tarih) {
            if (!secBas || secBit) { secBas = tarih; secBit = null; }
            else { secBit = tarih; if (secBit < secBas) { var t = secBas; secBas = secBit; secBit = t; } }
            uzerinde = null;
            takvimiCiz();
        }

        function ac() {
            secBas = bas; secBit = bit; uzerinde = null;
            gosterilenAy = new Date(bit.getFullYear(), bit.getMonth(), 1);
            hazirlariCiz();
            takvimiCiz();
            panel.hidden = false;
            dugme.setAttribute('aria-expanded', 'true');
            // Sag kenara tasiyorsa saga hizala.
            panel.classList.remove('sag');
            if (panel.getBoundingClientRect().right > window.innerWidth - 8) panel.classList.add('sag');
            var ilk = panel.querySelector('.ts-hazir-btn.active') || panel.querySelector('.ts-hazir-btn');
            if (ilk) ilk.focus();
        }

        function kapat() {
            panel.hidden = true;
            dugme.setAttribute('aria-expanded', 'false');
        }

        dugme.addEventListener('click', function () { if (panel.hidden) ac(); else kapat(); });
        panel.querySelector('.ts-vazgec').addEventListener('click', function () { kapat(); dugme.focus(); });
        panel.querySelector('.ts-uygula').addEventListener('click', function () {
            if (secBas && secBit) uygula(secBas, secBit);
        });
        document.addEventListener('mousedown', function (e) {
            if (!panel.hidden && !kok.contains(e.target)) kapat();
        });
        kok.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && !panel.hidden) { e.preventDefault(); kapat(); dugme.focus(); }
        });

        dugmeyiYaz();
    }

    function hepsi() { document.querySelectorAll('[data-ts]').forEach(kur); }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', hepsi); else hepsi();
})();
