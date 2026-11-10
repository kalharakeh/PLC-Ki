// Login page. On the very first start it creates the administrator instead.
(() => {
  const $ = id => document.getElementById(id);
  const next = new URLSearchParams(location.search).get('next');
  // A scanned QR label opens /#/station/...: the redirect to this page keeps that part, so go back to it after login
  const target = next && next.startsWith('/') && !next.startsWith('//') ? next : '/' + (location.hash.startsWith('#/') ? location.hash : '');
  let setup = false;

  function show(msg) {
    $('error').textContent = msg;
    $('error').classList.toggle('hidden', !msg);
  }

  async function init() {
    const st = await fetch('/api/auth/state').then(r => r.json());
    // The language (review 49): this browser's choice (kept by the main page), else the plant's
    let own = null; try { own = localStorage.getItem('plcki.language'); } catch (e) { /* private window */ }
    I18N.setLang(own || st.language || 'en');
    I18N.dom(document.body);
    document.title = I18N.t('PLC-Ki login');
    if (st.user) { location.replace(target); return; }
    if (st.setupNeeded) {
      setup = true;
      $('title').textContent = I18N.t('Create the administrator');
      $('intro').classList.remove('hidden');
      $('repeat-field').classList.remove('hidden');
      $('password').autocomplete = 'new-password';
      $('submit').textContent = I18N.t('Create and log in');
      if (st.setupAllowed) {
        $('intro').textContent = I18N.t('First start. This login can change the settings and add other users. Keep the password safe: it cannot be recovered.');
      } else {
        $('intro').textContent = I18N.t('PLC-Ki has no users yet. Open this page on the PLC-Ki PC itself (http://localhost) to create the first login.');
        $('form').querySelectorAll('input,button').forEach(e => e.disabled = true);
      }
    }
  }

  $('form').addEventListener('submit', async e => {
    e.preventDefault();
    show('');
    const name = $('name').value.trim(), password = $('password').value;
    if (setup && password !== $('repeat').value) { show(I18N.t('The two passwords are different.')); return; }
    $('submit').disabled = true;
    try {
      const r = await fetch(setup ? '/api/auth/setup' : '/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, password }),
      });
      if (r.ok) { location.replace(setup ? '/#/setup/start' : target); return; }
      const body = await r.json().catch(() => ({}));
      show(I18N.srv(body.error) || I18N.t('Failed ({status}).', { status: r.status }));
    } catch {
      show(I18N.t('PLC-Ki does not answer. Is it running?'));
    } finally {
      $('submit').disabled = false;
    }
  });

  init().catch(() => show(I18N.t('PLC-Ki does not answer. Is it running?')));
})();
