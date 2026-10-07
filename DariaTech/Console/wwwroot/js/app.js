const themeButton = document.getElementById('theme');
if (localStorage.getItem('theme') === 'dark') document.body.classList.add('dark');
themeButton?.addEventListener('click', () => { document.body.classList.toggle('dark'); localStorage.setItem('theme', document.body.classList.contains('dark') ? 'dark' : 'light'); });
