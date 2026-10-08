// The few things the panes need that markup cannot say.

window.fetchlineEditor = {
    // Puts the caret on a stretch of the text.
    select: function (textarea, start, end) {
        textarea.focus();
        textarea.setSelectionRange(start, Math.max(start, end));
    },
};

window.fetchlineKeys = {
    // The function keys named on the soft keys are real keys. They are taken only when pressed
    // alone, so Ctrl+F5 still reloads the page; and only stepping repeats when a key is held.
    listen: function (listener) {
        var repeats = { F9: true, F10: true };
        var keys = { F5: true, F6: true, F7: true, F8: true, F9: true, F10: true };

        window.addEventListener('keydown', function (event) {
            if (!keys[event.key] || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) {
                return;
            }

            event.preventDefault();
            if (!event.repeat || repeats[event.key]) {
                listener.invokeMethodAsync('Pressed', event.key);
            }
        });
    },
};

window.fetchlineLog = {
    // Keeps the lines of the cycle on screen in view: the last of them, or failing that the
    // last line before them. Only the log itself is scrolled, never the page around it.
    follow: function (scroller) {
        if (!scroller) {
            return;
        }

        var lines = scroller.querySelectorAll('.log-now');
        if (!lines.length) {
            lines = scroller.querySelectorAll('.log-line:not(.log-ahead)');
        }

        if (!lines.length) {
            scroller.scrollTop = 0;
            return;
        }

        var frame = scroller.getBoundingClientRect();
        var line = lines[lines.length - 1].getBoundingClientRect();
        if (line.bottom > frame.bottom) {
            scroller.scrollTop += line.bottom - frame.bottom;
        } else if (line.top < frame.top) {
            scroller.scrollTop -= frame.top - line.top;
        }
    },
};

window.fetchlineStaircase = (function () {
    'use strict';

    var observers = new WeakMap();
    var ruler = document.createElement('canvas').getContext('2d');

    // How many characters of the element's own face fit across it.
    function characters(element) {
        var style = getComputedStyle(element);
        ruler.font = style.fontStyle + ' ' + style.fontWeight + ' ' + style.fontSize + ' ' + style.fontFamily;
        var one = ruler.measureText('0000000000').width / 10;
        return one > 0 ? Math.floor(element.clientWidth / one) : 0;
    }

    return {
        // Tells the diagram how wide it is, in characters, now and whenever that changes.
        watch: function (scroller, listener) {
            var observer = new ResizeObserver(function () {
                listener.invokeMethodAsync('Resized', characters(scroller));
            });
            observer.observe(scroller);
            observers.set(scroller, observer);
        },

        unwatch: function (scroller) {
            var observer = scroller && observers.get(scroller);
            if (observer) {
                observer.disconnect();
                observers.delete(scroller);
            }
        },

        // The cycle on screen is the diagram's last column and the newest instructions are its
        // last rows: after a step, that corner is what should be in view.
        follow: function (scroller) {
            if (scroller) {
                scroller.scrollLeft = scroller.scrollWidth;
                scroller.scrollTop = scroller.scrollHeight;
            }
        },
    };
})();
