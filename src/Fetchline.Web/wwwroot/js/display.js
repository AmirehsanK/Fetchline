// The switches on the monitor's chin. They are ordinary buttons that change the display's
// settings; aria-pressed says which are in, for a screen reader and for the stylesheet alike.
(function () {
    'use strict';

    var display = window.fetchlineDisplay;
    var keys = document.querySelectorAll('.switches .key');

    function show(settings) {
        keys.forEach(function (key) {
            var pressed = key.hasAttribute('data-plain') ? settings.plain
                : key.dataset.language ? key.dataset.language === settings.language
                : key.dataset.phosphor === settings.phosphor;
            key.setAttribute('aria-pressed', pressed ? 'true' : 'false');
        });
    }

    keys.forEach(function (key) {
        key.addEventListener('click', function () {
            var settings = display.read();
            if (key.hasAttribute('data-plain')) {
                settings.plain = !settings.plain;
            } else if (key.dataset.language) {
                settings.language = key.dataset.language;
            } else {
                settings.phosphor = key.dataset.phosphor;
            }

            display.save(settings);
            show(settings);

            // What the tube shows is drawn by the application, which has to be told.
            if (key.dataset.language) {
                window.dispatchEvent(new CustomEvent('fetchline-language', { detail: settings.language }));
            }
        });
    });

    show(display.read());
})();
