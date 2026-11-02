// Settings page (administrators only).
(() => {
  const $ = id => document.getElementById(id);
  const fields = ['source', 'address', 'security', 'username', 'pollMs', 'timeoutMs', 'model', 'snapshotFile',
    'historyMinutes', 'askEnabled', 'askEffort', 'port', 'allowRemote', 'publicView'];
  let me = null;

  async function api(method, url, body) {
    const r = await fetch(url, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : {},
      body: body ? JSON.stringify(body) : undefined,
    });
    const data = r.status === 204 ? null : await r.json().catch(() => null);
    if (!r.ok) throw new Error((data && data.error) || `Failed (${r.status})`);
    return data;
  }

  function say(el, text, kind) {
    el.textContent = text;
    el.className = (el.classList.contains('hint') ? 'hint ' : 'small ') + (kind === 'ok' ? 'ok' : kind === 'err' ? 'err' : '');
    el.style.color = kind === 'ok' ? 'var(--ok)' : kind === 'err' ? 'var(--fault)' : '';
  }

  function options(select, list, current) {
    select.innerHTML = '';
    const seen = new Set();
    for (const f of list) {
      const o = new Option(f.label + (f.uploaded ? ' (uploaded)' : ''), f.path);
      select.add(o);
      seen.add(f.path);
    }
    if (current && !seen.has(current)) select.add(new Option(current, current));
    select.value = current || '';
  }

  function showInfo(info) {
    $('data-dir').textContent = info.dataDir;
    $('where').textContent = info.service ? 'Running as a Windows service' : 'Started by hand (not as a service)';
    const keyText = { settings: 'A key is saved here.', environment: 'Using the key from the ANTHROPIC_API_KEY environment variable. A key entered here replaces it.', none: 'No key yet. The question box stays off until you add one.' };
    $('key-state').textContent = keyText[info.apiKey];
    $('clear-key-wrap').classList.toggle('hidden', info.apiKey !== 'settings');
    const pwText = { settings: 'A password is saved here.', environment: 'Set by the PLCKI_PLC_PASSWORD environment variable, which wins over this field.', none: 'None. Leave empty if the CPU has no access protection.' };
    $('plc-pw-state').textContent = pwText[info.plcPassword];
    $('clear-plc-pw-wrap').classList.toggle('hidden', info.plcPassword !== 'settings');
    const note = $('restart-note');
    if (info.restartNeeded) {
      note.textContent = info.service
        ? 'Saved. Restart PLC-Ki to use the new settings (the page is back after a few seconds).'
        : 'Saved. Stop PLC-Ki (Ctrl+C in its window) and start it again to use the new settings.';
      note.classList.remove('hidden');
      $('restart').classList.toggle('hidden', !info.service);
    }
  }

  function fill(s) {
    for (const f of fields) {
      const el = $(f);
      if (el.type === 'checkbox') el.checked = !!s[f]; else if (el.tagName !== 'SELECT' || el.options.length) el.value = s[f];
    }
    $('port-echo').textContent = s.port;
    toggleSource();
  }

  function read() {
    const s = {};
    for (const f of fields) {
      const el = $(f);
      s[f] = el.type === 'checkbox' ? el.checked : el.type === 'number' ? Number(el.value) : el.value;
    }
    s.plcPassword = $('plcPassword').value || null;
    s.clearPlcPassword = $('clearPlcPassword').checked;
    s.apiKey = $('apiKey').value.trim() || null;
    s.clearApiKey = $('clearApiKey').checked;
    return s;
  }

  function toggleSource() {
    const src = $('source').value;
    document.querySelectorAll('[data-when]').forEach(el => el.classList.toggle('hidden', el.dataset.when !== src));
  }

  async function load() {
    const d = await api('GET', '/api/admin/settings');
    options($('model'), d.models, d.settings.model);
    options($('snapshotFile'), d.snapshots, d.settings.snapshotFile);
    fill(d.settings);
    showInfo(d.info);
  }

  async function loadUsers() {
    const st = await api('GET', '/api/auth/state');
    me = st.user && st.user.name;
    const users = await api('GET', '/api/admin/users');
    const tb = $('users');
    tb.innerHTML = '';
    for (const u of users) {
      const tr = document.createElement('tr');
      const name = document.createElement('td');
      name.textContent = u.name + (u.name === me ? ' (you)' : '');
      const role = document.createElement('td');
      const sel = document.createElement('select');
      sel.add(new Option('Operator', 'operator'));
      sel.add(new Option('Administrator', 'admin'));
      sel.value = u.role;
      sel.disabled = u.name === me;
      sel.setAttribute('aria-label', 'Role of ' + u.name);
      sel.onchange = () => act(() => api('PUT', `/api/admin/users/${encodeURIComponent(u.name)}/role`, { role: sel.value }), `${u.name} is now ${sel.value === 'admin' ? 'an administrator' : 'an operator'}.`);
      role.append(sel);
      const since = document.createElement('td');
      since.className = 'muted small';
      since.textContent = new Date(u.created).toLocaleDateString();
      const actions = document.createElement('td');
      actions.className = 'actions';
      const reset = document.createElement('button');
      reset.type = 'button'; reset.className = 'linkbtn'; reset.textContent = 'New password';
      reset.onclick = () => {
        const pw = prompt(`New password for ${u.name} (at least 8 characters):`);
        if (pw) act(() => api('PUT', `/api/admin/users/${encodeURIComponent(u.name)}/password`, { password: pw }), `Password of ${u.name} changed.`);
      };
      actions.append(reset);
      if (u.name !== me) {
        const del = document.createElement('button');
        del.type = 'button'; del.className = 'linkbtn'; del.textContent = 'Delete';
        del.style.marginLeft = '12px'; del.style.color = 'var(--fault)';
        del.onclick = () => {
          if (confirm(`Delete the user ${u.name}?`)) act(() => api('DELETE', `/api/admin/users/${encodeURIComponent(u.name)}`), `${u.name} deleted.`);
        };
        actions.append(del);
      }
      tr.append(name, role, since, actions);
      tb.append(tr);
    }
  }

  async function act(fn, okText) {
    try {
      await fn();
      say($('user-msg'), okText, 'ok');
    } catch (e) {
      say($('user-msg'), e.message, 'err');
    }
    await loadUsers();
  }

  $('source').onchange = toggleSource;
  $('port').oninput = () => { $('port-echo').textContent = $('port').value; };

  $('form').addEventListener('submit', async e => {
    e.preventDefault();
    $('save').disabled = true;
    try {
      const d = await api('PUT', '/api/admin/settings', read());
      $('plcPassword').value = ''; $('apiKey').value = '';
      $('clearPlcPassword').checked = false; $('clearApiKey').checked = false;
      fill(d.settings);
      showInfo(d.info);
      say($('save-msg'), 'Saved.', 'ok');
    } catch (err) {
      say($('save-msg'), err.message, 'err');
    } finally {
      $('save').disabled = false;
    }
  });

  $('test').onclick = async () => {
    say($('test-result'), 'Testing…');
    try {
      const r = await api('POST', '/api/admin/test-plc', { address: $('address').value });
      say($('test-result'), r.message, r.ok ? 'ok' : 'err');
    } catch (e) {
      say($('test-result'), e.message, 'err');
    }
  };

  $('upload').onclick = async () => {
    const file = $('model-file').files[0];
    if (!file) { say($('upload-result'), 'Choose a .json file first.', 'err'); return; }
    const fd = new FormData();
    fd.append('file', file);
    say($('upload-result'), 'Checking the file…');
    try {
      const r = await fetch('/api/admin/models', { method: 'POST', body: fd });
      const d = await r.json().catch(() => ({}));
      if (!r.ok) throw new Error(d.error || `Failed (${r.status})`);
      const keep = read();
      await load();
      fill({ ...keep, model: d.path });
      $('model').value = d.path;
      say($('upload-result'), `"${d.name}" with ${d.tags} tags is ready. Press Save to use it.`, 'ok');
    } catch (e) {
      say($('upload-result'), e.message, 'err');
    }
  };

  $('restart').onclick = async () => {
    $('restart').disabled = true;
    try {
      await api('POST', '/api/admin/restart');
    } catch (e) {
      say($('save-msg'), e.message, 'err');
      $('restart').disabled = false;
      return;
    }
    say($('save-msg'), 'Restarting…');
    const started = Date.now();
    const wait = async () => {
      try {
        const r = await fetch('/api/auth/state', { cache: 'no-store' });
        if (r.ok && Date.now() - started > 2500) { location.reload(); return; }
      } catch { /* not back yet */ }
      if (Date.now() - started > 60000) { say($('save-msg'), 'PLC-Ki did not come back. Check the PLC-Ki service in Windows Services.', 'err'); return; }
      setTimeout(wait, 1000);
    };
    setTimeout(wait, 1500);
  };

  $('add-user').addEventListener('submit', async e => {
    e.preventDefault();
    const name = $('new-name').value.trim();
    await act(() => api('POST', '/api/admin/users', { name, password: $('new-password').value, role: $('new-role').value }), `${name} added.`);
    if ($('user-msg').style.color === 'var(--ok)') { $('new-name').value = ''; $('new-password').value = ''; }
  });

  $('my-password').addEventListener('submit', async e => {
    e.preventDefault();
    try {
      await api('POST', '/api/auth/password', { current: $('cur-pw').value, password: $('new-pw').value });
      $('cur-pw').value = ''; $('new-pw').value = '';
      say($('pw-msg'), 'Password changed. Other sessions of yours are logged out.', 'ok');
    } catch (err) {
      say($('pw-msg'), err.message, 'err');
    }
  });

  load().catch(e => say($('save-msg'), e.message, 'err'));
  loadUsers().catch(e => say($('user-msg'), e.message, 'err'));
})();
