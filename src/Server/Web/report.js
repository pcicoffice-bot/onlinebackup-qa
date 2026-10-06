'use strict';
// AI-080: the report's print button (no inline script under the site's CSP)
document.getElementById('print').addEventListener('click', () => window.print());
