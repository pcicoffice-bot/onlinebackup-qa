'use strict';
// SITE-010: the language menu. The choice is remembered, so the start page (I18N-020) sends this visitor here next time.
(function () {
  const s = document.getElementById('lang'); if (!s) return;
  s.addEventListener('change', () => { try { localStorage.setItem('obLang', s.value); } catch (e) { } location.href = '../' + s.value + '/'; });
})();
