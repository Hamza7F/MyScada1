// Theme management for ALAM IOT SCADA
// Handles instant theme initialization without flicker, localStorage persistence,
// and smooth dynamic switching between Light and Dark themes.

window.theme = {
    initTheme: function () {
        var saved = localStorage.getItem('scada_theme');
        var isDark = false;
        if (saved === 'dark') {
            isDark = true;
        } else if (saved === 'light') {
            isDark = false;
        } else if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
            isDark = true;
        }

        document.documentElement.setAttribute('data-theme', isDark ? 'dark' : 'light');
        if (document.body) {
            document.body.classList.toggle('dark-theme', isDark);
        }
        return isDark;
    },

    setTheme: function (isDark) {
        var mode = isDark ? 'dark' : 'light';
        document.documentElement.setAttribute('data-theme', mode);
        localStorage.setItem('scada_theme', mode);
        if (document.body) {
            document.body.classList.toggle('dark-theme', isDark);
        }
    }
};

// Immediate execution in <head> to prevent theme flash:
(function () {
    try {
        var saved = localStorage.getItem('scada_theme');
        var isDark = false;
        if (saved === 'dark') {
            isDark = true;
        } else if (saved === 'light') {
            isDark = false;
        } else if (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches) {
            isDark = true;
        }
        document.documentElement.setAttribute('data-theme', isDark ? 'dark' : 'light');
    } catch (e) { }
})();
