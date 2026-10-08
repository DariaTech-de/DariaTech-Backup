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
