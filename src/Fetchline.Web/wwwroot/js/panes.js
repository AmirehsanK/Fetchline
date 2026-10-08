// The few things the panes need that markup cannot say.

window.fetchlineEditor = {
    // Puts the caret on a stretch of the text.
    select: function (textarea, start, end) {
        textarea.focus();
        textarea.setSelectionRange(start, Math.max(start, end));
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
