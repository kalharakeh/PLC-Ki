// Adds "user · Settings · Log out" to the page header, and sends the browser to the login page
// when the session has ended (any API call answering 401).
(() => {
  const realFetch = window.fetch.bind(window);
  window.fetch = async (...args) => {
    const r = await realFetch(...args);
    const url = String(args[0] && args[0].url || args[0]);
    if (r.status === 401 && !url.includes('/api/auth/')) {
      location.href = '/login.html?next=' + encodeURIComponent(location.pathname);
    }
    return r;
  };

  async function menu() {
    const st = await realFetch('/api/auth/state').then(r => r.json()).catch(() => null);
    const header = document.querySelector('header.top');
    if (!st || !header) return;
    const box = document.createElement('div');
    box.className = 'user-menu';
    if (st.user) {
      const who = document.createElement('span');
      who.className = 'who';
      who.textContent = st.user.name + (st.user.role === 'admin' ? ' (admin)' : '');
      box.append(who);
      if (st.user.role === 'admin' && location.pathname !== '/settings.html') {
        const a = document.createElement('a');
        a.href = '/settings.html';
        a.textContent = 'Settings';
        box.append(a);
      }
      if (location.pathname === '/settings.html') {
        const a = document.createElement('a');
        a.href = '/';
        a.textContent = 'Back to the machine';
        box.append(a);
      }
      const out = document.createElement('button');
      out.type = 'button';
      out.className = 'linkbtn';
      out.textContent = 'Log out';
      out.onclick = async () => {
        await realFetch('/api/auth/logout', { method: 'POST' });
        location.href = '/login.html';
      };
      box.append(out);
    } else {
      const a = document.createElement('a');
      a.href = '/login.html?next=' + encodeURIComponent(location.pathname);
      a.textContent = 'Log in';
      box.append(a);
    }
    header.append(box);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', menu);
  else menu();
})();
