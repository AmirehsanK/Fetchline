// The display's two settings: the colour of the phosphor, and whether the tube's effects are on.
// They live on the <html> element as data-phosphor and data-plain, where the stylesheet reads
// them, and in localStorage so that they are still there next time. This file runs in the head,
// before anything is drawn; display.js wires up the switches once they exist.
(function () {
    'use strict';

    var key = 'fetchline.display';
    var phosphors = ['green', 'amber', 'white'];
    var root = document.documentElement;

    function read() {
        try {
            var saved = JSON.parse(localStorage.getItem(key) || '{}');
            return {
                phosphor: phosphors.indexOf(saved.phosphor) >= 0 ? saved.phosphor : 'green',
                plain: saved.plain === true,
            };
        } catch (e) {
            // A browser that refuses storage still gets a working display, just not a remembered one.
            return { phosphor: 'green', plain: false };
        }
    }

    function apply(settings) {
        root.dataset.phosphor = settings.phosphor;
        if (settings.plain) {
            root.dataset.plain = '';
        } else {
            delete root.dataset.plain;
        }
    }

    function save(settings) {
        apply(settings);
        try {
            localStorage.setItem(key, JSON.stringify(settings));
        } catch (e) {
            // Nothing to do: the setting holds for this visit.
        }
    }

    window.fetchlineDisplay = { read: read, save: save, phosphors: phosphors };
    apply(read());
})();
