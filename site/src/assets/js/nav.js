(function () {
  var toggle = document.querySelector('.nav-toggle');
  var menu = document.getElementById('site-menu');
  var backdrop = document.querySelector('.nav-backdrop');
  if (!toggle || !menu) return;

  var mq = window.matchMedia('(max-width: 860px)');

  function setOpen(open) {
    document.body.classList.toggle('nav-open', open);
    toggle.setAttribute('aria-expanded', String(open));
    toggle.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
    if (backdrop) backdrop.hidden = !open;
  }

  toggle.addEventListener('click', function () {
    var open = !document.body.classList.contains('nav-open');
    setOpen(open);
    if (open) {
      var first = menu.querySelector('a');
      if (first) first.focus();
    }
  });

  if (backdrop) backdrop.addEventListener('click', function () { setOpen(false); });

  menu.addEventListener('click', function (e) {
    if (e.target.closest('a')) setOpen(false);
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && document.body.classList.contains('nav-open')) {
      setOpen(false);
      toggle.focus();
    }
  });

  // Keep state sane when the viewport crosses the breakpoint.
  var onChange = function () { if (!mq.matches) setOpen(false); };
  if (mq.addEventListener) mq.addEventListener('change', onChange);
  else if (mq.addListener) mq.addListener(onChange);

  setOpen(false);
})();
