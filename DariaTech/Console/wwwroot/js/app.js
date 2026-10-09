const themeButton = document.getElementById('theme');
const setTheme = dark => { document.body.classList.toggle('dark', dark); themeButton?.setAttribute('aria-pressed', String(dark)); };
let stored = null;
try { stored = localStorage.getItem('theme'); } catch { /* storage may be blocked */ }
setTheme(stored === 'dark');
themeButton?.addEventListener('click', () => {
 const dark = !document.body.classList.contains('dark');
 setTheme(dark);
 try { localStorage.setItem('theme', dark ? 'dark' : 'light'); } catch { /* storage may be blocked */ }
});
// "/" focuses the global search unless the user is typing in a field.
document.addEventListener('keydown', e => {
 if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey) return;
 const t = e.target;
 if (t instanceof HTMLElement && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
 const search = document.getElementById('global-search');
 if (search) { e.preventDefault(); search.focus(); }
});
// Keep the one-time code field numeric while typing or pasting.
document.querySelectorAll('input.otp').forEach(input => input.addEventListener('input', () => { input.value = input.value.replace(/\D/g, '').slice(0, 6); }));
// Destination picker: show and enable only the panel of the selected storage destination.
document.querySelectorAll('[data-dest-picker]').forEach(picker => {
 const show = () => document.querySelectorAll('.dest-panel').forEach(panel => {
  const active = panel.dataset.dest === picker.value;
  panel.classList.toggle('active', active);
  panel.disabled = !active;
 });
 picker.addEventListener('change', show);
});
// Backup editor: saved destination or a custom one; the hidden custom picker must not block submission.
document.querySelectorAll('[data-destination-mode]').forEach(radio => radio.addEventListener('change', () => {
 const custom = document.querySelector('[data-custom-destination]');
 if (!custom) return;
 const on = document.querySelector('[data-destination-mode][value=custom]').checked;
 custom.hidden = !on; custom.disabled = !on;
}));
// Remote helpers in the backup editor: signed device queries, polled until the agent answers.
(() => {
 const form = document.querySelector('[data-backup-form]');
 const token = () => form?.querySelector('input[name=__RequestVerificationToken]')?.value ?? '';
 const deviceId = () => form?.querySelector('input[name=DeviceId]')?.value ?? '';
 const poll = async (id, onDone, onWait) => {
  for (let i = 0; i < 200; i++) {
   const r = await fetch(`/BackupEdit?handler=Query&deviceId=${encodeURIComponent(deviceId())}&id=${encodeURIComponent(id)}`, { headers: { Accept: 'application/json' } });
   if (!r.ok) { onDone({ done: true, ok: false, message: 'Abfrage fehlgeschlagen.' }); return; }
   const result = await r.json();
   if (result.done) { onDone(result); return; }
   onWait?.(i);
   await new Promise(res => setTimeout(res, 1500));
  }
  onDone({ done: true, ok: false, message: 'Zeitüberschreitung.' });
 };
 const post = async (handler, data) => {
  data.set('__RequestVerificationToken', token());
  const r = await fetch(`/BackupEdit?handler=${handler}`, { method: 'POST', body: data, headers: { Accept: 'application/json' } });
  return r.ok ? r.json() : { error: 'Anfrage abgelehnt.' };
 };
 const waitText = i => 'Warte auf das Gerät' + '.'.repeat(1 + i % 3);

 const browser = document.querySelector('[data-browser]');
 if (browser) {
  const panel = browser.querySelector('[data-browser-panel]'), list = browser.querySelector('[data-browser-list]');
  const status = browser.querySelector('[data-browser-status]'), current = browser.querySelector('[data-browser-path]');
  const sources = document.querySelector('[data-source-list]');
  const add = path => {
   const lines = sources.value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
   const clean = path.length > 3 ? path.replace(/[\\/]+$/, '') : path;
   if (!lines.includes(clean)) lines.push(clean);
   sources.value = lines.join('\n');
  };
  const open = async path => {
   panel.hidden = false; list.replaceChildren(); current.textContent = path ?? ''; status.textContent = waitText(0);
   const data = new FormData(); data.set('deviceId', deviceId()); if (path) data.set('path', path);
   const started = await post('Browse', data);
   status.classList.remove('bad');
   if (started.error) { status.textContent = started.error; status.classList.add('bad'); return; }
   await poll(started.id, result => {
    if (!result.ok) { status.textContent = result.message; status.classList.add('bad'); return; }
    status.textContent = result.folders.length === 0 ? 'Keine Unterordner.' : (result.truncated ? 'Nur die ersten Ordner werden angezeigt.' : '');
    for (const folder of result.folders) {
     const li = document.createElement('li');
     const name = folder.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || folder;
     const openButton = document.createElement('button'); openButton.type = 'button'; openButton.className = 'link-button'; openButton.textContent = name; openButton.title = folder;
     openButton.addEventListener('click', () => open(folder));
     const addButton = document.createElement('button'); addButton.type = 'button'; addButton.className = 'button secondary small'; addButton.textContent = 'Hinzufügen';
     addButton.addEventListener('click', () => { add(folder); addButton.textContent = 'Hinzugefügt'; addButton.disabled = true; });
     li.append(openButton, addButton); list.append(li);
    }
   }, i => { status.textContent = waitText(i); });
  };
  browser.querySelectorAll('[data-browse-root]').forEach(b => b.addEventListener('click', () => open(null)));
  browser.querySelectorAll('[data-browse-path]').forEach(b => b.addEventListener('click', () => open(b.dataset.browsePath)));
 }

 const secret = document.querySelector('[data-passphrase]');
 if (secret) {
  const input = secret.querySelector('[data-passphrase-input]'), status = secret.querySelector('[data-passphrase-status]'), copy = secret.querySelector('[data-passphrase-copy]');
  const show = value => { input.value = value; input.type = 'text'; copy.hidden = false; };
  secret.querySelector('[data-passphrase-generate]')?.addEventListener('click', () => {
   // 30 characters from an unambiguous alphabet (about 175 bits), grouped for reading aloud.
   const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789';
   const bytes = crypto.getRandomValues(new Uint32Array(30));
   const chars = Array.from(bytes, b => alphabet[b % alphabet.length]).join('');
   show(chars.match(/.{1,6}/g).join('-'));
   status.className = 'dest-test-status good'; status.textContent = 'Schlüssel erzeugt. Er wird mit dem Job verschlüsselt in der Console gespeichert – zusätzlich bitte im Passwort-Manager ablegen.';
  });
  secret.querySelector('[data-passphrase-reveal]')?.addEventListener('click', async () => {
   const result = await post('Passphrase', new FormData(form));
   if (result.error || !result.passphrase) { status.className = 'dest-test-status bad'; status.textContent = result.error ?? 'Die Passphrase konnte nicht geladen werden.'; return; }
   show(result.passphrase); status.className = 'dest-test-status'; status.textContent = '';
  });
  copy.addEventListener('click', async () => {
   try { await navigator.clipboard.writeText(input.value); copy.textContent = 'Kopiert'; } catch { input.select(); }
  });
 }

 const test = document.querySelector('[data-dest-test]');
 if (test) {
  const status = test.querySelector('[data-test-status]'), create = test.querySelector('[data-test-create]'), hostKey = test.querySelector('[data-use-hostkey]');
  let reportedKey = null;
  const run = async createFolder => {
   create.hidden = true; hostKey.hidden = true; status.className = 'dest-test-status'; status.textContent = waitText(0);
   const data = new FormData(form); data.set('createFolder', createFolder ? 'true' : 'false');
   const started = await post('TestDestination', data);
   if (started.error) { status.textContent = started.error; status.classList.add('bad'); return; }
   await poll(started.id, result => {
    status.textContent = result.message + (result.detail ? ` (${result.detail})` : '');
    status.classList.add(result.ok ? 'good' : 'bad');
    create.hidden = result.code !== 'DestinationFolderMissing';
    reportedKey = result.code === 'DestinationHostKeyMismatch' ? result.detail : null;
    hostKey.hidden = !reportedKey;
   }, i => { status.textContent = waitText(i); });
  };
  test.querySelector('[data-test-destination]').addEventListener('click', () => run(false));
  create.addEventListener('click', () => run(true));
  hostKey.addEventListener('click', () => {
   const field = document.querySelector('.dest-panel.active input[name$=".opt.ssh-fingerprint"]');
   if (field && reportedKey) { field.value = reportedKey; hostKey.hidden = true; status.textContent = 'Host-Key übernommen. Bitte erneut testen.'; }
  });
 }
})();
// Copy the generated install command.
document.querySelectorAll('[data-copy]').forEach(button => button.addEventListener('click', async () => {
 const source = button.parentElement?.querySelector('[data-copy-source]');
 if (!source) return;
 try { await navigator.clipboard.writeText(source.textContent ?? ''); button.textContent = 'Kopiert'; }
 catch { getSelection()?.selectAllChildren(source); button.textContent = 'Markiert – mit Strg+C kopieren'; }
}));
// Restore guide: select all files, and refresh while the device still has to answer.
document.querySelectorAll('[data-select-all]').forEach(all => all.addEventListener('change', () => {
 all.closest('form')?.querySelectorAll('[data-file]').forEach(box => { box.checked = all.checked; });
}));
if (document.querySelector('[data-auto-refresh]')) setTimeout(() => location.reload(), 4000);
