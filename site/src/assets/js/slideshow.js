(function () {
  'use strict';

  function initSlideshow(root) {
    var slides = Array.prototype.slice.call(root.querySelectorAll('.slide'));
    if (slides.length === 0) return;

    slides.forEach(function (slide, i) {
      slide.classList.toggle('is-active', i === 0);
      slide.setAttribute('aria-hidden', i === 0 ? 'false' : 'true');
    });

    root.classList.add('is-ready');
    if (slides.length === 1) return;

    var interval = parseInt(root.getAttribute('data-interval'), 10);
    if (!interval || interval < 1000) interval = 5000;

    var current = 0;
    var timer = null;

    function show(next) {
      slides[current].classList.remove('is-active');
      slides[current].setAttribute('aria-hidden', 'true');
      current = (next + slides.length) % slides.length;
      slides[current].classList.add('is-active');
      slides[current].setAttribute('aria-hidden', 'false');
    }

    function start() {
      if (timer) return;
      timer = window.setInterval(function () { show(current + 1); }, interval);
    }

    function stop() {
      window.clearInterval(timer);
      timer = null;
    }

    var reduced = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)');
    if (reduced && reduced.matches) return;

    root.addEventListener('mouseenter', stop);
    root.addEventListener('mouseleave', start);
    document.addEventListener('visibilitychange', function () {
      if (document.hidden) { stop(); } else { start(); }
    });

    start();
  }

  function init() {
    Array.prototype.forEach.call(document.querySelectorAll('.slideshow'), initSlideshow);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
