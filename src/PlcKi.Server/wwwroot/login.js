// Login page. On the very first start it creates the administrator instead.
(() => {
  const $ = id => document.getElementById(id);
  const next = new URLSearchParams(location.search).get('next');
  const target = next && next.startsWith('/') && !next.startsWith('//') ? next : '/';
  let setup = false;

  function show(msg) {
    $('error').textContent = msg;
    $('error').classList.toggle('hidden', !msg);
  }

  async function init() {
    const st = await fetch('/api/auth/state').then(r => r.json());
    if (st.user) { location.replace(target); return; }
    if (st.setupNeeded) {
      setup = true;
      $('title').textContent = 'Create the administrator';
      $('intro').classList.remove('hidden');
      $('repeat-field').classList.remove('hidden');
      $('password').autocomplete = 'new-password';
      $('submit').textContent = 'Create and log in';
      if (st.setupAllowed) {
        $('intro').textContent = 'First start. This login can change the settings and add other users. Keep the password safe: it cannot be recovered.';
      } else {
        $('intro').textContent = 'PLC-Ki has no users yet. Open this page on the PLC-Ki PC itself (http://localhost) to create the first login.';
        $('form').querySelectorAll('input,button').forEach(e => e.disabled = true);
      }
    }
  }

  $('form').addEventListener('submit', async e => {
    e.preventDefault();
    show('');
    const name = $('name').value.trim(), password = $('password').value;
    if (setup && password !== $('repeat').value) { show('The two passwords are different.'); return; }
    $('submit').disabled = true;
    try {
      const r = await fetch(setup ? '/api/auth/setup' : '/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, password }),
      });
      if (r.ok) { location.replace(setup ? '/#/setup/connections' : target); return; }
      const body = await r.json().catch(() => ({}));
      show(body.error || `Failed (${r.status}).`);
    } catch {
      show('PLC-Ki does not answer. Is it running?');
    } finally {
      $('submit').disabled = false;
    }
  });

  init().catch(() => show('PLC-Ki does not answer. Is it running?'));
})();
