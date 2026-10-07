// Presentation only: existing Duplicati login/authentication behavior is retained.
document.addEventListener('DOMContentLoaded', function () {
    var brand = window.DariaTechBranding;
    if (!brand) return;
    document.title = brand.productName + ' – Anmelden';
    var panel = document.getElementById('login');
    var logo = document.createElement('img');
    logo.src = brand.companyLogo;
    logo.alt = brand.companyName;
    logo.className = 'company-logo';
    panel.insertBefore(logo, panel.firstChild);
    panel.querySelector('h2').textContent = brand.productName;
    document.querySelector('label[for="login-password"]').textContent = 'Lokales Backup-Passwort';
    document.getElementById('login-password').placeholder = 'Passwort';
    document.getElementById('login-password').autocomplete = 'current-password';
    document.getElementById('login-button').value = 'Anmelden';
    panel.querySelector('.helplink').textContent = 'Passwort vergessen?';
    var note = document.createElement('p');
    note.className = 'device-note';
    note.textContent = 'Backup-Jobs und Wiederherstellung auf diesem Gerät. Verwenden Sie das lokale Passwort aus der Agent-Installation.';
    panel.insertBefore(note, document.getElementById('login-form'));
    var support = document.createElement('a');
    support.href = 'mailto:' + brand.supportEmail;
    support.textContent = brand.supportEmail;
    support.className = 'support';
    panel.appendChild(support);
});
